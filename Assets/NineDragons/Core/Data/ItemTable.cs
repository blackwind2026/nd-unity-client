using System.Collections.Generic;
using System.IO;

namespace NineDragons.Data
{
    // FirstCategory groupings (see nd_itemtable.h). ids overlap between the three
    // sections, so a lookup MUST know which record kind it wants.
    public sealed class WeaponRecord
    {
        public int Id;
        public sbyte SecondType;      // rec[1]
        public sbyte ThirdType;       // @12 — weapon kind (sword/knife/club/…) → picks the atlas
        public short ModelIndex;      // rec[4] sModelIndex — RHAND cluster index for the held weapon
        public short IconNo;          // @69 sIcon_No (1-based) — index into the Item_Wea_*.tga atlas
        public short MinDamage;       // @17
        public short MaxDamage;       // @19
        public ushort Durability;     // @21 (x100)
        public short Level;           // @31
        public sbyte Clan;            // @43 (applyClan)
        public ushort[] OptionSeid = new ushort[5];  // @128 + o*8
        public ushort[] OptionValue = new ushort[5]; // @130 + o*8
    }

    public sealed class ClothRecord
    {
        public int Id;
        public sbyte SecondType;      // rec[1] (0 upper,1 lower,2 gloves,3 shoes,4 cap,6 armor,8 mask)
        public short ModelIndex;      // rec[4] sModelIndex — index into the CharactorModel.XMS cluster
        public short IconNo;          // @6 sIcon_No — index into Item_Clo_{clan}{letter}.tga
        public ushort Rank;           // @8 (itemRank)
        public sbyte Clan;            // @45
        public byte Sex;              // @47
        public ushort Defence;        // @70
        public short Stamina;         // @72
        public ushort Durability;     // @79
        public byte Pocket;           // @128 (tin vat pocket count)
        public ushort[] OptionSeid = new ushort[5];  // @82 + o*8
        public ushort[] OptionValue = new ushort[5]; // @84 + o*8
    }

    public sealed class AccessoryRecord
    {
        public int Id;
        public sbyte SecondType;      // rec[1]
        public short IconNo;          // @6 sIcon_No (1-based) — Item_Acc_00N.tga (paged /64)
        public ushort Rank;           // @8
        public sbyte Clan;            // @24
        public ushort Durability;     // @26
        public short RequiredLevel;   // @70
        public ushort[] OptionSeid = new ushort[5];  // @30 + o*8
        public ushort[] OptionValue = new ushort[5]; // @32 + o*8
    }

    /// <summary>
    /// Parses item_table.bit (shared client/server format from ZeroN 9D ItemTableReader).
    /// Layout: 104-byte header, then sections repeat to EOF, each:
    ///   [type:i8][size:u32][count:u16][maxIndex:u16] followed by count records of `size` bytes.
    /// BOOK (type 2) records are actually 108 bytes even though the header size field reads
    /// otherwise — the C++ loader hardcodes 108 for type 2, and we do the same.
    /// Ported 1:1 from nd_itemtable.h::nd_load_itemtable.
    /// </summary>
    public sealed class ItemTable
    {
        public const int ItemMax = 30000; // id is i16, cap generously

        /// <summary>Minimal record for the icon-only categories (potion/consuming/elixir/resource/quest):
        /// just what ItemIcon needs — id, secondType (potion atlas file), and the 1-based sIcon_No.</summary>
        public sealed class SimpleItem { public int Id; public sbyte SecondType; public short IconNo; }

