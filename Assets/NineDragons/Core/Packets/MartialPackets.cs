// ============================================================================
//  MartialPackets.cs - combat (martial) packets.
//  Ground truth: src/nd_gs_packets.h (byte-verified) + client packet_for_battle.h.
//  Bitfields are exposed as the raw wire byte/word (mtByte/seqByte/atkInfoWord)
//  plus typed accessors, so the layout is exact and self-documenting.
// ============================================================================
using System;

namespace Nd.Packets
{
    // --- 69 MARTIAL_FIRE / MSG_ACTIVE_FIRE (C->S) : player attacks, 9 bytes ---
    public sealed class MsgActiveFire : NdPacket
    {
        public const int Size = 9;
        public byte   MtByte;      // ucMartialType:4 | ucTargetType:3 | pad:1
        public byte   SeqByte;     // ucSequence:2 | pad:6
        public ushort TargetId;
        public short  Animation;

        public int MartialType { get => MtByte & 0xF;         set => MtByte = (byte)((MtByte & 0xF0) | (value & 0xF)); }
        public int TargetType  { get => (MtByte >> 4) & 0x7;  set => MtByte = (byte)((MtByte & 0x8F) | ((value & 0x7) << 4)); }
        public int Sequence    { get => SeqByte & 0x3;        set => SeqByte = (byte)((SeqByte & 0xFC) | (value & 0x3)); }

        public override int MsgId => Nd.Packets.MsgId.MartialFire;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U8(d, o, MtByte); Wire.U8(d, o + 1, SeqByte);
            Wire.U16(d, o + 2, TargetId); Wire.S16(d, o + 4, Animation);
            return Size;
        }
        public static MsgActiveFire Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgActiveFire
            {
                MtByte = Wire.U8(d, o), SeqByte = Wire.U8(d, o + 1),
                TargetId = Wire.U16(d, o + 2), Animation = Wire.S16(d, o + 4),
            };
        }
    }

    // --- 70 MARTIAL_RESULT / MSG_ACTIVE_RESULT_MOB (S->C) : damage on mob, 26 bytes ---
    public sealed class MsgActiveResultMob : NdPacket
    {
        public const int Size = 26;
        public byte   MtByte;         // martial:4 | target:3 | pad:1
        public int    LeftForce;      // attacker's remaining force
        public ushort AttackerId, TargetId;
        public short  Animation;
        public ushort AtkInfoWord;    // ucSequence:2 | atk1:3 | atk2:3 | atk3:3 | pad:5  (0 = all Hit)
        public ushort Damage1; public sbyte MobVitalRate1;
        public ushort Damage2; public sbyte MobVitalRate2;
        public ushort Damage3; public sbyte MobVitalRate3;
        public sbyte  BattleResult;

        public int MartialType { get => MtByte & 0xF;        set => MtByte = (byte)((MtByte & 0xF0) | (value & 0xF)); }
        public int TargetType  { get => (MtByte >> 4) & 0x7; set => MtByte = (byte)((MtByte & 0x8F) | ((value & 0x7) << 4)); }
        public int Sequence    { get => AtkInfoWord & 0x3;          set => AtkInfoWord = (ushort)((AtkInfoWord & 0xFFFC) | (value & 0x3)); }
        public int AtkInfo1    { get => (AtkInfoWord >> 2) & 0x7;   set => AtkInfoWord = (ushort)((AtkInfoWord & ~(0x7 << 2)) | ((value & 0x7) << 2)); }
        public int AtkInfo2    { get => (AtkInfoWord >> 5) & 0x7;   set => AtkInfoWord = (ushort)((AtkInfoWord & ~(0x7 << 5)) | ((value & 0x7) << 5)); }
        public int AtkInfo3    { get => (AtkInfoWord >> 8) & 0x7;   set => AtkInfoWord = (ushort)((AtkInfoWord & ~(0x7 << 8)) | ((value & 0x7) << 8)); }

        public override int MsgId => Nd.Packets.MsgId.MartialResult;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);        // 3
            Wire.U8(d, o, MtByte);              // 3
            Wire.S32(d, o + 1, LeftForce);      // 4
            Wire.U16(d, o + 5, AttackerId); Wire.U16(d, o + 7, TargetId);  // 8,10
            Wire.S16(d, o + 9, Animation);      // 12
            Wire.U16(d, o + 11, AtkInfoWord);   // 14
            Wire.U16(d, o + 13, Damage1); Wire.S8(d, o + 15, MobVitalRate1);  // 16,18
            Wire.U16(d, o + 16, Damage2); Wire.S8(d, o + 18, MobVitalRate2);  // 19,21
            Wire.U16(d, o + 19, Damage3); Wire.S8(d, o + 21, MobVitalRate3);  // 22,24
            Wire.S8(d, o + 22, BattleResult);   // 25
            return Size;                        // 26
        }
        public static MsgActiveResultMob Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgActiveResultMob
            {
                MtByte = Wire.U8(d, o),
                LeftForce = Wire.S32(d, o + 1),
                AttackerId = Wire.U16(d, o + 5), TargetId = Wire.U16(d, o + 7),
                Animation = Wire.S16(d, o + 9),
                AtkInfoWord = Wire.U16(d, o + 11),
                Damage1 = Wire.U16(d, o + 13), MobVitalRate1 = Wire.S8(d, o + 15),
                Damage2 = Wire.U16(d, o + 16), MobVitalRate2 = Wire.S8(d, o + 18),
                Damage3 = Wire.U16(d, o + 19), MobVitalRate3 = Wire.S8(d, o + 21),
                BattleResult = Wire.S8(d, o + 22),
            };
        }
    }
}
