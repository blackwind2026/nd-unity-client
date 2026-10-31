using System.Collections.Generic;

namespace NineDragons.Data
{
    /// <summary>
    /// data/seid_where.txt : maps an item option SEID to a "where#" (stat accumulator slot)
    /// and a value-type (0 add, 1 add%, 2 sub, 3 sub%). This is the input to the stat engine
    /// pipeline (item option SEID -> where# -> accumulator -> formula). Ported 1:1 from
    /// nd_stats.h::nd_stats_load.
    /// Line format: "SEID where value_type ; name". Lines starting with ';' '#' or blank are skipped.
    /// </summary>
    public sealed class SeidWhereTable
    {
        public const int SeidMax = 4096;

        private readonly byte[] _where = new byte[SeidMax];     // 0 = unmapped
        private readonly byte[] _valueType = new byte[SeidMax];

        /// <summary>Number of SEID rows parsed (matches the server [stat] count).</summary>
        public int Count { get; private set; }

        public int WhereOf(int seid) => (seid > 0 && seid < SeidMax) ? _where[seid] : 0;
        public int ValueTypeOf(int seid) => (seid > 0 && seid < SeidMax) ? _valueType[seid] : 0;
        public bool IsMapped(int seid) => seid > 0 && seid < SeidMax && _where[seid] != 0;

        public static SeidWhereTable Load(string path)
        {
            var t = new SeidWhereTable();
            foreach (var line in TextLoader.ReadLines(path))
            {
                if (line.Length == 0) continue;
                char c0 = line[0];
                if (c0 == ';' || c0 == '#') continue;

                var tok = TextLoader.Tokens(line);
                if (tok.Length < 3) continue;
                if (!TextLoader.TryInt(tok[0], out int seid)) continue;
                if (!TextLoader.TryInt(tok[1], out int wh)) continue;
                if (!TextLoader.TryInt(tok[2], out int vt)) continue;
                if (seid <= 0 || seid >= SeidMax) continue;

                t._where[seid] = (byte)wh;
                t._valueType[seid] = (byte)vt;
                t.Count++;
            }
            return t;
        }
    }
}