        // last-write-wins per id, mirroring the C++ flat arrays indexed by id
        private readonly Dictionary<int, WeaponRecord> _weapons = new Dictionary<int, WeaponRecord>();
        // vũ khí type 15 (cash) + 19 (ngoại trang) = bảng RIÊNG: id TRÙNG với type 0 nhưng là món khác
        // (chủ 09/09: "vũ khí type 0/15/19, thiếu → mang type nào cũng hiện thành type 0"). Tra theo (objType,id).
        private readonly Dictionary<int, WeaponRecord> _weapons15 = new Dictionary<int, WeaponRecord>();
        private readonly Dictionary<int, WeaponRecord> _weapons19 = new Dictionary<int, WeaponRecord>();
        private readonly Dictionary<int, ClothRecord> _clothes = new Dictionary<int, ClothRecord>();
        private readonly Dictionary<int, AccessoryRecord> _accessories = new Dictionary<int, AccessoryRecord>();
        private readonly Dictionary<int, SimpleItem> _potions = new Dictionary<int, SimpleItem>();
        private readonly Dictionary<int, SimpleItem> _consuming = new Dictionary<int, SimpleItem>();
        private readonly Dictionary<int, SimpleItem> _elixir = new Dictionary<int, SimpleItem>();
        private readonly Dictionary<int, SimpleItem> _resource = new Dictionary<int, SimpleItem>();
        private readonly Dictionary<int, SimpleItem> _quest = new Dictionary<int, SimpleItem>();

        public IReadOnlyDictionary<int, WeaponRecord> Weapons => _weapons;
        public IReadOnlyDictionary<int, ClothRecord> Clothes => _clothes;
        public IReadOnlyDictionary<int, AccessoryRecord> Accessories => _accessories;
        // icon-only categories (section = 0-based _XGI_FC: potion 3, consuming 4, elixir 5, resource 7, quest 8)
        public IReadOnlyDictionary<int, SimpleItem> Potions => _potions;
        public IReadOnlyDictionary<int, SimpleItem> Consuming => _consuming;
        public IReadOnlyDictionary<int, SimpleItem> Elixir => _elixir;
        public IReadOnlyDictionary<int, SimpleItem> Resource => _resource;
        public IReadOnlyDictionary<int, SimpleItem> Quest => _quest;

        // Counts of RECORDS processed (not distinct ids) — these are what the server logs.
        public int SectionCount { get; private set; }
        public int WeaponCount { get; private set; }
        public int ClothCount { get; private set; }
        public int AccessoryCount { get; private set; }

        public bool TryGetWeapon(int id, out WeaponRecord r) => _weapons.TryGetValue(id, out r);
        /// <summary>Tra vũ khí theo OBJECT-TYPE (0 thường / 15 cash / 19 ngoại trang) + id — id trùng giữa
        /// các type nên PHẢI biết type mới ra đúng món. objType khác 0/15/19 → coi như type 0.</summary>
        public bool TryGetWeapon(int objType, int id, out WeaponRecord r)
            => (objType == 15 ? _weapons15 : objType == 19 ? _weapons19 : _weapons).TryGetValue(id, out r);
        public bool TryGetCloth(int id, out ClothRecord r) => _clothes.TryGetValue(id, out r);
        public bool TryGetAccessory(int id, out AccessoryRecord r) => _accessories.TryGetValue(id, out r);

        /// <summary>Dựng WeaponRecord từ 1 record (offset Codex-2026: model@67, icon@69…). Dùng chung cho
        /// object-type 0/15/19 vì cả 3 cùng layout weapon.</summary>
        private static WeaponRecord MakeWeapon(byte[] rec, int id)
        {
            var w = new WeaponRecord
            {
                Id = id,
                SecondType = (sbyte)rec[1],
                ThirdType = (sbyte)rec[12],
                ModelIndex = ByteCursor.I16At(rec, 67),   // CODEX_WEAPON_MODEL_OFFSET (XGameItem.cpp:60/833)
                IconNo = ByteCursor.I16At(rec, 69),        // CODEX_WEAPON_ICON_OFFSET
                MinDamage = ByteCursor.I16At(rec, 17),
                MaxDamage = ByteCursor.I16At(rec, 19),
                Durability = ByteCursor.U16At(rec, 21),
                Level = ByteCursor.I16At(rec, 31),
                Clan = (sbyte)rec[43],
            };
            for (int o = 0; o < 5; o++)
            {
                w.OptionSeid[o] = ByteCursor.U16At(rec, 128 + o * 8);
                w.OptionValue[o] = ByteCursor.U16At(rec, 130 + o * 8);
            }
            return w;
        }

