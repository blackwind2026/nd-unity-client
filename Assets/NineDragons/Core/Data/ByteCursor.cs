using System;

namespace NineDragons.Data
{
    /// <summary>
    /// Little-endian cursor reader over an in-memory byte buffer. Mirrors the mr_* / it_*
    /// helpers in the C++ server (nd_monref.h, nd_itemtable.h). Reads past the end of the
    /// buffer return 0 (matching the C++ mr_u8 bounds behaviour) so a truncated file never
    /// throws — the loop simply stops advancing meaningful data.
    /// </summary>
    internal sealed class ByteCursor
    {
        private readonly byte[] _data;
        public int Pos;
        public int Length => _data.Length;

        public ByteCursor(byte[] data, int pos = 0)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            Pos = pos;
        }

        public bool CanRead(int n) => Pos + n <= _data.Length;

        public byte U8()
        {
            if (Pos < _data.Length) return _data[Pos++];
            Pos++;
            return 0;
        }

        public sbyte I8() => (sbyte)U8();

        public ushort U16()
        {
            int b0 = U8();
            int b1 = U8();
            return (ushort)(b0 | (b1 << 8));
        }

        public short I16() => (short)U16();

        public uint U32()
        {
            uint v = U8();
            v |= (uint)U8() << 8;
            v |= (uint)U8() << 16;
            v |= (uint)U8() << 24;
            return v;
        }

        public int I32() => (int)U32();

        public float F32()
        {
            uint v = U32();
            return BitConverter.Int32BitsToSingle((int)v);
        }

        public void Skip(int n) => Pos += n;

        // ---- absolute little-endian reads at an offset (for fixed-layout records) ----
        public static short I16At(byte[] p, int off) => (short)(p[off] | (p[off + 1] << 8));
        public static ushort U16At(byte[] p, int off) => (ushort)(p[off] | (p[off + 1] << 8));
        public static uint U32At(byte[] p, int off) =>
            (uint)p[off] | ((uint)p[off + 1] << 8) | ((uint)p[off + 2] << 16) | ((uint)p[off + 3] << 24);
    }
}
