using System.Collections.Generic;

namespace NineDragons.Data
{
    public sealed class SetItem
    {
        public int ItemId;
        public ushort Group;      // set group (0 = not part of a set)
        public byte InItem;       // min pieces required to activate this item's bonus (1..5)
        public ushort[] Seid = new ushort[3];
        public ushort[] Value = new ushort[3];
    }

    /// <summary>
    /// nfofile/Set_Script.txt : set (bo do) bonuses. Header row then:
    ///   Group  ItemType  ItemID  In_Item  EffectID  SEID1 Val1  SEID2 Val2  SEID3 Val3
    /// Ported 1:1 from nd_itemtable.h::nd_load_setscript. Requires >= 11 numeric columns and
    /// 0 &lt; id &lt; 30000. Also builds the cumulative "tier" table (wear K pieces of a group
    /// => apply tiers In_Item=2..K) exactly as the C++ server does.
    /// </summary>
    public sealed class SetScriptTable
    {
        public const int ItemMax = 30000;
        public const int SetMaxGroup = 512;

        private readonly Dictionary<int, SetItem> _items = new Dictionary<int, SetItem>();
        public IReadOnlyDictionary<int, SetItem> Items => _items;

        // g_setTierSEID[group][tier 0..5][3], g_setTierVal[...]
        private readonly ushort[,,] _tierSeid = new ushort[SetMaxGroup, 6, 3];
        private readonly ushort[,,] _tierVal = new ushort[SetMaxGroup, 6, 3];

        /// <summary>Number of Set_Script rows parsed (matches the server [set] count).</summary>
        public int Count { get; private set; }

        public bool TryGetItem(int itemId, out SetItem item) => _items.TryGetValue(itemId, out item);

        /// <summary>Group of an item id (0 if not in a set).</summary>
        public int GroupOf(int itemId) =>
            _items.TryGetValue(itemId, out var it) ? it.Group : 0;

        /// <summary>Cumulative tier bonus (group, tier 1..5) -> (seid[3], value[3]).</summary>
        public void GetTier(int group, int tier, out ushort[] seid, out ushort[] value)
        {
            seid = new ushort[3];
            value = new ushort[3];
            if (group <= 0 || group >= SetMaxGroup || tier < 1 || tier > 5) return;
            for (int i = 0; i < 3; i++)
            {
                seid[i] = _tierSeid[group, tier, i];
                value[i] = _tierVal[group, tier, i];
            }
        }

        public static SetScriptTable Load(string path)
        {
            var t = new SetScriptTable();
            var lines = TextLoader.ReadLines(path);
            bool first = true;
            foreach (var line in lines)
            {
                if (first) { first = false; continue; } // header row
                var tok = TextLoader.Tokens(line);
                if (tok.Length < 11) continue;

                if (!TextLoader.TryInt(tok[0], out int grp)) continue;
                if (!TextLoader.TryInt(tok[1], out int _ty)) continue;
                if (!TextLoader.TryInt(tok[2], out int id)) continue;
                if (!TextLoader.TryInt(tok[3], out int inItem)) continue;
                if (!TextLoader.TryInt(tok[4], out int _eff)) continue;
                if (!TextLoader.TryInt(tok[5], out int s1)) continue;
                if (!TextLoader.TryInt(tok[6], out int v1)) continue;
                if (!TextLoader.TryInt(tok[7], out int s2)) continue;
                if (!TextLoader.TryInt(tok[8], out int v2)) continue;
                if (!TextLoader.TryInt(tok[9], out int s3)) continue;
                if (!TextLoader.TryInt(tok[10], out int v3)) continue;
                if (id <= 0 || id >= ItemMax) continue;

                var item = new SetItem
                {
                    ItemId = id,
                    Group = (ushort)grp,
                    InItem = (byte)inItem,
                };
                item.Seid[0] = (ushort)s1; item.Value[0] = (ushort)v1;
                item.Seid[1] = (ushort)s2; item.Value[1] = (ushort)v2;
                item.Seid[2] = (ushort)s3; item.Value[2] = (ushort)v3;
                t._items[id] = item;

                if (grp > 0 && grp < SetMaxGroup && inItem >= 1 && inItem <= 5)
                {
                    t._tierSeid[grp, inItem, 0] = (ushort)s1; t._tierVal[grp, inItem, 0] = (ushort)v1;
                    t._tierSeid[grp, inItem, 1] = (ushort)s2; t._tierVal[grp, inItem, 1] = (ushort)v2;
                    t._tierSeid[grp, inItem, 2] = (ushort)s3; t._tierVal[grp, inItem, 2] = (ushort)v3;
                }
                t.Count++;
            }
            return t;
        }
    }
}
