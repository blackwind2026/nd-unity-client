// ============================================================================
//  WorldState.cs - M5 live game-state model.
//
//  Server-authoritative: every field here is written only from a parsed
//  server->client packet (CHARAC_INFO 16, SET_POWER 105, INVEN_INFO 121,
//  MONSTER_EXIST 40, MONSTER_MOVE 30, MONSTER_VITAL 31, ZONE_POSITIONING 25,
//  ZONE_RESTART 17, SET_LEVEL 104, CHARAC_SET_STATE 18, B_ATKRES_FROM_MOB 50).
//  The single exception is our own position, which is updated optimistically
//  when we SEND a ZONE_MOVE (24) - exactly what the C++ client does, since this
//  server stores the reported current pos and does not echo it back to the mover.
//
//  These are plain C# POCOs (no net10-only APIs) so Unity (M6/M7) can bind them.
// ============================================================================
using System.Collections.Generic;
using Nd.Net;                 // M1 CharacInfo
using Nd.Packets;             // M2 typed packets
using NineDragons.Data;       // M3 MonsterTable (optional enrichment)

namespace Nd.Session
{
    /// <summary>The local player's live state.</summary>
    public sealed class MyCharacter
    {
        public ushort Uid;
        public string Name = "";
        public sbyte  Zone;
        public sbyte  Class, ClassGrade;
        // display strings from client tables (grade/roll name + current title) — not in the wire
        // CHARAC_INFO, filled by the Unity layer where known (else empty → header shows what it can).
        public string GradeName = "";   // e.g. "Thiên Ngoại Tiên" (g_LevelName[GradeRank])
        public string NickTitle = "";   // current epithet fallback, e.g. "vô danh tiểu tốt"
        public int    NickTitleId;      // danh hiệu title-id → resolve NDTextDB "NicNameRef_title" (sect 22); 0 = chưa có (network chưa đẩy)

        // 품계 grade-rank + sublevel ("N thành") are DERIVED from InnerLevel via g_UserLevelTable
        // (grade_name.md): innerLevel = (grade-1)*12 + sublevel, grade 1-20, sublevel 1-12.
        // InnerLevel comes from the char-select row / level-up packets (NOT the in-game CHARAC_INFO).
        public int    InnerLevel;
        public int    GradeRank => InnerLevel > 0 ? ((InnerLevel - 1) / 12) + 1 : 0;
        public int    SubLevel  => InnerLevel > 0 ? ((InnerLevel - 1) % 12) + 1 : 0;

        // position (X,Z world plane). Seeded from CHARAC_INFO, corrected by
        // ZONE_POSITIONING/ZONE_RESTART, advanced by our own ZONE_MOVE sends.
        public float  X, Z;

        // power triplet: cur + max (int-widened CODEX build).
        public int    CurLife, MaxLife;
        public int    CurForce, MaxForce;
        public int    CurConc, MaxConc;

        public uint   Exp;          // LeftJin
        public int    Honor;        // fame / Danh vọng
        public short  LeftPoint, LeftMasteryPoint;

        // extra CHARAC_INFO(16) fields the Nhân-vật panel shows (all present in the 133-byte wire
        // payload — CharacInfo.Parse). These are REAL, unlike the attack/defence/avoid lines which
        // the client itself COMPUTES client-side (m_P_*/m_M_* property objects) and are not on the wire.
        public int    WoundRate, InsideWoundRate;   // Ngoại thương / Nội thương
        public int    Contribution;                 // Cống hiến bang
        public short  Retribution;                  // Tai tiếng
        public int    FuryParameter;                // Phẫn nộ gauge, 0..10000 (bar % = /10000)
        public ushort FatigueRate;

        // 5 base attributes — carried by the char-select row (CharacListEntry), NOT by CHARAC_INFO;
        // applied via ApplySelectEntry at selection. 0 until then. Overwritten with the FINAL values
        // (base+item+seal) when the server pushes op210 sub17.
        public short  Str, Zen, Intelligence, Constitution, Dexterity;

