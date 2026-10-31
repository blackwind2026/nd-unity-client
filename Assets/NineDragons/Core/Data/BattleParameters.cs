using System.Collections.Generic;

namespace NineDragons.Data
{
    /// <summary>
    /// nfofile/ND_Battle_Parameter.txt : combat coefficient tables. The main block (up to
    /// END_PARAM) is a set of named rows "NAME v0 v1 ... vN" (per-class columns, or a single
    /// scalar). After it come labelled sub-tables (START_x .. END_x): SKILL_PROPERTY,
    /// PVP_BALANCE, PVE_LEVELDIFF, PVP_LEVELDIFF, PVE_EXP_BONUS.
    /// The C++ server hard-codes the multipliers it needs; here we expose the whole file so the
    /// M4 stat engine can read any coefficient by name.
    /// Comments are '//' to end of line.
    /// </summary>
    public sealed class BattleParameters
    {
        private readonly Dictionary<string, double[]> _params =
            new Dictionary<string, double[]>();
        private readonly Dictionary<string, List<string[]>> _sections =
            new Dictionary<string, List<string[]>>();

        /// <summary>Named coefficient rows from the main block (NAME -> per-class values).</summary>
        public IReadOnlyDictionary<string, double[]> Parameters => _params;

        /// <summary>Labelled sub-tables (SKILL_PROPERTY, PVP_BALANCE, ...) -> raw tokenized rows.</summary>
        public IReadOnlyDictionary<string, List<string[]>> Sections => _sections;

        public int ParameterCount => _params.Count;
        public int SectionCount => _sections.Count;

        /// <summary>First value of a named parameter (or a fallback if missing).</summary>
        public double Get(string name, double fallback = 0.0) =>
            _params.TryGetValue(name, out var a) && a.Length > 0 ? a[0] : fallback;

        /// <summary>Per-class value of a named parameter (clamped to available columns).</summary>
        public double Get(string name, int classIndex, double fallback = 0.0)
        {
            if (!_params.TryGetValue(name, out var a) || a.Length == 0) return fallback;
            if (classIndex < 0) classIndex = 0;
            if (classIndex >= a.Length) classIndex = a.Length - 1;
            return a[classIndex];
        }

        private static string StripComment(string line)
        {
            int idx = line.IndexOf("//", System.StringComparison.Ordinal);
            return idx >= 0 ? line.Substring(0, idx) : line;
        }

        public static BattleParameters Load(string path)
        {
            var t = new BattleParameters();
            string state = "PARAM"; // PARAM, NONE, or a section name

            foreach (var raw in TextLoader.ReadLines(path))
            {
                string line = StripComment(raw);
                var tok = TextLoader.Tokens(line);
                if (tok.Length == 0) continue;

                string head = tok[0];
                if (head.StartsWith("START_"))
                {
                    state = head.Substring("START_".Length);
                    if (!t._sections.ContainsKey(state))
                        t._sections[state] = new List<string[]>();
                    continue;
                }
                if (head.StartsWith("END_"))
                {
                    state = "NONE";
                    continue;
                }

                if (state == "PARAM")
                {
                    // NAME followed by one or more numeric columns
                    if (tok.Length < 2) continue;
                    var vals = new List<double>();
                    for (int i = 1; i < tok.Length; i++)
                        if (TextLoader.TryDouble(tok[i], out double d)) vals.Add(d);
                    if (vals.Count > 0)
                        t._params[head] = vals.ToArray();
                }
                else if (state != "NONE")
                {
                    t._sections[state].Add(tok);
                }
            }
            return t;
        }
    }
}
