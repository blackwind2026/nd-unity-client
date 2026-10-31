// ============================================================================
//  NdPacket.cs - base type for every M2 packet.
//
//  Public interface for M1 (transport) and M5 (session):
//    * INdPacket.MsgId / WireSize          - identity + serialized byte count
//    * INdPacket.Write(Span<byte>)         - zero-alloc serialize into a buffer
//    * NdPacket.ToBytes()                  - convenience byte[] serialize
//    * static T Parse(ReadOnlySpan<byte>)  - per-type deserialize (see each class)
//
//  Serialize writes a COMPLETE packet: nd_header(sLength=WireSize, crypto/comp=0)
//  + ucMessage + body. Hand the result to NdCrypto.Encapsulate to put it on the
//  wire. Parse expects a buffer that M1 has already decapsulated (tail stripped,
//  sLength restored); it reads from offset 0 (header) so PeekMsgId/PeekLength work.
// ============================================================================
using System;

namespace Nd.Packets
{
    public interface INdPacket
    {
        int MsgId { get; }
        int WireSize { get; }
        int Write(Span<byte> dst);
    }

    public abstract class NdPacket : INdPacket
    {
        public abstract int MsgId { get; }
        public abstract int WireSize { get; }

        /// <summary>Serialize into dst (must be >= WireSize). Returns bytes written.</summary>
        public abstract int Write(Span<byte> dst);

        /// <summary>Allocate a byte[] of exactly WireSize and serialize into it.</summary>
        public byte[] ToBytes()
        {
            var b = new byte[WireSize];
            Write(b);
            return b;
        }

        /// <summary>Write nd_header + ucMessage. Returns the body offset (Wire.PrefixSize = 3).</summary>
        protected int WritePrefix(Span<byte> dst) => Wire.WritePrefix(dst, WireSize, MsgId);
    }
}
