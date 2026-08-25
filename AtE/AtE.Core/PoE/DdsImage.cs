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
	/// and the uncompressed layouts, but not BC6H/BC7 — for those, upload to the GPU.
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
		/// False for BC6H/BC7 (and the float formats), which need the GPU. Lets callers offer or
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

		#region Software decode (BC1-BC5 and the uncompressed layouts)

		/// <summary>
		/// Decode mip 0 to straight RGBA8, top-down, for backends that cannot sample BC formats.
		/// Returns false for BC6H/BC7 and the float formats, which need the GPU (or more code
		/// than is worth writing here).
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

		public override string ToString() =>
			$"DDS {Width}x{Height} {Format} mips:{MipCount} pitch:{RowPitch} data:{Data?.Length ?? 0}";
	}
}