        // worn-equipment item ids (from the char-select row) — for the paperdoll icons.
        public int    Sex, Amor, Pants, Cap, Weapon, Gloves, Shoes;   // Gloves = 아대 Adae, Shoes = giày
        public sbyte  WeaponType;   // object-type vũ khí đang mặc: 0 thường / 15 cash / 19 ngoại trang (id trùng nhau → cần type)

        // Server-authoritative COMPUTED stats — nd-world op210 (MSGNDPush) push, sub16/sub17.
        // These are NOT in the standard CHARAC_INFO wire; the client normally computes them, but
        // nd-world pushes the exact numbers (equipment+seal+buff folded in). StatsPushed stays false
        // until sub16 arrives → the panel shows "—" so we never display a stale/base guess.
        public bool StatsPushed;
        public int AtkMin, AtkMax, AtkRate, Defence, Avoid;          // physical (sub16)
        public int MAtkMin, MAtkMax, MAtkRate, MDefence, MAvoid;     // khí công (sub16)
        public int MoveSpeed;                                        // sub16 (14th)
        public bool ResistPushed;
        public int ResYin, ResYang, ResSoul, ResSpirit;             // Âm/Dương/Hồn/Thần (sub17)

        /// <summary>op210 sub16: "def mdef avoid mavoid atkRate matkRate crit mcrit critAvoid
        /// minDmg maxDmg mMinDmg mMaxDmg moveSpeed" (XNetwork_item_trade.cpp:9316).</summary>
        public void ApplyStatPush16(int[] n)
        {
            if (n == null || n.Length < 6) return;
            int G(int i) => i < n.Length ? n[i] : 0;
            Defence = G(0); MDefence = G(1); Avoid = G(2); MAvoid = G(3);
            AtkRate = G(4); MAtkRate = G(5);
            AtkMin = G(9); AtkMax = G(10); MAtkMin = G(11); MAtkMax = G(12); MoveSpeed = G(13);
            StatsPushed = true;
        }

        /// <summary>op210 sub17: "str con zen int dex yin yang soul spirit" — FINAL attributes
        /// (base+item+seal) + 4 elemental resistances (XNetwork_item_trade.cpp:9350).</summary>
        public void ApplyStatPush17(int[] n)
        {
            if (n == null || n.Length < 5) return;
            int G(int i) => i < n.Length ? n[i] : 0;
            Str = (short)G(0); Constitution = (short)G(1); Zen = (short)G(2);
            Intelligence = (short)G(3); Dexterity = (short)G(4);
            ResYin = G(5); ResYang = G(6); ResSoul = G(7); ResSpirit = G(8);
            ResistPushed = true;
        }

        /// <summary>op210 sub13 (_XDEF_ND_PUSHSUB_MAXSTAT): "maxLife maxForce maxConc" — the
        /// cash-folded, server-authoritative MAX triplet. CHARAC_INFO's max is only the stale
        /// BASE, so this is what makes the HUD show cur ≤ max. Direct assign, no accumulation
        /// (XNetwork_item_trade.cpp:9271), guard maxL>0 && maxF>=0 && maxC>=0.</summary>
        public void ApplyMaxStatPush(int[] n)
        {
            if (n == null || n.Length < 3) return;
            if (n[0] > 0 && n[1] >= 0 && n[2] >= 0)
            { MaxLife = n[0]; MaxForce = n[1]; MaxConc = n[2]; }
        }

        // inventory (from INVEN_INFO 121)
        public uint   Money;
        public int    BackPackExtend;   // cBackPackExtend: how far the bags are opened (server-authoritative)
        public readonly List<InvenItem> Inventory = new List<InvenItem>();

        public void ApplyCharacInfo(CharacInfo ci, string name, ushort uid)
        {
            Uid = uid;
            Name = name;
            Zone = ci.Zone;
            Class = ci.Class; ClassGrade = ci.ClassGrade;
            X = ci.InitPosX; Z = ci.InitPosZ;
            CurLife = ci.CurLife; MaxLife = ci.MaxLife;
            CurForce = ci.CurForce; MaxForce = ci.MaxForce;
            CurConc = ci.CurConc; MaxConc = ci.MaxConc;
            Exp = ci.LeftJin;
            Honor = ci.Honor;
            LeftPoint = ci.LeftPoint; LeftMasteryPoint = ci.LeftMasteryPoint;
            WoundRate = ci.WoundRate; InsideWoundRate = ci.InsideWoundRate;
            Contribution = ci.Contribution; Retribution = ci.Retribution;
            FuryParameter = ci.FuryParameter; FatigueRate = ci.FatigueRate;
        }

