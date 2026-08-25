using System;
using System.Text;
using static AtE.Globals;

namespace AtE {

	/// <summary>
	/// A parsed DirectDraw Surface (.dds) — the format essentially all of PoE's art is stored
	/// in, inside the bundles.
	///
	/// Only mip level 0 is kept, since that is what gets displayed. The block-compressed
	/// formats (BC1-BC7) are left compressed in <see cref="Data"/>: GPUs sample them natively,
	/// so the Windows backend uploads these bytes as-is. <see cref="TryDecodeToRgba"/> is the
	/// software fallback for backends (or callers) that need actual pixels; it covers BC1-BC5
	/// and the uncompressed layouts, plus BC7 — but not BC6H (which is HDR) or the float
	/// formats, which have to go to the GPU.
	///
	/// Layout being parsed:
	///   0    uint32  magic "DDS "
	///   4    DDS_HEADER, 124 bytes (size, flags, height, width, pitch, depth, mipCount,
	///                               reserved[11], DDS_PIXELFORMAT at +72, caps...)
	///   128  DDS_HEADER_DXT10, 20 bytes, only when the pixel format's fourCC is "DX10"
	///   then the surface data, mip 0 first.
	/// </summary>
	public sealed class DdsImage {

		private const int MagicSize = 4;
		private const int HeaderSize = 124;
		private const int Dxt10HeaderSize = 20;

		public int Width { get; private set; }
		public int Height { get; private set; }

		/// <summary>Mip levels present in the file (1 when the file has no mip chain).</summary>
		public int MipCount { get; private set; }

		public TextureFormat Format { get; private set; }

		/// <summary>Mip level 0's bytes: BC blocks for compressed formats, pixels otherwise.</summary>
		public byte[] Data { get; private set; }

		/// <summary>
		/// Bytes per row of <see cref="Data"/> — for block-compressed formats, one row of 4x4
		/// blocks (which is what D3D11 wants as its row pitch).
		/// </summary>
		public int RowPitch { get; private set; }

		/// <summary>True when the format is one of the BC block-compressed families.</summary>
		public bool IsBlockCompressed => IsBlockFormat(Format);

		/// <summary>
		/// Whether <see cref="TryDecodeToRgba"/> can produce pixels for this format — i.e. whether
		/// this image can be converted to PNG or shown by a backend that cannot sample BC blocks.
		/// False for BC6H and the float formats, which need the GPU. Lets callers offer or
		/// withhold a "save as PNG" action without decoding first.
		/// </summary>
		public bool CanDecodeToRgba {
			get {
				switch ( Format ) {
					case TextureFormat.R8G8B8A8_UNorm:
					case TextureFormat.B8G8R8A8_UNorm:
					case TextureFormat.R8_UNorm:
					case TextureFormat.BC1_UNorm:
					case TextureFormat.BC2_UNorm:
					case TextureFormat.BC3_UNorm:
					case TextureFormat.BC4_UNorm:
					case TextureFormat.BC5_UNorm:
					case TextureFormat.BC7_UNorm:
						return true;
					default:
						return false;
				}
			}
		}

		private DdsImage() { }

		/// <summary>
		/// Some of PoE's .dds entries are not images at all: they hold '*' followed by the path
		/// of the file to use instead. Follow the redirect before parsing.
		/// </summary>
		public static bool TryGetRedirect(byte[] bytes, out string targetPath) {
			targetPath = null;
			if ( bytes == null || bytes.Length < 2 || bytes[0] != (byte)'*' ) return false;
			int end = 1;
			while ( end < bytes.Length && bytes[end] != 0 && bytes[end] != (byte)'\n' && bytes[end] != (byte)'\r' ) end++;
			targetPath = Encoding.UTF8.GetString(bytes, 1, end - 1).Trim();
			return targetPath.Length > 0;
		}

