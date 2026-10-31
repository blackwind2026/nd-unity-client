using System.Collections.Generic;

namespace NineDragons.Data
{
    public sealed class EffectDef
    {
        public int Id;                 // SEID / effect id
        public string WhereName;       // effect name (e.g. "attack_rate")
        public int WhereNum;           // resolved via the effect_descript_where_table (0 if unknown)
        public string[] Properties;    // general / apply_additive / bonus_additive / ...
        public string ValueType;       // add_value / add_percent / sub_value / sub_percent (or null)
        public int ProcessFlag;        // "value Boolean" line
        public int Index;              // single-effect index line
    }

    /// <summary>
    /// nfofile/effect_script.txt. Two parseable parts:
    ///  1. effect_descript_where_table: the first "{ ... } / effect_descript_end" block,
    ///     mapping effect NAME -> where#. This is the authoritative name->where table that
    ///     seid_where.txt encodes numerically.
    ///  2. script_start .. script_end: a stream of "start { ID  name  property*  value_type
    ///     flag  index }" blocks describing each effect id.
    /// The C++ server does NOT parse this file (it uses the pre-flattened seid_where.txt); this
    /// loader is provided for completeness / tooling and has no server count to match.
    /// </summary>
    public sealed class EffectScriptTable
    {
        private static readonly HashSet<string> ValueTypes = new HashSet<string>
        {
            "add_value", "add_percent", "sub_value", "sub_percent"
        };

        private readonly Dictionary<string, int> _whereByName = new Dictionary<string, int>();
        private readonly Dictionary<int, EffectDef> _effects = new Dictionary<int, EffectDef>();

        /// <summary>effect name -> where# (from the descript table).</summary>
        public IReadOnlyDictionary<string, int> WhereByName => _whereByName;
        /// <summary>effect id -> definition (from the script blocks).</summary>
        public IReadOnlyDictionary<int, EffectDef> Effects => _effects;

        public int WhereTableCount => _whereByName.Count;
        public int EffectCount => _effects.Count;

        public int WhereOf(string name) =>
            name != null && _whereByName.TryGetValue(name, out int w) ? w : 0;

        private static string StripComment(string line)
        {
            // comments start with '/' or '//' in this file
            int idx = line.IndexOf('/');
            return idx >= 0 ? line.Substring(0, idx) : line;
        }

        public static EffectScriptTable Load(string path)
        {
            var t = new EffectScriptTable();
            var lines = TextLoader.ReadLines(path);

            // phase: 0 = before/scanning where-table, 1 = inside where-table,
            //        2 = after where-table (looking for script_start), 3 = inside scripts
            int phase = 0;
            bool inBlock = false;
            var block = new List<string>();

            foreach (var rawLine in lines)
            {
                string line = StripComment(rawLine);
                var tok = TextLoader.Tokens(line);

                if (phase <= 1)
                {
                    if (tok.Length == 0) continue;
                    if (phase == 0)
                    {
                        if (tok[0] == "{") { phase = 1; }
                        continue;
                    }
                    // phase == 1: reading name -> where# pairs
                    if (tok[0].StartsWith("}")) { phase = 2; continue; }
                    if (tok.Length >= 2 && TextLoader.TryInt(tok[1], out int wnum) &&
                        !TextLoader.TryInt(tok[0], out _))
                    {
                        t._whereByName[tok[0]] = wnum;
                    }
                    continue;
                }

                // phase 2 / 3: script blocks
                if (!inBlock)
                {
                    if (tok.Length == 0) continue;
                    if (tok[0] == "{") { inBlock = true; block.Clear(); }
                    // "start", "script_start" etc. are ignored until we see '{'
                    continue;
                }

                // inside a start{} block
                if (tok.Length == 0) continue;
                if (tok[0].StartsWith("}"))
                {
                    inBlock = false;
                    ParseBlock(t, block);
                    continue;
                }
                block.Add(tok[0]); // each interior line carries exactly one token of interest
            }
            return t;
        }

        private static void ParseBlock(EffectScriptTable t, List<string> el)
        {
            if (el.Count < 2) return;
            if (!TextLoader.TryInt(el[0], out int id)) return;
            string whereName = el[1];

            // find the value-type token; everything between name and it are properties
            int k = -1;
            for (int i = 2; i < el.Count; i++)
            {
                if (ValueTypes.Contains(el[i])) { k = i; break; }
            }

            var def = new EffectDef
            {
                Id = id,
                WhereName = whereName,
                WhereNum = t.WhereOf(whereName),
            };

            if (k >= 0)
            {
                int propCount = k - 2;
                def.Properties = new string[propCount < 0 ? 0 : propCount];
                for (int i = 0; i < def.Properties.Length; i++) def.Properties[i] = el[2 + i];
                def.ValueType = el[k];
                if (k + 1 < el.Count) TextLoader.TryInt(el[k + 1], out def.ProcessFlag);
                if (k + 2 < el.Count) TextLoader.TryInt(el[k + 2], out def.Index);
            }
            else
            {
                // no value-type marker: treat the remainder as properties
                int propCount = el.Count - 2;
                def.Properties = new string[propCount < 0 ? 0 : propCount];
                for (int i = 0; i < def.Properties.Length; i++) def.Properties[i] = el[2 + i];
            }

            t._effects[id] = def;
        }
    }
}