        /// <summary>Copy the 5 base attributes from the selected char-list row (they are not in
        /// CHARAC_INFO). Call right after SelectCharacter with that account's chosen entry.</summary>
        public void ApplySelectEntry(CharacListEntry e)
        {
            if (e == null) return;
            Str = e.Str; Zen = e.Zen; Intelligence = e.Intelligence;
            Constitution = e.Constitution; Dexterity = e.Dexterity;
            // worn-equipment ids from the char-select row (armor/pants/cap/gloves/shoes/weapon) — for the doll
            Sex = e.Sex; Amor = e.Amor; Pants = e.Pants; Cap = e.Cap; Weapon = e.Weapon;
            WeaponType = e.WeaponType;   // giữ object-type để hiển thị đúng vũ khí type 15/19 (không ép về 0)
            Gloves = e.Adae; Shoes = e.Shoes;
            // grade-rank/sublevel are derived from InnerLevel (grade_name.md); the char-select row is
            // the authoritative source for the local user (the in-game CHARAC_INFO omits it).
            InnerLevel = e.InnerLevel;
        }

        /// <summary>SET_POWER 105: bMax=1 sets the MAX triplet, bMax=0 sets CURRENT.</summary>
        public void ApplySetPower(MsgSetPower p)
        {
            if (p.Max != 0)
            {
                MaxLife = p.LifePower; MaxForce = p.ForcePower; MaxConc = p.ConcentrationPower;
            }
            else
            {
                CurLife = p.LifePower; CurForce = p.ForcePower; CurConc = p.ConcentrationPower;
            }
        }

        public void ApplyInventory(MsgInvenInfo p)
        {
            // mode 1 (inventory) = authoritative snapshot: reset money + item list + bag-extend.
            if (p.InvenMode == MsgInvenInfo.ModeInventory)
            {
                Money = p.Money;
                BackPackExtend = p.BackPackExtend;
                Inventory.Clear();
                Inventory.AddRange(p.Items);
                DeriveEquipFromInventory();
                return;
            }
            // mode 2 (append) = continuation for a bag that overflowed 4095 B: merge by slot, keep money.
            if (p.InvenMode == MsgInvenInfo.ModeAppend)
            {
                foreach (var it in p.Items) { Inventory.RemoveAll(x => x.Slot == it.Slot); Inventory.Add(it); }
                return;
            }
            // other modes (elixir 0, costume 6, result 7) are NOT the item list — ignore, so the real bag and
            // money set by the mode-1 packet are NOT wiped (this was the empty-bag / garbage-money bug).
        }

        /// <summary>Apply an INVEN_MOVE ACK (114 S->C): the server has ALREADY swapped slots `from` and `to`
        /// on its side, so mirror the swap locally by reassigning the two items' slots. to==255 = the server
        /// refused the move (item doesn't fit) → no-op. Empty target = the item just moves there.</summary>
        public void ApplyInvenMove(int from, int to)
        {
            if (to >= 255 || from == to) return;
            InvenItem a = null, b = null;
            foreach (var it in Inventory)
            {
                if (it.Slot == from) a = it;
                else if (it.Slot == to) b = it;
            }
            if (a != null) a.Slot = (byte)to;
            if (b != null) b.Slot = (byte)from;
            DeriveEquipFromInventory();   // an equip/unequip may have changed a worn slot → refresh the doll
        }