		/// <summary>Parse a .dds file. Returns false with a reason rather than throwing.</summary>
		public static bool TryParse(byte[] bytes, out DdsImage image, out string error) {
			image = null;
			error = null;
			// Redirects first: they are only a few dozen bytes, so a length check would
			// misreport them as truncated images.
			if ( TryGetRedirect(bytes, out string redirect) ) {
				error = $"this file is a redirect to \"{redirect}\", not image data";
				return false;
			}
			if ( bytes == null || bytes.Length < MagicSize + HeaderSize ) {
				error = $"too short to be a DDS ({bytes?.Length ?? 0} bytes)";
				return false;
			}
			if ( bytes[0] != 'D' || bytes[1] != 'D' || bytes[2] != 'S' || bytes[3] != ' ' ) {
				error = $"not a DDS: first bytes are {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}";
				return false;
			}

			int headerSize = BitConverter.ToInt32(bytes, 4);
			if ( headerSize != HeaderSize ) {
				error = $"DDS header claims {headerSize} bytes, expected {HeaderSize}";
				return false;
			}

			int height = BitConverter.ToInt32(bytes, 12);
			int width = BitConverter.ToInt32(bytes, 16);
			int mipCount = BitConverter.ToInt32(bytes, 28);
			// DDS_PIXELFORMAT starts 72 bytes into the header, i.e. 76 into the file.
			uint pfFlags = BitConverter.ToUInt32(bytes, 80);
			uint fourCC = BitConverter.ToUInt32(bytes, 84);
			int rgbBitCount = BitConverter.ToInt32(bytes, 88);
			uint rMask = BitConverter.ToUInt32(bytes, 92);
			uint gMask = BitConverter.ToUInt32(bytes, 96);
			uint bMask = BitConverter.ToUInt32(bytes, 100);
			uint aMask = BitConverter.ToUInt32(bytes, 104);

			if ( width <= 0 || height <= 0 || width >= 1 << 16 || height >= 1 << 16 ) {
				error = $"implausible dimensions {width}x{height}";
				return false;
			}

			const uint FourCCFlag = 0x4;
			int dataStart = MagicSize + HeaderSize;
			TextureFormat format;

			if ( (pfFlags & FourCCFlag) != 0 ) {
				if ( fourCC == MakeFourCC('D', 'X', '1', '0') ) {
					if ( bytes.Length < dataStart + Dxt10HeaderSize ) {
						error = "DX10 header is truncated";
						return false;
					}
					int dxgi = BitConverter.ToInt32(bytes, dataStart);
					dataStart += Dxt10HeaderSize;
					format = FromDxgiFormat(dxgi);
					if ( format == TextureFormat.Unknown ) {
						error = $"unsupported DXGI format {dxgi}";
						return false;
					}
				} else {
					format = FromFourCC(fourCC);
					if ( format == TextureFormat.Unknown ) {
						error = $"unsupported fourCC \"{FourCCToString(fourCC)}\"";
						return false;
					}
				}
			} else {
				format = FromMasks(rgbBitCount, rMask, gMask, bMask, aMask);
				if ( format == TextureFormat.Unknown ) {
					error = $"unsupported uncompressed layout: {rgbBitCount}bpp " +
						$"masks R={rMask:X8} G={gMask:X8} B={bMask:X8} A={aMask:X8}";
					return false;
				}
			}

			// Widths and heights are bounded above, but the products are not — keep the size
			// math in long so a silly header cannot overflow into a negative allocation.
			int rowPitch;
			long level0Bytes;
			if ( IsBlockFormat(format) ) {
				int blockBytes = BlockBytes(format);
				int blocksWide = Math.Max(1, (width + 3) / 4);
				int blocksHigh = Math.Max(1, (height + 3) / 4);
				rowPitch = blocksWide * blockBytes;
				level0Bytes = (long)rowPitch * blocksHigh;
			} else {
				int bpp = BytesPerPixel(format);
				rowPitch = width * bpp;
				level0Bytes = (long)rowPitch * height;
			}
			if ( level0Bytes <= 0 || level0Bytes > int.MaxValue / 4 ) {
				error = $"implausible surface size {level0Bytes} for {width}x{height} {format}";
				return false;
			}
			int level0Size = (int)level0Bytes;

			if ( dataStart + level0Size > bytes.Length ) {
				error = $"surface data is truncated: need {level0Size} bytes at {dataStart}, file has {bytes.Length}";
				return false;
			}

			byte[] level0 = new byte[level0Size];
			Buffer.BlockCopy(bytes, dataStart, level0, 0, level0Size);

			image = new DdsImage {
				Width = width,
				Height = height,
				MipCount = Math.Max(1, mipCount),
				Format = format,
				Data = level0,
				RowPitch = rowPitch,
			};
			return true;
		}

		#region Format tables

