using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace AtE {

	/// <summary>
	/// A parsed Path of Exile .datc64 table. The layout is:
	///
	///   u32   row count
	///   rows  row count * row size bytes, fixed width
	///   8 x 0xBB marker
	///   variable-length data (strings as null-terminated UTF-16, array contents)
	///
	/// Row size is not stored; it falls out of (marker position - 4) / row count. Offsets stored in
	/// rows (strings, arrays) are relative to the start of the marker. This class knows nothing
	/// about columns — <see cref="DatSchema"/> supplies those.
	/// </summary>
	public sealed class DatFile {

		public const int HeaderSize = 4;
		public const int MarkerLength = 8;

		public readonly byte[] Bytes;
		public readonly int RowCount;
		public readonly int RowSize;
		/// <summary>Index of the first marker byte; variable-data offsets are relative to this.</summary>
		public readonly int VarStart;

		private DatFile(byte[] bytes, int rowCount, int rowSize, int varStart) {
			Bytes = bytes;
			RowCount = rowCount;
			RowSize = rowSize;
			VarStart = varStart;
		}

		public static bool TryParse(byte[] bytes, out DatFile file, out string error) {
			file = null;
			error = null;
			if ( bytes == null || bytes.Length < HeaderSize + MarkerLength ) {
				error = "too short to be a .datc64 file";
				return false;
			}
			uint rows = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
			if ( rows > (uint)(bytes.Length - HeaderSize) ) {
				error = $"implausible row count {rows}";
				return false;
			}
			int rowCount = (int)rows;

			// The marker is the first run of eight 0xBB that sits on a row boundary. Requiring the
			// boundary keeps a coincidental run inside the row data from being mistaken for it.
			for ( int p = HeaderSize; p <= bytes.Length - MarkerLength; p++ ) {
				if ( bytes[p] != 0xBB ) continue;
				if ( rowCount == 0 ? p != HeaderSize : (p - HeaderSize) % rowCount != 0 ) continue;
				bool all = true;
				for ( int k = 1; k < MarkerLength; k++ ) {
					if ( bytes[p + k] != 0xBB ) { all = false; break; }
				}
				if ( !all ) continue;
				int rowSize = rowCount == 0 ? 0 : (p - HeaderSize) / rowCount;
				file = new DatFile(bytes, rowCount, rowSize, p);
				return true;
			}
			error = "no 0xBB marker found between the rows and the data section";
			return false;
		}

		/// <summary>Byte offset of a row within <see cref="Bytes"/>.</summary>
		public int RowOffset(int row) => HeaderSize + row * RowSize;

		public bool InRow(int column, int size, int rowSize) => column >= 0 && column + size <= rowSize;

		public long ReadInt(int at, int size, bool signed) {
			var s = new ReadOnlySpan<byte>(Bytes, at, size);
			switch ( size ) {
				case 1: return signed ? (sbyte)s[0] : s[0];
				case 2: return signed ? BinaryPrimitives.ReadInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
				case 4: return signed ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
				default: return BinaryPrimitives.ReadInt64LittleEndian(s);
			}
		}

		public float ReadFloat(int at) => BinaryPrimitives.ReadSingleLittleEndian(new ReadOnlySpan<byte>(Bytes, at, 4));

		public ulong ReadU64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(Bytes, at, 8));

		/// <summary>The string whose offset (into the data section) is stored at <paramref name="at"/>.</summary>
		public string ReadStringAt(int at) {
			ulong off = ReadU64(at);
			return ReadStringOffset(off);
		}

		public string ReadStringOffset(ulong off) {
			if ( off > (ulong)(Bytes.Length - VarStart) ) return "<bad string offset>";
			int start = VarStart + (int)off;
			int end = start;
			while ( end + 1 < Bytes.Length && (Bytes[end] != 0 || Bytes[end + 1] != 0) ) end += 2;
			return Encoding.Unicode.GetString(Bytes, start, end - start);
		}

		/// <summary>Reads an array header (count, offset) and checks that its contents lie inside the file.</summary>
		public bool TryReadArray(int at, int elementSize, out int count, out int contentStart) {
			ulong n = ReadU64(at);
			ulong off = ReadU64(at + 8);
			count = 0;
			contentStart = 0;
			if ( n == 0 ) return true;
			if ( elementSize <= 0 ) return false;
			ulong available = (ulong)(Bytes.Length - VarStart);
			if ( off > available || n > available || off + n * (ulong)elementSize > available ) return false;
			count = (int)n;
			contentStart = VarStart + (int)off;
			return true;
		}
	}
}
