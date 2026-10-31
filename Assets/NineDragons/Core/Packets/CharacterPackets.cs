// ============================================================================
//  CharacterPackets.cs - char-select + character-info packets.
//  Layout ground truth: src/nd_gs_packets.h (byte-verified with static_assert)
//  cross-checked against client Game_packets.h (_CharacList) + packet_for_svrmove.h.
// ============================================================================
using System;

namespace Nd.Packets
{
    // --- GS_CharacList : one char-select row, 88 bytes (Game_packets.h _CharacList
    //     with _CHARAC_BLOCKING + _XTS_NEWCHARACLIST on). NOT a standalone packet;
    //     it is an element of MSG_CHARAC_LIST, so it has no header/ucMessage. ---
    public sealed class GsCharacList
    {
        public const int Size = 88;

        public int    UniqueId;
        public byte[] Name = new byte[13];   // raw CP949 bytes
        public byte[] Nick = new byte[13];
        public sbyte  Zone;
        public sbyte  Sex, Face, Hair, Group;
        public sbyte  AmorType, PantsType, CapType, AdaeType, ShoesType, BackPackType, WeaponType, MaskType;
        public short  Amor, Pants, Cap, Adae, Shoes, BackPack;
        public short  SelectWeaponId;
        public short  Mask;
        public byte   WeaponStrength;
        public short  Str, Zen, Intelligence, Constitution, Dexterity;
        public short  InnerLevel;
        public sbyte  Class, ClassGrade, Gm;
        public float  PosX, PosZ;
        public int    BlockingEndDate;        // _CHARAC_BLOCKING
        public byte   NameFlags;              // _XTS_NEWCHARACLIST: ucChangeNameFlag:2 | ucLeftFlag:6

        public string NameText => Wire.CStr(Name, 0, 13);
        public string NickText => Wire.CStr(Nick, 0, 13);

        public int Write(Span<byte> d, int o)
        {
            Wire.S32(d, o + 0, UniqueId);
            Wire.FixedBytes(d, o + 4, 13, Name);
            Wire.FixedBytes(d, o + 17, 13, Nick);
            Wire.S8(d, o + 30, Zone);
            Wire.S8(d, o + 31, Sex); Wire.S8(d, o + 32, Face); Wire.S8(d, o + 33, Hair); Wire.S8(d, o + 34, Group);
            Wire.S8(d, o + 35, AmorType); Wire.S8(d, o + 36, PantsType); Wire.S8(d, o + 37, CapType); Wire.S8(d, o + 38, AdaeType);
            Wire.S8(d, o + 39, ShoesType); Wire.S8(d, o + 40, BackPackType); Wire.S8(d, o + 41, WeaponType); Wire.S8(d, o + 42, MaskType);
            Wire.S16(d, o + 43, Amor); Wire.S16(d, o + 45, Pants); Wire.S16(d, o + 47, Cap);
            Wire.S16(d, o + 49, Adae); Wire.S16(d, o + 51, Shoes); Wire.S16(d, o + 53, BackPack);
            Wire.S16(d, o + 55, SelectWeaponId);
            Wire.S16(d, o + 57, Mask);
            Wire.U8(d, o + 59, WeaponStrength);
            Wire.S16(d, o + 60, Str); Wire.S16(d, o + 62, Zen); Wire.S16(d, o + 64, Intelligence);
            Wire.S16(d, o + 66, Constitution); Wire.S16(d, o + 68, Dexterity);
            Wire.S16(d, o + 70, InnerLevel);
            Wire.S8(d, o + 72, Class); Wire.S8(d, o + 73, ClassGrade); Wire.S8(d, o + 74, Gm);
            Wire.F32(d, o + 75, PosX); Wire.F32(d, o + 79, PosZ);
            Wire.S32(d, o + 83, BlockingEndDate);
            Wire.U8(d, o + 87, NameFlags);
            return Size;
        }