		private static uint MakeFourCC(char a, char b, char c, char d) =>
			(uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

		private static string FourCCToString(uint fourCC) {
			var sb = new StringBuilder(4);
			for ( int i = 0; i < 4; i++ ) {
				char c = (char)((fourCC >> (i * 8)) & 0xFF);
				sb.Append(c >= 0x20 && c < 0x7F ? c : '.');
			}
			return sb.ToString();
		}

		private static TextureFormat FromFourCC(uint fourCC) {
			if ( fourCC == MakeFourCC('D', 'X', 'T', '1') ) return TextureFormat.BC1_UNorm;
			if ( fourCC == MakeFourCC('D', 'X', 'T', '2') ) return TextureFormat.BC2_UNorm;
			if ( fourCC == MakeFourCC('D', 'X', 'T', '3') ) return TextureFormat.BC2_UNorm;
			if ( fourCC == MakeFourCC('D', 'X', 'T', '4') ) return TextureFormat.BC3_UNorm;
			if ( fourCC == MakeFourCC('D', 'X', 'T', '5') ) return TextureFormat.BC3_UNorm;
			if ( fourCC == MakeFourCC('A', 'T', 'I', '1') ) return TextureFormat.BC4_UNorm;
			if ( fourCC == MakeFourCC('B', 'C', '4', 'U') ) return TextureFormat.BC4_UNorm;
			if ( fourCC == MakeFourCC('A', 'T', 'I', '2') ) return TextureFormat.BC5_UNorm;
			if ( fourCC == MakeFourCC('B', 'C', '5', 'U') ) return TextureFormat.BC5_UNorm;
			return TextureFormat.Unknown;
		}

		// The DXGI_FORMAT values we can do something with.
		private static TextureFormat FromDxgiFormat(int dxgi) {
			switch ( dxgi ) {
				case 2: return TextureFormat.R32G32B32A32_Float;
				case 10: return TextureFormat.R16G16B16A16_Float;
				case 28: case 29: return TextureFormat.R8G8B8A8_UNorm;  // UNORM, UNORM_SRGB
				case 61: return TextureFormat.R8_UNorm;
				case 65: return TextureFormat.R8_UNorm;                 // A8_UNORM, close enough to show
				case 71: case 72: return TextureFormat.BC1_UNorm;
				case 74: case 75: return TextureFormat.BC2_UNorm;
				case 77: case 78: return TextureFormat.BC3_UNorm;
				case 80: return TextureFormat.BC4_UNorm;
				case 83: return TextureFormat.BC5_UNorm;
				case 87: case 88: return TextureFormat.B8G8R8A8_UNorm;
				case 95: case 96: return TextureFormat.BC6H_UF16;
				case 98: case 99: return TextureFormat.BC7_UNorm;
				default: return TextureFormat.Unknown;
			}
		}

		private static TextureFormat FromMasks(int bits, uint r, uint g, uint b, uint a) {
			if ( bits == 32 ) {
				if ( r == 0x00FF0000 && g == 0x0000FF00 && b == 0x000000FF ) return TextureFormat.B8G8R8A8_UNorm;
				if ( r == 0x000000FF && g == 0x0000FF00 && b == 0x00FF0000 ) return TextureFormat.R8G8B8A8_UNorm;
			}
			if ( bits == 8 && (r == 0xFF || a == 0xFF) ) return TextureFormat.R8_UNorm;
			return TextureFormat.Unknown;
		}

		public static bool IsBlockFormat(TextureFormat format) {
			switch ( format ) {
				case TextureFormat.BC1_UNorm:
				case TextureFormat.BC2_UNorm:
				case TextureFormat.BC3_UNorm:
				case TextureFormat.BC4_UNorm:
				case TextureFormat.BC5_UNorm:
				case TextureFormat.BC6H_UF16:
				case TextureFormat.BC7_UNorm:
					return true;
				default:
					return false;
			}
		}

		private static int BlockBytes(TextureFormat format) =>
			format == TextureFormat.BC1_UNorm || format == TextureFormat.BC4_UNorm ? 8 : 16;

		private static int BytesPerPixel(TextureFormat format) {
			switch ( format ) {
				case TextureFormat.R8_UNorm: return 1;
				case TextureFormat.R8G8B8A8_UNorm:
				case TextureFormat.B8G8R8A8_UNorm: return 4;
				case TextureFormat.R16G16B16A16_Float: return 8;
				case TextureFormat.R32G32B32A32_Float: return 16;
				default: return 0;
			}
		}

		#endregion

		#region Software decode (BC1-BC5, BC7, and the uncompressed layouts)

		/// <summary>
		/// Decode mip 0 to straight RGBA8, top-down, for backends that cannot sample BC formats.
		/// Returns false for BC6H and the float formats, which are HDR and would need tone
		/// mapping to mean anything as 8-bit RGBA.
		/// </summary>
		public bool TryDecodeToRgba(out byte[] rgba) {
			rgba = null;
			switch ( Format ) {
				case TextureFormat.R8G8B8A8_UNorm:
					rgba = (byte[])Data.Clone();
					return true;
				case TextureFormat.B8G8R8A8_UNorm: {
						byte[] outBuf = (byte[])Data.Clone();
						for ( int i = 0; i + 3 < outBuf.Length; i += 4 ) {
							byte t = outBuf[i];
							outBuf[i] = outBuf[i + 2];
							outBuf[i + 2] = t;
						}
						rgba = outBuf;
						return true;
					}
				case TextureFormat.R8_UNorm: {
						if ( (long)Width * Height * 4 > int.MaxValue ) return false;
						byte[] outBuf = new byte[Width * Height * 4];
						for ( int i = 0; i < Width * Height && i < Data.Length; i++ ) {
							byte v = Data[i];
							outBuf[i * 4 + 0] = v;
							outBuf[i * 4 + 1] = v;
							outBuf[i * 4 + 2] = v;
							outBuf[i * 4 + 3] = 255;
						}
						rgba = outBuf;
						return true;
					}
				case TextureFormat.BC1_UNorm:
				case TextureFormat.BC2_UNorm:
				case TextureFormat.BC3_UNorm:
				case TextureFormat.BC4_UNorm:
				case TextureFormat.BC5_UNorm:
				case TextureFormat.BC7_UNorm:
					rgba = DecodeBlocks();
					return rgba != null;
				default:
					return false;
			}
		}

		private byte[] DecodeBlocks() {
			if ( (long)Width * Height * 4 > int.MaxValue ) return null;
			int blockBytes = BlockBytes(Format);
			int blocksWide = Math.Max(1, (Width + 3) / 4);
			int blocksHigh = Math.Max(1, (Height + 3) / 4);
			byte[] output = new byte[Width * Height * 4];
			var pixels = new byte[16 * 4];   // one 4x4 block, RGBA

			for ( int by = 0; by < blocksHigh; by++ ) {
				for ( int bx = 0; bx < blocksWide; bx++ ) {
					int at = (by * blocksWide + bx) * blockBytes;
					if ( at + blockBytes > Data.Length ) return output; // truncated; show what we have
					Array.Clear(pixels, 0, pixels.Length);
					switch ( Format ) {
						case TextureFormat.BC7_UNorm:
							DecodeBc7Block(Data, at, pixels);
							break;
						case TextureFormat.BC1_UNorm:
							DecodeColorBlock(Data, at, pixels, opaqueOnly: false);
							break;
						case TextureFormat.BC2_UNorm:
							DecodeColorBlock(Data, at + 8, pixels, opaqueOnly: true);
							for ( int i = 0; i < 16; i++ ) {
								int nibble = (Data[at + (i >> 1)] >> ((i & 1) * 4)) & 0xF;
								pixels[i * 4 + 3] = (byte)(nibble * 17);
							}
							break;
						case TextureFormat.BC3_UNorm:
							DecodeColorBlock(Data, at + 8, pixels, opaqueOnly: true);
							DecodeAlphaBlock(Data, at, pixels, 3);
							break;
						case TextureFormat.BC4_UNorm:
							// One channel: show it as grey.
							DecodeAlphaBlock(Data, at, pixels, 0);
							for ( int i = 0; i < 16; i++ ) {
								pixels[i * 4 + 1] = pixels[i * 4 + 0];
								pixels[i * 4 + 2] = pixels[i * 4 + 0];
								pixels[i * 4 + 3] = 255;
							}
							break;
						case TextureFormat.BC5_UNorm:
							// Two channels (usually a normal map's X and Y).
							DecodeAlphaBlock(Data, at, pixels, 0);
							DecodeAlphaBlock(Data, at + 8, pixels, 1);
							for ( int i = 0; i < 16; i++ ) {
								pixels[i * 4 + 2] = 255;
								pixels[i * 4 + 3] = 255;
							}
							break;
					}
					// Blit the block, clipping at the edges of a non-multiple-of-4 image.
					for ( int py = 0; py < 4; py++ ) {
						int y = by * 4 + py;
						if ( y >= Height ) break;
						for ( int px = 0; px < 4; px++ ) {
							int x = bx * 4 + px;
							if ( x >= Width ) break;
							int src = (py * 4 + px) * 4;
							int dst = (y * Width + x) * 4;
							output[dst + 0] = pixels[src + 0];
							output[dst + 1] = pixels[src + 1];
							output[dst + 2] = pixels[src + 2];
							output[dst + 3] = pixels[src + 3];
						}
					}
				}
			}
			return output;
		}

		/// <summary>
		/// The 8-byte BC1-style colour block: two RGB565 endpoints and 16 2-bit indices.
		/// When the first endpoint is not greater than the second, index 3 is transparent black
		/// — unless <paramref name="opaqueOnly"/>, i.e. the alpha lives in a separate block.
		/// </summary>
		private static void DecodeColorBlock(byte[] data, int at, byte[] pixels, bool opaqueOnly) {
			ushort c0 = (ushort)(data[at] | (data[at + 1] << 8));
			ushort c1 = (ushort)(data[at + 2] | (data[at + 3] << 8));
			var r = new byte[4];
			var g = new byte[4];
			var b = new byte[4];
			var a = new byte[4] { 255, 255, 255, 255 };
			Unpack565(c0, out r[0], out g[0], out b[0]);
			Unpack565(c1, out r[1], out g[1], out b[1]);
			if ( c0 > c1 || opaqueOnly ) {
				r[2] = (byte)((2 * r[0] + r[1]) / 3);
				g[2] = (byte)((2 * g[0] + g[1]) / 3);
				b[2] = (byte)((2 * b[0] + b[1]) / 3);
				r[3] = (byte)((r[0] + 2 * r[1]) / 3);
				g[3] = (byte)((g[0] + 2 * g[1]) / 3);
				b[3] = (byte)((b[0] + 2 * b[1]) / 3);
			} else {
				r[2] = (byte)((r[0] + r[1]) / 2);
				g[2] = (byte)((g[0] + g[1]) / 2);
				b[2] = (byte)((b[0] + b[1]) / 2);
				r[3] = g[3] = b[3] = 0;
				a[3] = 0;
			}
			uint indices = BitConverter.ToUInt32(data, at + 4);
			for ( int i = 0; i < 16; i++ ) {
				int code = (int)((indices >> (i * 2)) & 0x3);
				pixels[i * 4 + 0] = r[code];
				pixels[i * 4 + 1] = g[code];
				pixels[i * 4 + 2] = b[code];
				pixels[i * 4 + 3] = a[code];
			}
		}

		private static void Unpack565(ushort c, out byte r, out byte g, out byte b) {
			int r5 = (c >> 11) & 0x1F;
			int g6 = (c >> 5) & 0x3F;
			int b5 = c & 0x1F;
			// Replicate the high bits into the low ones so 31 maps to 255, not 248.
			r = (byte)((r5 << 3) | (r5 >> 2));
			g = (byte)((g6 << 2) | (g6 >> 4));
			b = (byte)((b5 << 3) | (b5 >> 2));
		}

		/// <summary>
		/// The 8-byte BC3/BC4-style single-channel block: two endpoints and 16 3-bit indices,
		/// written into <paramref name="channel"/> of each pixel.
		/// </summary>
		private static void DecodeAlphaBlock(byte[] data, int at, byte[] pixels, int channel) {
			byte a0 = data[at];
			byte a1 = data[at + 1];
			var values = new byte[8];
			values[0] = a0;
			values[1] = a1;
			if ( a0 > a1 ) {
				for ( int k = 1; k <= 6; k++ ) values[k + 1] = (byte)(((7 - k) * a0 + k * a1) / 7);
			} else {
				for ( int k = 1; k <= 4; k++ ) values[k + 1] = (byte)(((5 - k) * a0 + k * a1) / 5);
				values[6] = 0;
				values[7] = 255;
			}
			// 16 x 3 bits, little-endian across the 6 index bytes.
			ulong bits = 0;
			for ( int i = 0; i < 6; i++ ) bits |= (ulong)data[at + 2 + i] << (i * 8);
			for ( int i = 0; i < 16; i++ ) {
				int code = (int)((bits >> (i * 3)) & 0x7);
				pixels[i * 4 + channel] = values[code];
			}
		}

		#endregion

		#region BC7 decode

		// BC7 packs eight different encodings into the same 16 bytes. Each block starts with a
		// unary mode field (the number of zero bits before the first 1), and the mode decides how
		// many subsets the 4x4 block is split into, how the block is partitioned between them,
		// how many bits the endpoints and indices get, and whether alpha is interpolated
		// separately or swapped with a colour channel afterwards.
		//
		// The tables below are the format's own constants, generated mechanically from the
		// reference decoder in Pillow 12.3.0 (src/libImaging/BcnDecode.c) rather than typed by
		// hand — the partition tables alone are 128 arbitrary bit-packed values, and a single
		// wrong digit would corrupt one partition out of 64 in a way that is easy to miss.
		//
		// Subsets2/Subsets3 are bit-packed partition tables: for two subsets one bit per pixel,
		// for three subsets two bits per pixel, LSB = pixel 0. Anchor* give the pixel index in
		// each partition whose index field is one bit shorter (its high bit is implied zero,
		// which is what fixes the direction of each subset's interpolation).

		// GENERATED from Pillow 12.3.0 src/libImaging/BcnDecode.c - do not hand-edit.
		private static readonly ushort[] Bc7Subsets2 = new ushort[] {
			0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80,
			0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
			0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE,
			0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
			0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A,
			0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
			0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C,
			0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
		};
		private static readonly uint[] Bc7Subsets3 = new uint[] {
			0xAA685050, 0x6A5A5040, 0x5A5A4200, 0x5450A0A8, 0xA5A50000, 0xA0A05050,
			0x5555A0A0, 0x5A5A5050, 0xAA550000, 0xAA555500, 0xAAAA5500, 0x90909090,
			0x94949494, 0xA4A4A4A4, 0xA9A59450, 0x2A0A4250, 0xA5945040, 0x0A425054,
			0xA5A5A500, 0x55A0A0A0, 0xA8A85454, 0x6A6A4040, 0xA4A45000, 0x1A1A0500,
			0x0050A4A4, 0xAAA59090, 0x14696914, 0x69691400, 0xA08585A0, 0xAA821414,
			0x50A4A450, 0x6A5A0200, 0xA9A58000, 0x5090A0A8, 0xA8A09050, 0x24242424,
			0x00AA5500, 0x24924924, 0x24499224, 0x50A50A50, 0x500AA550, 0xAAAA4444,
			0x66660000, 0xA5A0A5A0, 0x50A050A0, 0x69286928, 0x44AAAA44, 0x66666600,
			0xAA444444, 0x54A854A8, 0x95809580, 0x96969600, 0xA85454A8, 0x80959580,
			0xAA141414, 0x96960000, 0xAAAA1414, 0xA05050A0, 0xA0A5A5A0, 0x96000000,
			0x40804080, 0xA9A8A9A8, 0xAAAAAA44, 0x2A4A5254,
		};
		private static readonly byte[] Bc7Anchor2 = new byte[] {
			15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
			15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
			15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
			6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
		};
		private static readonly byte[] Bc7Anchor3a = new byte[] {
			3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
			3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
			8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
			3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
		};
		private static readonly byte[] Bc7Anchor3b = new byte[] {
			15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
			15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
			15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
			15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
		};
		// Per mode: subsets, partition bits, rotation bits, index-selection bit, colour bits,
		// alpha bits, per-endpoint p-bit, shared p-bit, index bits, second index bits.
		private static readonly byte[,] Bc7Modes = new byte[8, 10] {
			{ 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 },
			{ 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
			{ 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 },
			{ 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
			{ 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 },
			{ 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
			{ 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 },
			{ 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
		};
		private static readonly byte[] Bc7Weights2 = new byte[] { 0, 21, 43, 64 };
		private static readonly byte[] Bc7Weights3 = new byte[] { 0, 9, 18, 27, 37, 46, 55, 64 };
		private static readonly byte[] Bc7Weights4 = new byte[] { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

		private static byte[] Bc7Weights(int indexBits) =>
			indexBits == 2 ? Bc7Weights2 : indexBits == 3 ? Bc7Weights3 : Bc7Weights4;

		private static int Bc7Subset(int subsetCount, int partition, int pixel) {
			if ( subsetCount == 2 ) return 1 & (Bc7Subsets2[partition] >> pixel);
			if ( subsetCount == 3 ) return 3 & (int)(Bc7Subsets3[partition] >> (2 * pixel));
			return 0;
		}

		/// <summary>Read up to 8 bits at a bit offset within a block.</summary>
		private static int Bc7GetBits(byte[] data, int block, int bitPosition, int count) {
			if ( count == 0 ) return 0;
			int index = block + (bitPosition >> 3);
			int shift = bitPosition & 7;
			int mask = (1 << count) - 1;
			if ( shift + count <= 8 ) return (data[index] >> shift) & mask;
			int high = index + 1 < data.Length ? data[index + 1] : 0;
			return ((data[index] | (high << 8)) >> shift) & mask;
		}

		/// <summary>Bit-replicate a quantized endpoint component up to 8 bits.</summary>
		private static int Bc7Expand(int value, int bits) {
			int shifted = (value << (8 - bits)) & 0xFF;
			return (shifted | (shifted >> bits)) & 0xFF;
		}

		private static void Bc7Lerp(byte[] pixels, int at, int[] endpoints, int e, int colorWeight, int alphaWeight) {
			int ct = 64 - colorWeight;
			int at2 = 64 - alphaWeight;
			pixels[at + 0] = (byte)((ct * endpoints[e * 4 + 0] + colorWeight * endpoints[(e + 1) * 4 + 0] + 32) >> 6);
			pixels[at + 1] = (byte)((ct * endpoints[e * 4 + 1] + colorWeight * endpoints[(e + 1) * 4 + 1] + 32) >> 6);
			pixels[at + 2] = (byte)((ct * endpoints[e * 4 + 2] + colorWeight * endpoints[(e + 1) * 4 + 2] + 32) >> 6);
			pixels[at + 3] = (byte)((at2 * endpoints[e * 4 + 3] + alphaWeight * endpoints[(e + 1) * 4 + 3] + 32) >> 6);
		}

		/// <summary>
		/// Decode one 16-byte BC7 block into 16 RGBA pixels. Follows the reference decoder step
		/// for step; see the note above the tables.
		/// </summary>
		private static void DecodeBc7Block(byte[] data, int block, byte[] pixels) {
			int firstByte = data[block];
			if ( firstByte == 0 ) {
				// No mode bit set at all: a reserved encoding. The reference decodes it as
				// opaque black rather than failing, so do the same.
				for ( int i = 0; i < 16; i++ ) {
					pixels[i * 4 + 0] = 0;
					pixels[i * 4 + 1] = 0;
					pixels[i * 4 + 2] = 0;
					pixels[i * 4 + 3] = 255;
				}
				return;
			}

			// The mode is unary: however many zero bits precede the first set bit.
			int bit = 0;
			while ( (firstByte & (1 << bit)) == 0 ) bit++;
			int mode = bit;
			bit++;

			int subsets = Bc7Modes[mode, 0];
			int partitionBits = Bc7Modes[mode, 1];
			int rotationBits = Bc7Modes[mode, 2];
			int indexSelBits = Bc7Modes[mode, 3];
			int colorBits = Bc7Modes[mode, 4];
			int alphaBits = Bc7Modes[mode, 5];
			int endpointPBit = Bc7Modes[mode, 6];
			int sharedPBit = Bc7Modes[mode, 7];
			int indexBits = Bc7Modes[mode, 8];
			int indexBits2 = Bc7Modes[mode, 9];

			byte[] colorWeights = Bc7Weights(indexBits);
			byte[] alphaWeights = Bc7Weights(alphaBits != 0 && indexBits2 != 0 ? indexBits2 : indexBits);

			int partition = Bc7GetBits(data, block, bit, partitionBits); bit += partitionBits;
			int rotation = Bc7GetBits(data, block, bit, rotationBits); bit += rotationBits;
			int indexSelection = Bc7GetBits(data, block, bit, indexSelBits); bit += indexSelBits;

			// Up to three subsets, two endpoints each; four components per endpoint.
			int endpointCount = subsets << 1;
			var endpoints = new int[6 * 4];

			// Components are stored channel-major: every endpoint's red, then green, then blue.
			for ( int channel = 0; channel < 3; channel++ ) {
				for ( int i = 0; i < endpointCount; i++ ) {
					endpoints[i * 4 + channel] = Bc7GetBits(data, block, bit, colorBits);
					bit += colorBits;
				}
			}
			for ( int i = 0; i < endpointCount; i++ ) {
				if ( alphaBits != 0 ) {
					endpoints[i * 4 + 3] = Bc7GetBits(data, block, bit, alphaBits);
					bit += alphaBits;
				} else {
					endpoints[i * 4 + 3] = 255;
				}
			}

			// P-bits are a shared low bit appended to every component of an endpoint (or of a
			// whole subset), which is how these modes buy one more bit of precision cheaply.
			if ( endpointPBit != 0 ) {
				colorBits++;
				if ( alphaBits != 0 ) alphaBits++;
				for ( int i = 0; i < endpointCount; i++ ) {
					int p = Bc7GetBits(data, block, bit, 1); bit++;
					endpoints[i * 4 + 0] = (endpoints[i * 4 + 0] << 1) | p;
					endpoints[i * 4 + 1] = (endpoints[i * 4 + 1] << 1) | p;
					endpoints[i * 4 + 2] = (endpoints[i * 4 + 2] << 1) | p;
					if ( alphaBits != 0 ) endpoints[i * 4 + 3] = (endpoints[i * 4 + 3] << 1) | p;
				}
			}
			if ( sharedPBit != 0 ) {
				colorBits++;
				if ( alphaBits != 0 ) alphaBits++;
				for ( int i = 0; i < endpointCount; i += 2 ) {
					int p = Bc7GetBits(data, block, bit, 1); bit++;
					for ( int j = 0; j < 2; j++ ) {
						endpoints[(i + j) * 4 + 0] = (endpoints[(i + j) * 4 + 0] << 1) | p;
						endpoints[(i + j) * 4 + 1] = (endpoints[(i + j) * 4 + 1] << 1) | p;
						endpoints[(i + j) * 4 + 2] = (endpoints[(i + j) * 4 + 2] << 1) | p;
						if ( alphaBits != 0 ) endpoints[(i + j) * 4 + 3] = (endpoints[(i + j) * 4 + 3] << 1) | p;
					}
				}
			}

			for ( int i = 0; i < endpointCount; i++ ) {
				endpoints[i * 4 + 0] = Bc7Expand(endpoints[i * 4 + 0], colorBits);
				endpoints[i * 4 + 1] = Bc7Expand(endpoints[i * 4 + 1], colorBits);
				endpoints[i * 4 + 2] = Bc7Expand(endpoints[i * 4 + 2], colorBits);
				if ( alphaBits != 0 ) endpoints[i * 4 + 3] = Bc7Expand(endpoints[i * 4 + 3], alphaBits);
			}

			// Colour indices run from here; the second index set (modes 4 and 5) starts after
			// them, shortened by one bit per anchor pixel.
			int colorIndexBit = bit;
			int alphaIndexBit = colorIndexBit + 16 * indexBits - subsets;

			for ( int i = 0; i < 16; i++ ) {
				int e = Bc7Subset(subsets, partition, i) << 1;

				int bits = indexBits;
				if ( i == 0 ) {
					bits--;                       // subset 0's anchor is always pixel 0
				} else if ( subsets == 2 ) {
					if ( i == Bc7Anchor2[partition] ) bits--;
				} else if ( subsets == 3 ) {
					if ( i == Bc7Anchor3a[partition] || i == Bc7Anchor3b[partition] ) bits--;
				}
				int index = Bc7GetBits(data, block, colorIndexBit, bits);
				colorIndexBit += bits;

				if ( alphaBits != 0 && indexBits2 != 0 ) {
					int bits2 = indexBits2;
					if ( i == 0 ) bits2--;
					int index2 = Bc7GetBits(data, block, alphaIndexBit, bits2);
					alphaIndexBit += bits2;
					// The index-selection bit swaps which set drives colour and which alpha.
					if ( indexSelection != 0 ) {
						Bc7Lerp(pixels, i * 4, endpoints, e, alphaWeights[index2], colorWeights[index]);
					} else {
						Bc7Lerp(pixels, i * 4, endpoints, e, colorWeights[index], alphaWeights[index2]);
					}
				} else {
					Bc7Lerp(pixels, i * 4, endpoints, e, colorWeights[index], colorWeights[index]);
				}

				// Modes 4 and 5 may have stored a colour channel in the alpha slot; put it back.
				if ( rotation != 0 ) {
					int swapWith = i * 4 + (rotation - 1);
					byte t = pixels[swapWith];
					pixels[swapWith] = pixels[i * 4 + 3];
					pixels[i * 4 + 3] = t;
				}
			}
		}

		#endregion

		public override string ToString() =>
			$"DDS {Width}x{Height} {Format} mips:{MipCount} pitch:{RowPitch} data:{Data?.Length ?? 0}";
	}
}
