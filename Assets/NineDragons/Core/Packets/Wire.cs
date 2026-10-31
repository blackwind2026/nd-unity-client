// ============================================================================
//  Wire.cs - low-level framing + little-endian primitives for 9Dragons packets.
//
//  Ground truth for the framing header:
//    common/nd_crypto.h  ->  struct nd_header { short sLength:12; sCrypto:2; sCompressed:2; }
//    src/nd_gs_packets.h ->  every MSG_* begins  { nd_header header; uint8_t ucMessage; ... }
//    src/main.cpp:98     ->  ((nd_header*)pkt)->sLength = structLen;   (length = whole struct)
//
//  So on the wire (before the M1 crypto layer adds its 2-byte tail):
//    byte 0..1 : nd_header (little-endian 16-bit): bits0-11 sLength, 12-13 sCrypto, 14-15 sComp
//    byte 2    : ucMessage  (== MSG_NO_*)
//    byte 3..  : packet body
//
//  M2 always writes sLength = full serialized size and crypto/comp = 0. The M1
//  transport (NdCrypto.Encapsulate) later flips the crypto flag + appends the tail
//  and rewrites sLength to include the tail; on receive it restores sLength to the
//  body length before M2 parses. Length reads therefore mask 0x0FFF (matches
//  NdCrypto.WireLength) so a lingering crypto/comp flag never corrupts the size.
// ============================================================================
using System;

namespace Nd.Packets
{
    /// <summary>The 2-byte packet framing header (nd_header). Little-endian bitfield.</summary>
    public struct NdHeader
    {
        public int SLength;      // bits 0-11  (total packet length, header included, tail excluded)
        public int SCrypto;      // bits 12-13
        public int SCompressed;  // bits 14-15

        public NdHeader(int length, int crypto = 0, int compressed = 0)
        {
            SLength = length; SCrypto = crypto; SCompressed = compressed;
        }

        public void Write(Span<byte> dst)
        {
            int word = (SLength & 0x0FFF) | ((SCrypto & 0x3) << 12) | ((SCompressed & 0x3) << 14);
            dst[0] = (byte)(word & 0xFF);
            dst[1] = (byte)((word >> 8) & 0xFF);
        }

        public static NdHeader Read(ReadOnlySpan<byte> src)
        {
            int word = src[0] | (src[1] << 8);
            return new NdHeader(word & 0x0FFF, (word >> 12) & 0x3, (word >> 14) & 0x3);
        }
    }

    /// <summary>Static little-endian read/write helpers over byte spans (Unity/netstandard2.1 safe).</summary>
    public static class Wire
    {
        public const int HeaderSize = 2;   // nd_header
        public const int PrefixSize = 3;   // nd_header (2) + ucMessage (1)

        // ---- framing peeks (for M1 transport / M5 dispatch on a raw buffer) ----

        /// <summary>12-bit length from the header (mask 0x0FFF). Matches NdCrypto.WireLength.</summary>
        public static int PeekLength(ReadOnlySpan<byte> buf) => (buf[0] | (buf[1] << 8)) & 0x0FFF;

        /// <summary>The MSG_NO_* message id byte (offset 2).</summary>
        public static int PeekMsgId(ReadOnlySpan<byte> buf) => buf[2];

        /// <summary>Write header(sLength=size, crypto=0, comp=0) + ucMessage. Returns body offset (3).</summary>
        public static int WritePrefix(Span<byte> dst, int size, int msgId)
        {
            new NdHeader(size).Write(dst);
            dst[2] = (byte)msgId;
            return PrefixSize;
        }

        // ---- unsigned ----
        public static void U8(Span<byte> d, int o, int v) => d[o] = (byte)v;
        public static byte U8(ReadOnlySpan<byte> d, int o) => d[o];

        public static void U16(Span<byte> d, int o, int v)
        {
            d[o] = (byte)(v & 0xFF);
            d[o + 1] = (byte)((v >> 8) & 0xFF);
        }
        public static ushort U16(ReadOnlySpan<byte> d, int o)
            => (ushort)(d[o] | (d[o + 1] << 8));

        public static void U32(Span<byte> d, int o, uint v)
        {
            d[o] = (byte)(v & 0xFF);
            d[o + 1] = (byte)((v >> 8) & 0xFF);
            d[o + 2] = (byte)((v >> 16) & 0xFF);
            d[o + 3] = (byte)((v >> 24) & 0xFF);
        }
        public static uint U32(ReadOnlySpan<byte> d, int o)
            => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

        // ---- signed ----
        public static void S8(Span<byte> d, int o, int v) => d[o] = (byte)(sbyte)v;
        public static sbyte S8(ReadOnlySpan<byte> d, int o) => (sbyte)d[o];

        public static void S16(Span<byte> d, int o, int v) => U16(d, o, v & 0xFFFF);
        public static short S16(ReadOnlySpan<byte> d, int o) => (short)U16(d, o);

        public static void S32(Span<byte> d, int o, int v) => U32(d, o, unchecked((uint)v));
        public static int S32(ReadOnlySpan<byte> d, int o) => unchecked((int)U32(d, o));

        // ---- float (IEEE-754 32-bit, LE) ----
        public static void F32(Span<byte> d, int o, float v)
            => U32(d, o, unchecked((uint)BitConverter.SingleToInt32Bits(v)));
        public static float F32(ReadOnlySpan<byte> d, int o)
            => BitConverter.Int32BitsToSingle(unchecked((int)U32(d, o)));

        // ---- fixed-length char[] (null-padded, CP949/ASCII bytes; wire is byte-exact) ----

        /// <summary>Write a fixed-size field: copies up to `len` bytes of `src`, zero-pads the rest.</summary>
        public static void FixedBytes(Span<byte> d, int o, int len, ReadOnlySpan<byte> src)
        {
            int n = src.Length < len ? src.Length : len;
            for (int i = 0; i < n; i++) d[o + i] = src[i];
            for (int i = n; i < len; i++) d[o + i] = 0;
        }

        /// <summary>Read a fixed-size field as raw bytes (caller owns encoding).</summary>
        public static byte[] FixedBytes(ReadOnlySpan<byte> d, int o, int len)
        {
            var r = new byte[len];
            for (int i = 0; i < len; i++) r[i] = d[o + i];
            return r;
        }

        // ---- fixed char[] as text ----
        //  Names are CP949 on the wire; here we use a byte-preserving Latin1 map so
        //  the round-trip is byte-exact and never throws for arbitrary bytes. ASCII
        //  names decode correctly; Korean glyphs stay as raw bytes for M3/UI to redecode.

        /// <summary>Encode a string into a fixed field: Latin1 bytes, NUL-terminated + zero-padded.</summary>
        public static void CStr(Span<byte> d, int o, int len, string s)
        {
            int n = 0;
            if (s != null)
            {
                int m = s.Length < len ? s.Length : len;
                for (; n < m; n++)
                {
                    char ch = s[n];
                    d[o + n] = ch > 0xFF ? (byte)'?' : (byte)ch;
                }
            }
            for (int i = n; i < len; i++) d[o + i] = 0;
        }

        /// <summary>Decode a fixed field to a string (Latin1), trimmed at the first NUL.</summary>
        public static string CStr(ReadOnlySpan<byte> d, int o, int len)
        {
            int n = 0;
            while (n < len && d[o + n] != 0) n++;
            var chars = new char[n];
            for (int i = 0; i < n; i++) chars[i] = (char)d[o + i];
            return new string(chars);
        }
    }
}
