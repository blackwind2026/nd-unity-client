using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NineDragons.Client
{
    /// <summary>
    /// CharactorModel.XMS cluster tables (SCR_BASE.XP) — the client's g_UserModel_* arrays.
    /// Each cluster (HAIR/FACE/UPPERBODY/LOWERBODY/LHAND/RHAND/LFOOT/RFOOT/CAP…) is a flat list
    /// filled by walking the script: `_XM_MODELINDEX n` sets the running fill index and each
    /// following `_XM_LOADMODEL hi mid lo` stores `hi` at index++ (male at 0.., female at 300+,
    /// _XM_MODEL_GENDERSEPARATECOUNT). Equipped cloth picks
    /// cluster[genderOffset + ClothRecord.ModelIndex].
    /// </summary>
    public sealed class CharModelTable
    {
        public const int GenderSeparate = 300;
        private readonly Dictionary<string, Dictionary<int, string>> _clusters =
            new Dictionary<string, Dictionary<int, string>>();

        public string Get(string cluster, int index)
            => _clusters.TryGetValue(cluster, out var m) && m.TryGetValue(index, out var f) ? f : null;

        /// <summary>debug: one line per cluster listing its default entries at [off], [off+1].</summary>
        public string Dump(int off)
        {
            var sb = new StringBuilder();
            foreach (var kv in _clusters)
            {
                kv.Value.TryGetValue(off, out var a);
                kv.Value.TryGetValue(off + 1, out var b);
                sb.Append($"  {kv.Key}: [{off}]={a ?? "-"} [{off + 1}]={b ?? "-"} (n={kv.Value.Count})\n");
            }
            return sb.ToString();
        }

        public static CharModelTable Parse(byte[] xms)
        {
            var t = new CharModelTable();
            using var r = new StringReader(Encoding.GetEncoding(28591).GetString(xms));
            string line;
            Dictionary<int, string> cur = null;
            int idx = 0;
            while ((line = r.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tok[0].StartsWith("_XM_MODELCLUSTER_"))
                {
                    string name = tok[0].Substring("_XM_MODELCLUSTER_".Length);
                    if (!t._clusters.TryGetValue(name, out cur))
                        t._clusters[name] = cur = new Dictionary<int, string>();
                    idx = 0;
                }
                else if (tok[0] == "_XM_MODELINDEX" && tok.Length >= 2)
                {
                    int.TryParse(tok[1], out idx);
                }
                else if (tok[0] == "_XM_LOADMODEL" && tok.Length >= 2 && cur != null)
                {
                    cur[idx++] = tok[1];   // high-LOD model
                }
            }
            return t;
        }
    }
}
