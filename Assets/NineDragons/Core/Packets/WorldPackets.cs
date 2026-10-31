// ============================================================================
//  WorldPackets.cs - zone / monster / state / death packets.
//  Ground truth: src/nd_gs_packets.h (byte-verified) + client packet_for_zone_monster.h.
// ============================================================================
using System;

namespace Nd.Packets
{
    // --- 25 ZONE_POSITIONING (S->C) : 18 bytes ---
    public sealed class MsgZonePositioning : NdPacket
    {
        public const int Size = 18;
        public sbyte Zone;
        public float InitPosX, InitPosZ;
        public short StandingObb;
        public int   TimeCode;

        public override int MsgId => Nd.Packets.MsgId.ZonePositioning;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.S8(d, o, Zone);
            Wire.F32(d, o + 1, InitPosX); Wire.F32(d, o + 5, InitPosZ);
            Wire.S16(d, o + 9, StandingObb);
            Wire.S32(d, o + 11, TimeCode);
            return Size;
        }
        public static MsgZonePositioning Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgZonePositioning
            {
                Zone = Wire.S8(d, o),
                InitPosX = Wire.F32(d, o + 1), InitPosZ = Wire.F32(d, o + 5),
                StandingObb = Wire.S16(d, o + 9), TimeCode = Wire.S32(d, o + 11),
            };
        }
    }

    // --- 17 ZONE_RESTART (S->C) : revive player (pos + hp), 18 bytes ---
    public sealed class MsgZoneRestart : NdPacket
    {
        public const int Size = 18;
        public float RestartPosX, RestartPosZ;
        public uint  Jin;
        public short VitalPower;    // new HP (WORD, <= 32767)
        public sbyte SeverNum;      // server/zone (-1 = village)

        public override int MsgId => Nd.Packets.MsgId.ZoneRestart;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.F32(d, o, RestartPosX); Wire.F32(d, o + 4, RestartPosZ);
            Wire.U32(d, o + 8, Jin);
            Wire.S16(d, o + 12, VitalPower);
            Wire.S8(d, o + 14, SeverNum);
            return Size;
        }
        public static MsgZoneRestart Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgZoneRestart
            {
                RestartPosX = Wire.F32(d, o), RestartPosZ = Wire.F32(d, o + 4),
                Jin = Wire.U32(d, o + 8), VitalPower = Wire.S16(d, o + 12), SeverNum = Wire.S8(d, o + 14),
            };
        }
    }

    // --- 32 CHARAC_CTRL (S->C) : control player (dying / coma), 8 bytes ---
    public sealed class MsgCharacCtrl : NdPacket
    {
        public const int Size = 8;
        public sbyte  Flag;   // CharacCtrlFlag.Binsa=0 / Dead=1
        public ushort Uid;
        public ushort Time;   // seconds; 0xffff = until release

        public override int MsgId => Nd.Packets.MsgId.CharacCtrl;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.S8(d, o, Flag); Wire.U16(d, o + 1, Uid); Wire.U16(d, o + 3, Time);
            return Size;
        }
        public static MsgCharacCtrl Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgCharacCtrl { Flag = Wire.S8(d, o), Uid = Wire.U16(d, o + 1), Time = Wire.U16(d, o + 3) };
        }
    }

    // --- 33 CHARAC_SELECT_MODE (C->S) : choose respawn point, 5 bytes ---
    public sealed class MsgCharacSelectMode : NdPacket
    {
        public const int Size = 5;
        public sbyte Mode;             // CharacSelMode.*
        public sbyte SelectPosition;   // position index when mode needs it

        public override int MsgId => Nd.Packets.MsgId.CharacSelectMode;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.S8(d, o, Mode); Wire.S8(d, o + 1, SelectPosition);
            return Size;
        }
        public static MsgCharacSelectMode Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgCharacSelectMode { Mode = Wire.S8(d, o), SelectPosition = Wire.S8(d, o + 1) };
        }
    }

    // --- 19 STATE_CHANGE (C<->S) : peace/battle toggle, 4 bytes ---
    public sealed class MsgStateChange : NdPacket
    {
        public const int Size = 4;
        public byte Mode;   // StateChangeMode.* (3 peace req, 4 battle req)

        public override int MsgId => Nd.Packets.MsgId.StateChange;
        public override int WireSize => Size;
        public override int Write(Span<byte> d) { int o = WritePrefix(d); Wire.U8(d, o, Mode); return Size; }
        public static MsgStateChange Parse(ReadOnlySpan<byte> d) => new MsgStateChange { Mode = Wire.U8(d, Wire.PrefixSize) };
    }

    // --- 19 STATE_CHANGE_RESULT (S->C) : self toggle ack, 5 bytes ---
    public sealed class MsgStateChangeResult : NdPacket
    {
        public const int Size = 5;
        public byte Mode;
        public byte Result;   // 0 = OK

        public override int MsgId => Nd.Packets.MsgId.StateChange;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U8(d, o, Mode); Wire.U8(d, o + 1, Result);
            return Size;
        }
        public static MsgStateChangeResult Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgStateChangeResult { Mode = Wire.U8(d, o), Result = Wire.U8(d, o + 1) };
        }
    }

    // --- 40 MONSTER_EXIST (S->C) : mob in view, 20 bytes ---
    public sealed class MsgMonsterExist : NdPacket
    {
        public const int Size = 20;
        public ushort MobType;
        public ushort MobId;
        public float  X, Z;
        public sbyte  MobVitalRate;   // 100 = full
        public uint   ConditionBit;

        public override int MsgId => Nd.Packets.MsgId.MonsterExist;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U16(d, o, MobType); Wire.U16(d, o + 2, MobId);
            Wire.F32(d, o + 4, X); Wire.F32(d, o + 8, Z);
            Wire.S8(d, o + 12, MobVitalRate);
            Wire.U32(d, o + 13, ConditionBit);
            return Size;
        }
        public static MsgMonsterExist Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgMonsterExist
            {
                MobType = Wire.U16(d, o), MobId = Wire.U16(d, o + 2),
                X = Wire.F32(d, o + 4), Z = Wire.F32(d, o + 8),
                MobVitalRate = Wire.S8(d, o + 12), ConditionBit = Wire.U32(d, o + 13),
            };
        }
    }

    // --- 30 MONSTER_MOVE (S->C) : mob moves (fX,fZ)->(fGoto), 25 bytes ---
    public sealed class MsgMonsterMove : NdPacket
    {
        public const int Size = 25;
        public ushort MobType;
        public ushort MobId;
        public float  X, Z;
        public float  GotoX, GotoZ;
        public sbyte  MobVitalRate;
        public byte   SpeedByte;   // ucMobWalkMode:1 | ucSpeedRate:7  (speed/5)

        public int  WalkMode   { get => SpeedByte & 0x1;         set => SpeedByte = (byte)((SpeedByte & 0xFE) | (value & 0x1)); }
        public int  SpeedRate  { get => (SpeedByte >> 1) & 0x7F; set => SpeedByte = (byte)((SpeedByte & 0x01) | ((value & 0x7F) << 1)); }

        public override int MsgId => Nd.Packets.MsgId.MonsterMove;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U16(d, o, MobType); Wire.U16(d, o + 2, MobId);
            Wire.F32(d, o + 4, X); Wire.F32(d, o + 8, Z);
            Wire.F32(d, o + 12, GotoX); Wire.F32(d, o + 16, GotoZ);
            Wire.S8(d, o + 20, MobVitalRate);
            Wire.U8(d, o + 21, SpeedByte);
            return Size;
        }
        public static MsgMonsterMove Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgMonsterMove
            {
                MobType = Wire.U16(d, o), MobId = Wire.U16(d, o + 2),
                X = Wire.F32(d, o + 4), Z = Wire.F32(d, o + 8),
                GotoX = Wire.F32(d, o + 12), GotoZ = Wire.F32(d, o + 16),
                MobVitalRate = Wire.S8(d, o + 20), SpeedByte = Wire.U8(d, o + 21),
            };
        }
    }

    // --- 31 MONSTER_VITAL (S->C) : 6 bytes, vitalRate<0 = mob DIED ---
    public sealed class MsgMonsterVital : NdPacket
    {
        public const int Size = 6;
        public ushort MobId;
        public sbyte  MobVitalRate;

        public override int MsgId => Nd.Packets.MsgId.MonsterVital;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U16(d, o, MobId); Wire.S8(d, o + 2, MobVitalRate);
            return Size;
        }
        public static MsgMonsterVital Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgMonsterVital { MobId = Wire.U16(d, o), MobVitalRate = Wire.S8(d, o + 2) };
        }
    }

    // --- 50 B_ATKRES_FROM_MOB (S->C) : mob hits player, 21 bytes ---
    //  NOTE: sDamage = player's REMAINING HP (Set_current_lifepower), NOT the damage.
    public sealed class MsgBAtkResFromMob : NdPacket
    {
        public const int Size = 21;
        public ushort AttackerId;   // mob
        public ushort DefenderId;   // player (m_Uniqueid)
        public byte   AtkByte;      // ucToType:1|ucAtkInfo:3|ucAtkType:2|ucAddPassiveType:2
        public int    Damage;       // = player remaining HP (<=0 dead)
        public sbyte  PassiveTitle;
        public float  ItemDropPosX, ItemDropPosZ;

        public override int MsgId => Nd.Packets.MsgId.BAtkResFromMob;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.U16(d, o, AttackerId); Wire.U16(d, o + 2, DefenderId);
            Wire.U8(d, o + 4, AtkByte);
            Wire.S32(d, o + 5, Damage);
            Wire.S8(d, o + 9, PassiveTitle);
            Wire.F32(d, o + 10, ItemDropPosX); Wire.F32(d, o + 14, ItemDropPosZ);
            return Size;
        }
        public static MsgBAtkResFromMob Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgBAtkResFromMob
            {
                AttackerId = Wire.U16(d, o), DefenderId = Wire.U16(d, o + 2),
                AtkByte = Wire.U8(d, o + 4), Damage = Wire.S32(d, o + 5), PassiveTitle = Wire.S8(d, o + 9),
                ItemDropPosX = Wire.F32(d, o + 10), ItemDropPosZ = Wire.F32(d, o + 14),
            };
        }
    }

    // --- 130 NPC_TRADE_REQ (C->S) : buy/sell/heal/gamble at an NPC, 15 bytes (packet_for_items_trade.h:710) ---
    //  cBuyFromNPC = EN_NPC_TRADE_MODE (0 sell / 1 buy / 5 heal / 7 move-village / …). When BUYING,
    //  ItemNo is the index in the NPC's sale list; when SELLING, ItemNo is the inventory slot.
    public sealed class MsgNpcTradeReq : NdPacket
    {
        public const int Size = 15;      // prefix(3) + 12
        public sbyte  Mode;              // cBuyFromNPC (EN_NPC_TRADE_MODE)
        public ushort NpcId;             // usNPCID
        public byte   ItemNo;            // ucItemNo (sale-list index on buy, inventory slot on sell)
        public sbyte  ItemType;          // cItemType
        public short  ItemId;            // sItemID
        public byte   Count;             // ucCount
        public uint   NeedMoney;         // uiNeedMoney (price on buy; 0 on sell)

        public override int MsgId => Nd.Packets.MsgId.NpcTrade;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.S8(d, o, Mode);
            Wire.U16(d, o + 1, NpcId);
            Wire.U8(d, o + 3, ItemNo);
            Wire.S8(d, o + 4, ItemType);
            Wire.S16(d, o + 5, ItemId);
            Wire.U8(d, o + 7, Count);
            Wire.U32(d, o + 8, NeedMoney);
            return Size;
        }
    }

    // --- 64 SELF_CHANNEL_START_REQUEST (C->S) : start/stop vận khí (self-recovery channel), 7 bytes ---
    //  (packet_for_battle.h:130). cMode 0 = start, 2 = stop; carries the vận khí skill's learn-slot + id.
    //  Server replies SELF_CHANNEL_CTRL (op64) with the recovery result and broadcasts SELF_CHANNEL_INFO
    //  (op65) so others see the channel effect.
    public sealed class MsgSelfChannelStartReq : NdPacket
    {
        public const int Size = 7;      // prefix(3) + 4
        public sbyte Mode;              // cMode (0 start / 2 stop)
        public byte  LearnSkillIndex;   // ucLearnSkillIndex
        public short SkillId;           // skillID

        public override int MsgId => Nd.Packets.MsgId.SelfChannel;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);
            Wire.S8(d, o, Mode);
            Wire.U8(d, o + 1, LearnSkillIndex);
            Wire.S16(d, o + 2, SkillId);
            return Size;
        }
    }
}
