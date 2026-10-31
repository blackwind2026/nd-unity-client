// ============================================================================
//  LevelPackets.cs - level / stat / mastery packets.
//  Ground truth: src/nd_gs_packets.h (byte-verified) + client packet_for_level.h.
//  NOTE the CODEX_OFFLINE_LOGIN build widens SET_POWER power fields short->int32
//  (SET_POWER = 16 bytes), and CHARAC_SET_STATE state is int32.
// ============================================================================
using System;

namespace Nd.Packets
{
    // --- 104 SET_LEVEL (S->C) : EXP update, 7 bytes ---
    public sealed class MsgSetLevel : NdPacket
    {
        public const int Size = 7;
        public uint Jin;   // current EXP

        public override int MsgId => Nd.Packets.MsgId.SetLevel;
        public override int WireSize => Size;
        public override int Write(Span<byte> d) { int o = WritePrefix(d); Wire.U32(d, o, Jin); return Size; }
        public static MsgSetLevel Parse(ReadOnlySpan<byte> d) => new MsgSetLevel { Jin = Wire.U32(d, Wire.PrefixSize) };
    }

    // --- 105 SET_POWER (S->C) : hp/force/conc, 16 bytes (int-widened build) ---
    //  bMax=1 -> set MAX values; bMax=0 -> set CURRENT (used to heal on level-up / rest).
    public sealed class MsgSetPower : NdPacket
    {
        public const int Size = 16;
        public byte Max;               // bMax: 1 = max, 0 = current
        public int  LifePower, ForcePower, ConcentrationPower;

        public override int MsgId => Nd.Packets.MsgId.SetPower;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U8(d, o, Max); o += 1;
            Wire.S32(d, o, LifePower); Wire.S32(d, o + 4, ForcePower); Wire.S32(d, o + 8, ConcentrationPower);
            return Size;
        }
        public static MsgSetPower Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgSetPower
            {
                Max = Wire.U8(d, o),
                LifePower = Wire.S32(d, o + 1),
                ForcePower = Wire.S32(d, o + 5),
                ConcentrationPower = Wire.S32(d, o + 9),
            };
        }
    }

    // --- 106 LEVEL_UP_REQ (C->S) : 3 bytes ---
    public sealed class MsgLevelUpReq : NdPacket
    {
        public const int Size = 3;
        public override int MsgId => Nd.Packets.MsgId.LevelUp;
        public override int WireSize => Size;
        public override int Write(Span<byte> d) => WritePrefix(d);
        public static MsgLevelUpReq Parse(ReadOnlySpan<byte> d) => new MsgLevelUpReq();
    }

    // --- 106 LEVEL_UP_RESULT (S->C) : 10 bytes ---
    public sealed class MsgLevelUpResult : NdPacket
    {
        public const int Size = 10;
        public short InnerLevel;
        public short LeftPoint;
        public short LeftMateryPoint;
        public byte  LevelupInfo;   // 0 error, 1 leveled up, 2 not enough EXP

        public override int MsgId => Nd.Packets.MsgId.LevelUp;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.S16(d, o, InnerLevel); Wire.S16(d, o + 2, LeftPoint); Wire.S16(d, o + 4, LeftMateryPoint);
            Wire.U8(d, o + 6, LevelupInfo);
            return Size;
        }
        public static MsgLevelUpResult Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgLevelUpResult
            {
                InnerLevel = Wire.S16(d, o), LeftPoint = Wire.S16(d, o + 2), LeftMateryPoint = Wire.S16(d, o + 4),
                LevelupInfo = Wire.U8(d, o + 6),
            };
        }
    }

    // --- 107 POINT_USE_REQ (C->S) : add 1 stat point, 4 bytes ---
    public sealed class MsgPointUseReq : NdPacket
    {
        public const int Size = 4;
        public sbyte Where;   // 0 Str,1 Zen,2 Int,3 Con,4 Dex

        public override int MsgId => Nd.Packets.MsgId.PointUse;
        public override int WireSize => Size;
        public override int Write(Span<byte> d) { int o = WritePrefix(d); Wire.S8(d, o, Where); return Size; }
        public static MsgPointUseReq Parse(ReadOnlySpan<byte> d) => new MsgPointUseReq { Where = Wire.S8(d, Wire.PrefixSize) };
    }

    // --- 107 POINT_USE_RESULT (S->C) : 12 bytes (Codex: extra sPower2 short) ---
    public sealed class MsgPointUseResult : NdPacket
    {
        public const int Size = 12;
        public sbyte Where;
        public short Point;    // new stat value
        public short Power;    // derived max
        public short Power2;   // Codex extra short (0)
        public short LeftPoint;

        public override int MsgId => Nd.Packets.MsgId.PointUse;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.S8(d, o, Where);
            Wire.S16(d, o + 1, Point); Wire.S16(d, o + 3, Power); Wire.S16(d, o + 5, Power2); Wire.S16(d, o + 7, LeftPoint);
            return Size;
        }
        public static MsgPointUseResult Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgPointUseResult
            {
                Where = Wire.S8(d, o),
                Point = Wire.S16(d, o + 1), Power = Wire.S16(d, o + 3), Power2 = Wire.S16(d, o + 5), LeftPoint = Wire.S16(d, o + 7),
            };
        }
    }

    // --- 108 MASTERY_POINT_USE (C->S) : bump 1 skill mastery step, 6 bytes ---
    public sealed class MsgMasteryPointUse : NdPacket
    {
        public const int Size = 6;
        public byte  Page;        // ucPageNo:3 | padding:5 (low 3 bits)
        public short MasteryId;

        public override int MsgId => Nd.Packets.MsgId.MasteryPointUse;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U8(d, o, Page); Wire.S16(d, o + 1, MasteryId);
            return Size;
        }
        public static MsgMasteryPointUse Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgMasteryPointUse { Page = Wire.U8(d, o), MasteryId = Wire.S16(d, o + 1) };
        }
    }

    // --- 108 MASTERY_POINT_RESULT (S->C) : 9 bytes ---
    public sealed class MsgMasteryPointResult : NdPacket
    {
        public const int Size = 9;
        public byte  PageFail;         // ucPageNo:3 | ucFail:5 (0 OK, 1 error)
        public short MasteryId;
        public sbyte MasteryStep;      // NEW step of the skill
        public short LeftMasteryPoint;

        public override int MsgId => Nd.Packets.MsgId.MasteryPointUse;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U8(d, o, PageFail); Wire.S16(d, o + 1, MasteryId); Wire.S8(d, o + 3, MasteryStep);
            Wire.S16(d, o + 4, LeftMasteryPoint);
            return Size;
        }
        public static MsgMasteryPointResult Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgMasteryPointResult
            {
                PageFail = Wire.U8(d, o), MasteryId = Wire.S16(d, o + 1),
                MasteryStep = Wire.S8(d, o + 3), LeftMasteryPoint = Wire.S16(d, o + 4),
            };
        }
    }

    // --- 18 CHARAC_SET_STATE (S->C) : one status field (union), 8 bytes ---
    public sealed class MsgCharacSetState : NdPacket
    {
        public const int Size = 8;
        public sbyte Flag;    // EN_STATE_PACKET_FLAG (4 = honor)
        public int   State;   // union __charac_state max = int (e.g. sHonor)

        public override int MsgId => Nd.Packets.MsgId.CharacSetState;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.S8(d, o, Flag); Wire.S32(d, o + 1, State);
            return Size;
        }
        public static MsgCharacSetState Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgCharacSetState { Flag = Wire.S8(d, o), State = Wire.S32(d, o + 1) };
        }
    }
}
