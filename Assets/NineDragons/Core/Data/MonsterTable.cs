using System.Collections.Generic;
using System.IO;

namespace NineDragons.Data
{
    public sealed class MonsterRecord
    {
        public int RawId;        // mobID - 4000 (the id nd-world uses)
        public int MobId;        // raw file id (raw + 4000)
        public short Level;
        // classification the target-status frame reads (g_MobAttribTable[type].* in the client):
        //   Party      -> faction/type icon index into mi_npcname2.tga (g_MobTargetIconPos[party]);
        //                 wild-animal ("beast") types land in 11..16 → the 獸 glyph clip (0,44,22,66).
        //   Rank       -> frame variant: 0 normal, 1 named/quest, 2 raid, 3/4/8 boss
        //                 (XWindow_MonsterStatus.cpp DrawMonster:1479-1567).
        //   ScriptType -> special mobs 91..95 also suppress the level line.
        //   Attribute  -> elemental type (ucAttr) that tints the name (1 blue/2 red/3 purple/4 yellow).
        public sbyte  Party;
        public ushort ScriptType;
        public sbyte  Rank;
        public byte   Attribute;
        public int Hp;
        public short AtkMin;
        public short AtkMax;
        public short Delay;      // ms
        public float AtkRange;
        public float Sight;      // normal_scope (aggro detection radius)
        public float Chase;      // chase_scope (leash extension)
        public sbyte AiTarget;   // 1 = auto-aggro, 0 = passive
        public sbyte AiAtk;
        public sbyte GroupAttack;
        public uint RespawnMin;  // seconds
        public uint RespawnMax;
        public uint Exp;
        public uint SkillExp;
        public uint Honor;
        public uint Fame;        // fame0 (fame1/fame2 in file are ignored, matching server)
    }

    /// <summary>
    /// Parses monster_ref.bmo (ZeroN 9D Monster Editor format). 104-byte header, then a
    /// stream of variable-length records read strictly field-by-field (name is length-prefixed,
    /// 60 drop entries of 9 bytes each near the tail). Ported 1:1 from nd_monref.h::nd_load_monref.
    /// Keyed by RawId (= file mobID - 4000).
    /// </summary>
    public sealed class MonsterTable
    {
        public const int MonRefMax = 16000;

        private readonly Dictionary<int, MonsterRecord> _mobs = new Dictionary<int, MonsterRecord>();
        public IReadOnlyDictionary<int, MonsterRecord> Monsters => _mobs;

        /// <summary>Number of records read from the file (matches the server [monref] count).</summary>
        public int Count { get; private set; }

        public bool TryGet(int rawId, out MonsterRecord r) => _mobs.TryGetValue(rawId, out r);

        public static MonsterTable Load(string path) => Load(File.ReadAllBytes(path));

        public static MonsterTable Load(byte[] data)
        {
            var t = new MonsterTable();
            var c = new ByteCursor(data, 104); // skip header

            while (c.Pos + 2 <= c.Length)
            {
                ushort mobID = c.U16();
                if (mobID == 65535 || mobID <= 4000 || mobID >= 4000 + MonRefMax) break;
                int raw = mobID - 4000;

                short mlevel = c.I16();
                sbyte party = c.I8();          // faction/type -> target-frame glyph index
                ushort scriptType = c.U16();   // AI script type (91..95 suppress the level line)
                c.I8();                        // mission
                sbyte rank = c.I8();           // 0 normal / 1 quest / 2 raid / 3,4,8 boss
                byte nlen = c.U8(); c.Skip(nlen);                          // name (length-prefixed)
                c.I16(); c.I8(); c.I8();                                   // size advert timeResult
                int hp = c.I32();
                c.I16(); c.I16(); c.I32(); c.I8();                         // sp stamina upgrade_exp upgrade_count
                c.Skip(6 * 4); c.I8(); c.Skip(4 * 2); c.Skip(2);          // 6 float speed, activity, 4 resist, m/f attr
                c.I16(); c.I16();                                          // agility agility1
                c.I16(); c.I16();                                          // defense CKdefense
                c.Skip(2);                                                 // attackClass attackType
                short amin = c.I16(), amax = c.I16();
                c.Skip(2 * 2);                                             // mAtkSucc mAtkSucc1
                short adelay = c.I16();
                c.Skip(4);                                                 // mAtkAngle
                float arange = c.F32();
                c.Skip(4); c.Skip(2 * 2);                                  // mAtkArea, mCrit x2
                c.Skip(26);                                                // ranged attack block
                byte ucAttr = c.U8(); c.Skip(4);                          // ucAttr (element), iAttrAtk
                c.Skip(23 * 2);                                            // special1 + special2
                c.Skip(1); c.Skip(23 * 4);                                 // skill_title, skill1..4
                c.Skip(2 + 1); c.Skip(2 * 3);                             // model_change change_prob, 3 regain
                float nscope = c.F32(), cscope = c.F32();                  // normal_scope, chase_scope
                sbyte aiTarget = c.I8(); c.I8();                          // ai_target, ai_item
                sbyte aiAtk = c.I8(); c.I8(); c.I8();                     // ai_atk, ai_family, ai_escape
                sbyte groupAtk = c.I8(); c.I8();                          // group_attack, ai_battle
                c.Skip(2); c.Skip(4 * 3); c.Skip(1);                      // ai_siege, temp x3, exp_level
                uint rexp = c.U32();
                uint skexp = c.U32(); c.I8();                             // skillexp, honor_lvl
                uint honor = c.U32();
                uint fame0 = c.U32(); c.U32(); c.U32();                   // fame x3
                c.I8();                                                    // gold_lvl
                c.U32(); c.U32(); c.Skip(4);                              // gold min/max/prob
                c.Skip(60 * 9);                                           // 60 drops (9B each)
                c.Skip(1 + 1 + 2);                                        // AtkProb Key_Type keyID
                uint rmin = c.U32(), rmax = c.U32(); c.I8();              // RespawnMin/Max/Count

                if (raw >= 0 && raw < MonRefMax)
                {
                    var m = new MonsterRecord
                    {
                        RawId = raw,
                        MobId = mobID,
                        Level = mlevel,
                        Party = party,
                        ScriptType = scriptType,
                        Rank = rank,
                        Attribute = ucAttr,
                        Hp = hp > 0 ? hp : 100,
                        AtkMin = amin > 0 ? amin : (short)1,
                        AtkMax = (amax >= amin && amax > 0) ? amax : (amin > 0 ? amin : (short)1),
                        Delay = adelay > 0 ? adelay : (short)2000,
                        AtkRange = arange > 0.5f ? arange : 2.0f,
                        Sight = nscope,
                        Chase = cscope,
                        AiTarget = aiTarget,
                        AiAtk = aiAtk,
                        GroupAttack = groupAtk,
                        RespawnMin = rmin > 0 ? rmin : 15u,
                        RespawnMax = (rmax >= rmin && rmax > 0) ? rmax : (rmin > 0 ? rmin : 15u),
                        Exp = rexp,
                        SkillExp = skexp,
                        Honor = honor,
                        Fame = fame0,
                    };
                    t._mobs[raw] = m;
                }
                t.Count++;
            }
            return t;
        }
    }
}
