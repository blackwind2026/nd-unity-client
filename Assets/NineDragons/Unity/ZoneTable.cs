using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NineDragons.Client
{
    /// <summary>
    /// Zone id → map file, parsed from <c>Zonelist.ini</c> in SCR_BASE.XP — the same file
    /// the C++ client feeds into <c>g_ZoneInfoTable</c> (XProc_FirstLoad::InitialzeZoneTable):
    /// whitespace-separated <c>id short long title FILENAME basepoint(3f) service bgm bgm2
    /// mapsize property pkinfo [penalty]</c>; ';' comments, `_XM_EOS` terminator.
    /// </summary>
    public sealed class ZoneTable
    {
        private readonly Dictionary<int, string> _zoneToFile = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _zoneToBgm = new Dictionary<int, string>();

        public int Count => _zoneToFile.Count;

        /// <summary>Map name WITHOUT extension for a zone id (e.g. 2 → "map_gaebang"); null if unknown.</summary>
        public string MapNameFor(int zoneId)
        {
            if (!_zoneToFile.TryGetValue(zoneId, out var file)) return null;
            return file.EndsWith(".xtv", StringComparison.OrdinalIgnoreCase)
                ? file.Substring(0, file.Length - 4) : file;
        }

        /// <summary>Primary background music (.ogg) for a zone id; null if unknown.</summary>
        public string BgmFor(int zoneId) => _zoneToBgm.TryGetValue(zoneId, out var b) ? b : null;

        public static ZoneTable Parse(byte[] zonelistIni)
        {
            var t = new ZoneTable();
            using var reader = new StringReader(Encoding.GetEncoding(28591).GetString(zonelistIni));
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("_XM_EOS")) break;
                line = line.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length < 5) continue;
                if (!int.TryParse(tok[0], out int id)) continue;
                t._zoneToFile[id] = tok[4];
                // columns: id short long title FILE bp.x bp.y bp.z service bgm1 bgm2 mapsize ...
                if (tok.Length > 9 && tok[9].EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                    t._zoneToBgm[id] = tok[9];
            }
            return t;
        }
    }
}
