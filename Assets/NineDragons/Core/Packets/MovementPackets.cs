// ============================================================================
//  MovementPackets.cs - character movement.
//  Ground truth: client packet_for_zone_monster.h MSG_ZONE_MOVE + src/main.cpp
//  (server reads C->S ZONE_MOVE fX@5, fZ@9). This is THE movement packet in use;
//  the newer MSG_MOVE/en_move_mode family exists in the client header but is NOT
//  dispatched by this Linux client / nd-world pair (see SPEC/M2-packets.md).
// ============================================================================
using System;

namespace Nd.Packets
{
    // --- 24 ZONE_MOVE (C<->S) : character move, 25 bytes ---
    //  C->S: player reports current(fX,fZ)+goto; S->C: broadcast another char's move.
    public sealed class MsgZoneMove : NdPacket
    {
        public const int Size = 25;
        public ushort UniqueId;
        public float  X, Z;          // current position
        public float  GotoX, GotoZ;  // destination
        public short  StandingObb;
        public ushort IndexAnim;     // sCharacIndex:4 | sAnimationNumber:12  (LE bitfield)

        public int CharacIndex     { get => IndexAnim & 0xF;          set => IndexAnim = (ushort)((IndexAnim & 0xFFF0) | (value & 0xF)); }
        public int AnimationNumber { get => (IndexAnim >> 4) & 0xFFF; set => IndexAnim = (ushort)((IndexAnim & 0x000F) | ((value & 0xFFF) << 4)); }

        public override int MsgId => Nd.Packets.MsgId.ZoneMove;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U16(d, o, UniqueId);
            Wire.F32(d, o + 2, X); Wire.F32(d, o + 6, Z);
            Wire.F32(d, o + 10, GotoX); Wire.F32(d, o + 14, GotoZ);
            Wire.S16(d, o + 18, StandingObb);
            Wire.U16(d, o + 20, IndexAnim);
            return Size;
        }
        public static MsgZoneMove Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgZoneMove
            {
                UniqueId = Wire.U16(d, o),
                X = Wire.F32(d, o + 2), Z = Wire.F32(d, o + 6),
                GotoX = Wire.F32(d, o + 10), GotoZ = Wire.F32(d, o + 14),
                StandingObb = Wire.S16(d, o + 18),
                IndexAnim = Wire.U16(d, o + 20),
            };
        }
    }

    // --- 26 ZONE_REQ_CHARAC_INFO (C->S) : ask the server for another player's name+appearance, 5 bytes ---
    //  MSG_ZONE_REQ_CHARAC_INFO { header; ucMessage; u_short usUniqueID; } (packet_for_zone_monster.h:361).
    //  The client sends this the first time it sees a PC (via ZONE_MOVE) it has no info for; the server
    //  answers with ZONE_CHARAC_INFO (27) carrying the appearance (XNetwork.cpp:15624-15638).
    public sealed class MsgZoneReqCharacInfo : NdPacket
    {
        public const int Size = 5;       // prefix(3) + usUniqueID(2)
        public ushort UniqueId;

        public override int MsgId => Nd.Packets.MsgId.ZoneReqCharacInfo;
        public override int WireSize => Size;
        public override int Write(Span<byte> d) { int o = WritePrefix(d); Wire.U16(d, o, UniqueId); return Size; }
        public static MsgZoneReqCharacInfo Parse(ReadOnlySpan<byte> d)
            => new MsgZoneReqCharacInfo { UniqueId = Wire.U16(d, Wire.PrefixSize) };
    }
}