        public static GsCharacList Read(ReadOnlySpan<byte> d, int o)
        {
            var c = new GsCharacList
            {
                UniqueId = Wire.S32(d, o + 0),
                Name = Wire.FixedBytes(d, o + 4, 13),
                Nick = Wire.FixedBytes(d, o + 17, 13),
                Zone = Wire.S8(d, o + 30),
                Sex = Wire.S8(d, o + 31), Face = Wire.S8(d, o + 32), Hair = Wire.S8(d, o + 33), Group = Wire.S8(d, o + 34),
                AmorType = Wire.S8(d, o + 35), PantsType = Wire.S8(d, o + 36), CapType = Wire.S8(d, o + 37), AdaeType = Wire.S8(d, o + 38),
                ShoesType = Wire.S8(d, o + 39), BackPackType = Wire.S8(d, o + 40), WeaponType = Wire.S8(d, o + 41), MaskType = Wire.S8(d, o + 42),
                Amor = Wire.S16(d, o + 43), Pants = Wire.S16(d, o + 45), Cap = Wire.S16(d, o + 47),
                Adae = Wire.S16(d, o + 49), Shoes = Wire.S16(d, o + 51), BackPack = Wire.S16(d, o + 53),
                SelectWeaponId = Wire.S16(d, o + 55),
                Mask = Wire.S16(d, o + 57),
                WeaponStrength = Wire.U8(d, o + 59),
                Str = Wire.S16(d, o + 60), Zen = Wire.S16(d, o + 62), Intelligence = Wire.S16(d, o + 64),
                Constitution = Wire.S16(d, o + 66), Dexterity = Wire.S16(d, o + 68),
                InnerLevel = Wire.S16(d, o + 70),
                Class = Wire.S8(d, o + 72), ClassGrade = Wire.S8(d, o + 73), Gm = Wire.S8(d, o + 74),
                PosX = Wire.F32(d, o + 75), PosZ = Wire.F32(d, o + 79),
                BlockingEndDate = Wire.S32(d, o + 83),
                NameFlags = Wire.U8(d, o + 87),
            };
            return c;
        }
    }

    // --- 8 CHARAC_REQ_LIST (C->S) : 3 bytes ---
    public sealed class MsgCharacReqList : NdPacket
    {
        public const int Size = 3;
        public override int MsgId => Nd.Packets.MsgId.CharacReqList;
        public override int WireSize => Size;
        public override int Write(Span<byte> d) => WritePrefix(d);
        public static MsgCharacReqList Parse(ReadOnlySpan<byte> d) => new MsgCharacReqList();
    }

    // --- 9 CHARAC_LIST (S->C) : nCharac + List[3] + uiStateFlag, 272 bytes ---
    public sealed class MsgCharacList : NdPacket
    {
        public const int Size = 272;
        public const int MaxChar = 3;

        public sbyte          NCharac;
        public GsCharacList[]  List = { new GsCharacList(), new GsCharacList(), new GsCharacList() };
        public uint            StateFlag;