        /// <summary>Drive the doll's worn-gear fields (Weapon/Amor/…) from the equipped inventory slots 0-24,
        /// which are the authoritative source after an equip/unequip. INVEN_MOVE does not resend CHARAC_INFO
        /// live, so without this the doll wouldn't update until relog.</summary>
        private void DeriveEquipFromInventory()
        {
            int amor = 0, pants = 0, gloves = 0, shoes = 0, cap = 0, weapon = 0; sbyte wtype = 0;
            foreach (var it in Inventory)
            {
                if (it.Id == 0) continue;
                switch (it.Slot)
                {
                    case 0:  amor = it.Id;  break;   // AMOR
                    case 1:  pants = it.Id; break;   // PANTS
                    case 2:  gloves = it.Id; break;  // ADAE (gloves)
                    case 3:  shoes = it.Id; break;   // SHOES
                    case 4:  cap = it.Id;   break;   // CAP
                    case 10: weapon = it.Id; wtype = (sbyte)it.Type; break;   // WEAPON_1 (whatever is equipped)
                }
            }
            Amor = amor; Pants = pants; Gloves = gloves; Shoes = shoes; Cap = cap;
            Weapon = weapon; WeaponType = wtype;   // doll icon always reflects the equipped weapon slot
        }
    }

    /// <summary>A nearby monster (from MONSTER_EXIST / MONSTER_MOVE / MONSTER_VITAL).</summary>
    public sealed class MonsterEntity
    {
        public ushort Id;            // usMobID (instance, starts at 1001)
        public ushort WireType;      // usMobType on the wire (field mobs = rawType + 4000)
        public float  X, Z;
        public float  GotoX, GotoZ;  // MONSTER_MOVE destination
        public int    WalkMode;      // MONSTER_MOVE ucMobWalkMode (0 = walk, 1 = run)
        public int    SpeedRate;     // MONSTER_MOVE ucSpeedRate (raw; 20 = normal, 0 = not sent)
        public sbyte  VitalRate;     // 0..100, <0 = dead
        public bool   Alive = true;

        // MonsterTable (monster_ref.bmo) enrichment; 0 when the table is absent.
        public int    Level;
        public int    Hp;
        public uint   ExpReward;

        // Classification the top target-status frame reads (from monster_ref.bmo, mirroring
        // g_MobAttribTable[type].* in the client). Party -> mi_npcname2.tga type-glyph index;
        // Rank -> frame variant (0 normal / 1 quest / 2 raid / 3,4,8 boss); ScriptType 91..95 also
        // hides the level line; Attribute -> element that tints the name. Set by Enrich (live) or
        // directly (the -ndTargetDemo offline mobs).
        public sbyte  Party;
        public ushort ScriptType;
        public sbyte  Rank;
        public byte   Attribute;

        /// <summary>Boss/raid frame: the client draws the boss plate for ranks 3, 4 and 8.</summary>
        public bool IsBoss => Rank == 3 || Rank == 4 || Rank == 8;
        /// <summary>Named/quest frame (rank 1) — the quest decoration + no level line.</summary>
        public bool IsQuest => Rank == 1;

        /// <summary>The id nd-world uses internally (= WireType - 4000 for field mobs).</summary>
        public int RawType => WireType > 4000 ? WireType - 4000 : WireType;

        public void Enrich(MonsterTable t)
        {
            if (t == null) return;
            if (t.TryGet(RawType, out var r))
            {
                Level = r.Level; Hp = r.Hp; ExpReward = r.Exp;
                Party = r.Party; ScriptType = r.ScriptType; Rank = r.Rank; Attribute = r.Attribute;
            }
        }
    }

    /// <summary>Another player in view (from ZONE_MOVE broadcast). Rare with a single bot online.</summary>
    public sealed class PlayerEntity
    {
        public ushort Uid;
        public float  X, Z;
        public float  GotoX, GotoZ;
        public int    AnimationNumber;

        // appearance from ZONE_CHARAC_INFO (op 27); HasAppearance false until it arrives
        public bool   HasAppearance;
        public string Name = "";
        public int    Sex, Face, Hair;
        public int    Amor, Pants, Cap, Weapon;
    }

    /// <summary>The whole observable world: me + nearby entities.</summary>
    public sealed class WorldState
    {
        public readonly MyCharacter Me = new MyCharacter();
        public readonly Dictionary<ushort, MonsterEntity> Monsters = new Dictionary<ushort, MonsterEntity>();
        public readonly Dictionary<ushort, PlayerEntity>  Players  = new Dictionary<ushort, PlayerEntity>();

        public IEnumerable<MonsterEntity> AliveMonsters
        {
            get { foreach (var m in Monsters.Values) if (m.Alive) yield return m; }
        }
    }
}