        private static SimpleItem MakeSimple(byte[] rec, int id, int iconOff)
            => new SimpleItem { Id = id, SecondType = (sbyte)rec[1], IconNo = ByteCursor.I16At(rec, iconOff) };

        public static ItemTable Load(string path) => Load(File.ReadAllBytes(path));

        public static ItemTable Load(byte[] data)
        {
            var t = new ItemTable();
            var c = new ByteCursor(data, 104); // skip 104-byte header

            var rec = new byte[4096];
            while (c.CanRead(9))
            {
                int type = c.I8();
                uint size = c.U32();
                int count = c.U16();
                c.U16(); // maxIndex (unused)

                uint recSize = (type == 2) ? 108u : size;
                if (recSize == 0 || recSize > (uint)rec.Length)
                {
                    if (count == 0) continue; // empty section: skip, do not count
                    break;                    // malformed: stop like the C++ loader
                }
                t.SectionCount++;

                for (int i = 0; i < count; i++)
                {
                    if (!c.CanRead((int)recSize)) return t; // truncated -> stop (goto done)
                    System.Array.Copy(data, c.Pos, rec, 0, (int)recSize);
                    c.Skip((int)recSize);

                    int id = ByteCursor.I16At(rec, 2);
                    if (id <= 0 || id >= ItemMax) continue;

                    if (type == 0)      { t._weapons[id]   = MakeWeapon(rec, id); t.WeaponCount++; }
                    else if (type == 15) t._weapons15[id] = MakeWeapon(rec, id);   // cash weapon (cùng layout)
                    else if (type == 19) t._weapons19[id] = MakeWeapon(rec, id);   // ngoại trang weapon
                    else if (type == 1)
                    {
                        var cl = new ClothRecord
                        {
                            Id = id,
                            SecondType = (sbyte)rec[1],
                            ModelIndex = ByteCursor.I16At(rec, 4),
                            IconNo = ByteCursor.I16At(rec, 6),
                            Rank = ByteCursor.U16At(rec, 8),
                            Clan = (sbyte)rec[45],
                            Sex = rec[47],
                            Defence = ByteCursor.U16At(rec, 70),
                            Stamina = ByteCursor.I16At(rec, 72),
                            Durability = ByteCursor.U16At(rec, 79),
                            Pocket = rec[128],
                        };
                        for (int o = 0; o < 5; o++)
                        {
                            cl.OptionSeid[o] = ByteCursor.U16At(rec, 82 + o * 8);
                            cl.OptionValue[o] = ByteCursor.U16At(rec, 84 + o * 8);
                        }
                        t._clothes[id] = cl;
                        t.ClothCount++;
                    }
                    else if (type == 9)
                    {
                        var a = new AccessoryRecord
                        {
                            Id = id,
                            SecondType = (sbyte)rec[1],
                            IconNo = ByteCursor.I16At(rec, 6),
                            Rank = ByteCursor.U16At(rec, 8),
                            Clan = (sbyte)rec[24],
                            Durability = ByteCursor.U16At(rec, 26),
                            RequiredLevel = ByteCursor.I16At(rec, 70),
                        };
                        for (int o = 0; o < 5; o++)
                        {
                            a.OptionSeid[o] = ByteCursor.U16At(rec, 30 + o * 8);
                            a.OptionValue[o] = ByteCursor.U16At(rec, 32 + o * 8);
                        }
                        t._accessories[id] = a;
                        t.AccessoryCount++;
                    }
                    // icon-only categories (offsets from item_icons.md §2): potion@6 consuming@13
                    // elixir@11 resource@6 quest@6. Store just enough for ItemIcon.
                    else if (type == 3) t._potions[id]   = MakeSimple(rec, id, 6);
                    else if (type == 4) t._consuming[id] = MakeSimple(rec, id, 13);
                    else if (type == 5) t._elixir[id]    = MakeSimple(rec, id, 11);
                    else if (type == 7) t._resource[id]  = MakeSimple(rec, id, 6);
                    else if (type == 8) t._quest[id]     = MakeSimple(rec, id, 6);
                    // book/socket/box/box-key/life still skipped by size.
                }
            }
            return t;
        }
    }
}