        public override int MsgId => Nd.Packets.MsgId.CharacList;
        public override int WireSize => Size;

        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);          // 3
            Wire.S8(d, o, NCharac); o += 1;  // 3
            for (int i = 0; i < MaxChar; i++)
                o += (List[i] ?? new GsCharacList()).Write(d, o);   // 4, 92, 180
            Wire.U32(d, o, StateFlag); o += 4;                       // 268
            return o;                                               // 272
        }

        public static MsgCharacList Parse(ReadOnlySpan<byte> d)
        {
            var p = new MsgCharacList();
            int o = Wire.PrefixSize;         // 3
            p.NCharac = Wire.S8(d, o); o += 1;
            for (int i = 0; i < MaxChar; i++) { p.List[i] = GsCharacList.Read(d, o); o += GsCharacList.Size; }
            p.StateFlag = Wire.U32(d, o);
            return p;
        }
    }

    // --- 10 CHARAC_SELECT (C->S) : name, 16 bytes ---
    public sealed class MsgCharacSelect : NdPacket
    {
        public const int Size = 16;
        public byte[] SelectName = new byte[13];

        public string NameText { get => Wire.CStr(SelectName, 0, 13); set => Wire.CStr(SelectName, 0, 13, value); }

        public override int MsgId => Nd.Packets.MsgId.CharacSelect;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.FixedBytes(d, o, 13, SelectName);
            return Size;
        }
        public static MsgCharacSelect Parse(ReadOnlySpan<byte> d)
            => new MsgCharacSelect { SelectName = Wire.FixedBytes(d, Wire.PrefixSize, 13) };
    }

    // --- 11 CHARAC_CREATE (C->S) : 24 bytes ---
    public sealed class MsgCharacCreate : NdPacket
    {
        public const int Size = 26;   // 3 + name13 + 5 stats + (sex,face,hair)3 + initZone1 + clothSet1
        public byte[] Name = new byte[13];
        public sbyte  Constitution, Zen, Intelligence, Dexterity, Str;
        public sbyte  Sex, Face, Hair;
        public sbyte  InitZone;
        public byte   ClothSet;

        public string NameText { get => Wire.CStr(Name, 0, 13); set => Wire.CStr(Name, 0, 13, value); }

        public override int MsgId => Nd.Packets.MsgId.CharacCreate;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.FixedBytes(d, o, 13, Name); o += 13;
            Wire.S8(d, o++, Constitution); Wire.S8(d, o++, Zen); Wire.S8(d, o++, Intelligence);
            Wire.S8(d, o++, Dexterity); Wire.S8(d, o++, Str);
            Wire.S8(d, o++, Sex); Wire.S8(d, o++, Face); Wire.S8(d, o++, Hair);
            Wire.S8(d, o++, InitZone);
            Wire.U8(d, o++, ClothSet);
            return o;
        }
        public static MsgCharacCreate Parse(ReadOnlySpan<byte> d)
        {
            var p = new MsgCharacCreate();
            int o = Wire.PrefixSize;
            p.Name = Wire.FixedBytes(d, o, 13); o += 13;
            p.Constitution = Wire.S8(d, o++); p.Zen = Wire.S8(d, o++); p.Intelligence = Wire.S8(d, o++);
            p.Dexterity = Wire.S8(d, o++); p.Str = Wire.S8(d, o++);
            p.Sex = Wire.S8(d, o++); p.Face = Wire.S8(d, o++); p.Hair = Wire.S8(d, o++);
            p.InitZone = Wire.S8(d, o++);
            p.ClothSet = Wire.U8(d, o++);
            return p;
        }
    }

    // --- 12 CHARAC_DELETE (C->S) : name + password, 29 bytes ---
    public sealed class MsgCharacDelete : NdPacket
    {
        public const int Size = 29;   // 3 + 13 + 13
        public byte[] Name = new byte[13];
        public byte[] Password = new byte[13];

        public string NameText { get => Wire.CStr(Name, 0, 13); set => Wire.CStr(Name, 0, 13, value); }
        public string PasswordText { get => Wire.CStr(Password, 0, 13); set => Wire.CStr(Password, 0, 13, value); }

        public override int MsgId => Nd.Packets.MsgId.CharacDelete;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.FixedBytes(d, o, 13, Name); o += 13;
            Wire.FixedBytes(d, o, 13, Password); o += 13;
            return o;
        }
        public static MsgCharacDelete Parse(ReadOnlySpan<byte> d)
        {
            var p = new MsgCharacDelete();
            int o = Wire.PrefixSize;
            p.Name = Wire.FixedBytes(d, o, 13); o += 13;
            p.Password = Wire.FixedBytes(d, o, 13);
            return p;
        }
    }

    // --- 13 CHARAC_RESULT (S->C) : 5 bytes ---
    public sealed class MsgCharacResult : NdPacket
    {
        public const int Size = 5;
        public byte PrevCommand;   // 11=create, 12=delete
        public byte Result;        // 0=OK

        public override int MsgId => Nd.Packets.MsgId.CharacResult;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U8(d, o, PrevCommand); Wire.U8(d, o + 1, Result);
            return Size;
        }
        public static MsgCharacResult Parse(ReadOnlySpan<byte> d)
            => new MsgCharacResult { PrevCommand = Wire.U8(d, Wire.PrefixSize), Result = Wire.U8(d, Wire.PrefixSize + 1) };
    }

    // --- 16 CHARAC_INFO (S->C) : own stats, 133 bytes (CODEX int-widened) ---
    public sealed class MsgCharacInfo : NdPacket
    {
        public const int Size = 133;

        public sbyte Zone;
        public int   CurLifePower, CurForcePower, CurConcentrationPower;
        public int   MaxLifePower, MaxForcePower, MaxConcentrationPower;
        public short LeftPoint, LeftMasteryPoint;
        public uint  LeftJin;      // EXP
        public uint  LeftGong;
        public short Retribution;
        public int   Honor;
        public short Showdowm;
        public float InitPosX, InitPosZ;
        public int   TimeCode;
        public short StandingObb;
        public int   WoundRate, InsideWoundRate;
        public ushort FatigueRate;
        public short FuryParameter;
        public sbyte RespawnServerNo;
        public byte[] RespawnPosName = new byte[13];
        public int   WinScore, LostScore, TieScore, SeriesCount;
        public int   MaxSeriesWinCount, MaxSeriesLoseCount, AbuseCount;
        public sbyte PrevMatchResult;
        public sbyte Class, ClassGrade;
        public int   Contribution;
        public sbyte Gm;
        public int   LeftLevelupLimit;
        public byte  ModeBits;            // ucPeaceMode:2 | ucHideMode:1 | ucClothView:1 | reserve:4
        public byte  PrevDeadFrom;
        public byte  DeadCountInCombat;
        public ushort LeftCombatPenaltyTime;

        public string RespawnPosText => Wire.CStr(RespawnPosName, 0, 13);

        public override int MsgId => Nd.Packets.MsgId.CharacInfo;
        public override int WireSize => Size;

        public override int Write(Span<byte> d)
        {
            WritePrefix(d);
            Wire.S8(d, 3, Zone);
            Wire.S32(d, 4, CurLifePower); Wire.S32(d, 8, CurForcePower); Wire.S32(d, 12, CurConcentrationPower);
            Wire.S32(d, 16, MaxLifePower); Wire.S32(d, 20, MaxForcePower); Wire.S32(d, 24, MaxConcentrationPower);
            Wire.S16(d, 28, LeftPoint); Wire.S16(d, 30, LeftMasteryPoint);
            Wire.U32(d, 32, LeftJin); Wire.U32(d, 36, LeftGong);
            Wire.S16(d, 40, Retribution);
            Wire.S32(d, 42, Honor);
            Wire.S16(d, 46, Showdowm);
            Wire.F32(d, 48, InitPosX); Wire.F32(d, 52, InitPosZ);
            Wire.S32(d, 56, TimeCode);
            Wire.S16(d, 60, StandingObb);
            Wire.S32(d, 62, WoundRate); Wire.S32(d, 66, InsideWoundRate);
            Wire.U16(d, 70, FatigueRate);
            Wire.S16(d, 72, FuryParameter);
            Wire.S8(d, 74, RespawnServerNo);
            Wire.FixedBytes(d, 75, 13, RespawnPosName);
            Wire.S32(d, 88, WinScore); Wire.S32(d, 92, LostScore); Wire.S32(d, 96, TieScore); Wire.S32(d, 100, SeriesCount);
            Wire.S32(d, 104, MaxSeriesWinCount); Wire.S32(d, 108, MaxSeriesLoseCount); Wire.S32(d, 112, AbuseCount);
            Wire.S8(d, 116, PrevMatchResult);
            Wire.S8(d, 117, Class); Wire.S8(d, 118, ClassGrade);
            Wire.S32(d, 119, Contribution);
            Wire.S8(d, 123, Gm);
            Wire.S32(d, 124, LeftLevelupLimit);
            Wire.U8(d, 128, ModeBits);
            Wire.U8(d, 129, PrevDeadFrom);
            Wire.U8(d, 130, DeadCountInCombat);
            Wire.U16(d, 131, LeftCombatPenaltyTime);
            return Size;
        }

        public static MsgCharacInfo Parse(ReadOnlySpan<byte> d)
        {
            var p = new MsgCharacInfo
            {
                Zone = Wire.S8(d, 3),
                CurLifePower = Wire.S32(d, 4), CurForcePower = Wire.S32(d, 8), CurConcentrationPower = Wire.S32(d, 12),
                MaxLifePower = Wire.S32(d, 16), MaxForcePower = Wire.S32(d, 20), MaxConcentrationPower = Wire.S32(d, 24),
                LeftPoint = Wire.S16(d, 28), LeftMasteryPoint = Wire.S16(d, 30),
                LeftJin = Wire.U32(d, 32), LeftGong = Wire.U32(d, 36),
                Retribution = Wire.S16(d, 40),
                Honor = Wire.S32(d, 42),
                Showdowm = Wire.S16(d, 46),
                InitPosX = Wire.F32(d, 48), InitPosZ = Wire.F32(d, 52),
                TimeCode = Wire.S32(d, 56),
                StandingObb = Wire.S16(d, 60),
                WoundRate = Wire.S32(d, 62), InsideWoundRate = Wire.S32(d, 66),
                FatigueRate = Wire.U16(d, 70),
                FuryParameter = Wire.S16(d, 72),
                RespawnServerNo = Wire.S8(d, 74),
                RespawnPosName = Wire.FixedBytes(d, 75, 13),
                WinScore = Wire.S32(d, 88), LostScore = Wire.S32(d, 92), TieScore = Wire.S32(d, 96), SeriesCount = Wire.S32(d, 100),
                MaxSeriesWinCount = Wire.S32(d, 104), MaxSeriesLoseCount = Wire.S32(d, 108), AbuseCount = Wire.S32(d, 112),
                PrevMatchResult = Wire.S8(d, 116),
                Class = Wire.S8(d, 117), ClassGrade = Wire.S8(d, 118),
                Contribution = Wire.S32(d, 119),
                Gm = Wire.S8(d, 123),
                LeftLevelupLimit = Wire.S32(d, 124),
                ModeBits = Wire.U8(d, 128),
                PrevDeadFrom = Wire.U8(d, 129),
                DeadCountInCombat = Wire.U8(d, 130),
                LeftCombatPenaltyTime = Wire.U16(d, 131),
            };
            return p;
        }
    }

    // --- 153 SV_ENTER_GSERVER (C->S) : ID + enterCheck + uid, 70 bytes ---
    public sealed class MsgSvEnterGServer : NdPacket
    {
        public const int Size = 70;   // 3 + 61 + 4 + 2
        public byte[] Id = new byte[61];
        public uint   EnterCheck;     // client sends 0
        public ushort UniqueId;

        public string IdText { get => Wire.CStr(Id, 0, 61); set => Wire.CStr(Id, 0, 61, value); }

        public override int MsgId => Nd.Packets.MsgId.SvEnterGServer;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.FixedBytes(d, o, 61, Id); o += 61;
            Wire.U32(d, o, EnterCheck); o += 4;
            Wire.U16(d, o, UniqueId); o += 2;
            return o;
        }
        public static MsgSvEnterGServer Parse(ReadOnlySpan<byte> d)
        {
            var p = new MsgSvEnterGServer();
            int o = Wire.PrefixSize;
            p.Id = Wire.FixedBytes(d, o, 61); o += 61;
            p.EnterCheck = Wire.U32(d, o); o += 4;
            p.UniqueId = Wire.U16(d, o);
            return p;
        }
    }

    // --- 154 SV_ENTER_RESULT (S->C) : 5 bytes ---
    public sealed class MsgSvEnterResult : NdPacket
    {
        public const int Size = 5;
        public sbyte Result;      // 0 = success
        public sbyte Reserved;

        public override int MsgId => Nd.Packets.MsgId.SvEnterResult;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.S8(d, o, Result); Wire.S8(d, o + 1, Reserved);
            return Size;
        }
        public static MsgSvEnterResult Parse(ReadOnlySpan<byte> d)
            => new MsgSvEnterResult { Result = Wire.S8(d, Wire.PrefixSize), Reserved = Wire.S8(d, Wire.PrefixSize + 1) };
    }

    // --- SEQ_GS (S->C) : header + ucMessage + ucStartSeq, 4 bytes ---
    public sealed class MsgSeqGs : NdPacket
    {
        public const int Size = 4;
        public byte StartSeq;

        public override int MsgId => Nd.Packets.MsgId.SeqNumberGs;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U8(d, o, StartSeq);
            return Size;
        }
        public static MsgSeqGs Parse(ReadOnlySpan<byte> d)
            => new MsgSeqGs { StartSeq = Wire.U8(d, Wire.PrefixSize) };
    }
}
