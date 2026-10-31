using NineDragons.Data;

namespace NineDragons.Stat
{
    /// <summary>
    /// Port of the ninedragons-server stat engine (<c>nd-world/src/nd_stats.h</c>) to plain C#.
    /// Computes a character's derived stats (HP/force/conc, defence, damage, accuracy, avoid) from
    /// attributes + class + inner level + equipped items, reproducing the numbers the server prints
    /// on its <c>[STAT]</c> log lines.
    ///
    /// Pipeline (identical to the server): item option (SEID + value) -> seid_where.txt (SEID -> where#)
    /// -> accumulator acc[where#] -> base formula (ND_Battle_Parameter.txt coefficients) + accumulator.
    ///
    /// Faithful to nd_stats.h including the three fixed-bug notes (UPDATING.md 25/26-08-2026):
    ///   * acc[] is sized WHERE_MAX = 256 (where# reaches 203 in the live data; a 64-slot array
    ///     overflowed on all_stat=73 / all_defence=162 — a real crash bug).
    ///   * magic accuracy/avoid use where# 6 (m_attack_rate) and 7 (m_avoid_rate), NOT physical 1/2.
    ///   * all_stat (where 73) fans out to con/str/zen/int/dex; all_defence (where 162) adds to BOTH
    ///     physical and magic defence.
    /// Set bonuses are cumulative by piece count (wear K pieces of a group => apply tiers 2..K).
    /// </summary>
    public sealed class StatEngine
    {
        // --- where# constants (effect_descript_where_table), same as the nd_stats.h enum ---
        private const int W_ATKRATE = 1, W_AVOID = 2, W_ATKDMG = 3, W_DEF = 4;
        private const int W_MATKRATE = 6, W_MAVOID = 7, W_MATKDMG = 8, W_MDEF = 9;
        private const int W_MAXLIFE = 14, W_MAXFORCE = 15, W_MAXCONC = 16;
        private const int W_STR = 17, W_ZEN = 18, W_INT = 19, W_CON = 20, W_DEX = 21;
        private const int W_ALLSTAT = 73, W_ALLDEF = 162;

        // acc[] MUST be >= 256: where# reaches 162 (all_defence) / 203 in the data. A smaller
        // array overflows (the original 64-slot bug). See UPDATING.md 26-08.
        private const int WHERE_MAX = 256;

        private const int ItemMax = 30000;   // ITEM_MAX
        private const int SetMaxGroup = 512;  // SET_MAXGROUP

        // Per-class multipliers {vitality, inner, concentrate} from PointTable.xms, hard-coded in
        // nd_stats.h (NOT in ND_Battle_Parameter.txt). class 0 = vit 20 / inner 15 / conc 10.
        private static readonly int[,] ClassMul = new int[12, 3]
        {
            {20,15,10},{50,35,10},{50,35,15},{40,30,20},{25,45,20},{40,30,10},
            {40,30,20},{25,45,20},{25,45,20},{20,30,20},{20,15,10},{20,15,10}
        };

        private readonly ItemTable _items;
        private readonly SeidWhereTable _seid;
        private readonly SetScriptTable _sets;

        // Base-formula coefficients read from ND_Battle_Parameter.txt (column 0 = Lãng nhân; the
        // coefficients are uniform across classes). Fallbacks equal the hard-coded nd_stats.h
        // constants, so the result is identical whether or not the file is present.
        // NOTE the "reversed" naming: *_Chr_Step_Mul multiplies innerlevel, *_Chr_Level_Mul
        // multiplies step.
        private readonly double _pdInner, _pdStep, _pdStr;                 // PD (physical damage base)
        private readonly double _mdInner, _mdStep, _mdZen;                 // MD (magic damage base)
        private readonly double _pfInner, _pfStep, _pfStr;                 // PF (physical defence)
        private readonly double _mfInner, _mfStep, _mfInt, _mfZen;        // MF (magic defence)
        private readonly double _parInner, _parStep, _parDex;             // PAR (physical accuracy)
        private readonly double _marInner, _marStep, _marInt;            // MAR (magic accuracy)
        private readonly double _pdrInner, _pdrStep, _pdrDex;            // PDR (physical avoid)
        private readonly double _mdrInner, _mdrStep, _mdrInt;           // MDR (magic avoid)
        private readonly double _pdMinMul, _pdMaxMul;                    // weapon min/max damage multiplier

        /// <summary>
        /// Build a stat engine over the loaded M3 data tables. <paramref name="battle"/> is optional;
        /// when null, the hard-coded nd_stats.h coefficients are used (the two agree on the shipped
        /// ND_Battle_Parameter.txt).
        /// </summary>
        public StatEngine(ItemTable items, SeidWhereTable seidWhere, SetScriptTable sets,
                          BattleParameters battle = null)
        {
            _items = items;
            _seid = seidWhere;
            _sets = sets;

            // Get(name, classIndex, fallback) : returns the value for class 0 (uniform), else fallback.
            double G(string name, double fallback) =>
                battle != null ? battle.Get(name, 0, fallback) : fallback;

            _pdInner = G("PD_Chr_Step_Mul", 0.5); _pdStep = G("PD_Chr_Level_Mul", 1.5); _pdStr = G("PD_Str_Mul", 3.0);
            _mdInner = G("MD_Chr_Step_Mul", 1.0); _mdStep = G("MD_Chr_Level_Mul", 3.0); _mdZen = G("MD_Zen_Mul", 4.0);
            _pfInner = G("PF_Chr_Step_Mul", 0.5); _pfStep = G("PF_Chr_Level_Mul", 4.0); _pfStr = G("PF_Str_Mul", 6.0);
            _mfInner = G("MF_Chr_Step_Mul", 0.5); _mfStep = G("MF_Chr_Level_Mul", 4.0);
            _mfInt = G("MF_Int_Mul", 1.0); _mfZen = G("MF_Zen_Mul", 2.0);
            _parInner = G("PAR_Chr_Step_Mul", 0.2); _parStep = G("PAR_Chr_Level_Mul", 5.1); _parDex = G("PAR_Dex_Mul", 4.0);
            _marInner = G("MAR_Chr_Step_Mul", 0.2); _marStep = G("MAR_Chr_Level_Mul", 5.1); _marInt = G("MAR_Int_Mul", 2.5);
            _pdrInner = G("PDR_Chr_Step_Mul", 0.2); _pdrStep = G("PDR_Chr_Level_Mul", 5.1); _pdrDex = G("PDR_Dex_Mul", 4.0);
            _mdrInner = G("MDR_Chr_Step_Mul", 0.2); _mdrStep = G("MDR_Chr_Level_Mul", 5.1); _mdrInt = G("MDR_Int_Mul", 2.0);
            _pdMinMul = G("PD_Base_Min_Weapon_Mul", 0.5); _pdMaxMul = G("PD_Base_Max_Weapon_Mul", 1.0);
        }

        // ------------------------------------------------------------------
        // Apply n option (SEID,value) pairs into the accumulator acc[where#].
        // 1:1 with nd_apply_opts_n. value_type >= 2 => subtract; % is treated as flat (matches server).
        // ------------------------------------------------------------------
        private void ApplyOpts(ushort[] seid, ushort[] val, int n, long[] acc)
        {
            for (int o = 0; o < n; o++)
            {
                int s = seid[o];
                int v = val[o];
                if (s <= 0 || s >= SeidWhereTable.SeidMax) continue;
                int wh = _seid.WhereOf(s);
                if (wh == 0) continue;
                int sign = (_seid.ValueTypeOf(s) >= 2) ? -1 : 1; // add_value/percent -> +, sub -> -

                if (wh == W_ALLSTAT)
                {
                    acc[W_STR] += sign * v; acc[W_ZEN] += sign * v; acc[W_INT] += sign * v;
                    acc[W_CON] += sign * v; acc[W_DEX] += sign * v;
                }
                else if (wh == W_ALLDEF)
                {
                    acc[W_DEF] += sign * v; acc[W_MDEF] += sign * v;
                }
                else if (wh > 0 && wh < WHERE_MAX)
                {
                    acc[wh] += sign * v;
                }
            }
        }

        /// <summary>
        /// Compute the derived stats for a character with the given equipment. 1:1 with
        /// <c>nd_stats_compute</c>.
        /// </summary>
        public NdStats Compute(NdCharacter ch, NdEquipment eq)
        {
            long[] acc = new long[WHERE_MAX];  // large enough for where# up to 255
            int wpnMin = 0, wpnMax = 0, sumStamina = 0;

            int[] invId = eq.Ids ?? new int[NdEquipment.SlotCount];
            int[] invType = eq.Types ?? new int[NdEquipment.SlotCount];

            for (int slot = 0; slot <= 19 && slot < invId.Length; slot++)
            {
                int id = invId[slot];
                int ty = slot < invType.Length ? invType[slot] : 0;
                if (id <= 0 || id >= ItemMax) continue;

                if (IsClothType(ty) && _items.TryGetCloth(id, out var cl))
                {
                    acc[W_DEF] += cl.Defence;
                    sumStamina += cl.Stamina;
                    ApplyOpts(cl.OptionSeid, cl.OptionValue, 5, acc);
                }
                else if (IsWeaponType(ty) && _items.TryGetWeapon(id, out var wp))
                {
                    wpnMin += wp.MinDamage;
                    wpnMax += wp.MaxDamage;
                    // nd_stats.h DOES parse weapon options at offset 128 (main-stat vt=0 flat).
                    // Ported faithfully so the port matches the server's [STAT] output by construction.
                    ApplyOpts(wp.OptionSeid, wp.OptionValue, 5, acc);
                }
                else if (IsAccType(ty) && _items.TryGetAccessory(id, out var ac))
                {
                    ApplyOpts(ac.OptionSeid, ac.OptionValue, 5, acc);
                }
            }

            // SET (bộ đồ) bonus, CUMULATIVE by piece count in cloth slots 0..4.
            // Count how many pieces of each group are worn; wearing K>=2 pieces applies tiers 2..K.
            {
                int[] setCount = new int[SetMaxGroup];
                for (int slot = 0; slot <= 4 && slot < invId.Length; slot++)
                {
                    int id = invId[slot];
                    if (id <= 0 || id >= ItemMax) continue;
                    int g = _sets.GroupOf(id);
                    if (g > 0 && g < SetMaxGroup) setCount[g]++;
                }
                for (int g = 1; g < SetMaxGroup; g++)
                {
                    int K = setCount[g];
                    if (K < 2) continue;
                    if (K > 5) K = 5;
                    for (int tier = 2; tier <= K; tier++)
                    {
                        _sets.GetTier(g, tier, out var tSeid, out var tVal);
                        ApplyOpts(tSeid, tVal, 3, acc);
                    }
                }
            }

            int cClass = ch.Class;
            if (cClass < 0 || cClass >= 12) cClass = 0;
            int vit = ClassMul[cClass, 0], inner = ClassMul[cClass, 1], conc = ClassMul[cClass, 2];

            int con = ch.Con + (int)acc[W_CON];
            int zen = ch.Zen + (int)acc[W_ZEN];
            int intel = ch.Int + (int)acc[W_INT];
            int str = ch.Str + (int)acc[W_STR];
            int dex = ch.Dex + (int)acc[W_DEX];

            int innerlevel = ch.InnerLevel;
            if (innerlevel < 1) innerlevel = 1;
            int step = (innerlevel - 1) / 12 + 1; // Get_level (belt) = (innerlevel-1)/12 + 1

            var o = new NdStats
            {
                AddCon = (int)acc[W_CON], AddStr = (int)acc[W_STR], AddZen = (int)acc[W_ZEN],
                AddInt = (int)acc[W_INT], AddDex = (int)acc[W_DEX],
                SumStamina = sumStamina, AddMaxLife = (int)acc[W_MAXLIFE],
            };

            o.MaxHP = con * vit + sumStamina + (int)acc[W_MAXLIFE];
            o.MaxForce = zen * inner + (int)acc[W_MAXFORCE];
            o.MaxConc = intel * conc + (int)acc[W_MAXCONC];

            // Base formulas (ND_Battle_Parameter.txt coefficients, uniform across classes).
            // Truncation via (int) matches the C++ (int)(...) casts exactly.
            int def_base = (int)(_pfInner * innerlevel + _pfStep * step + _pfStr * str);
            int mdef_base = (int)(_mfInner * innerlevel + _mfStep * step + _mfInt * intel + _mfZen * zen);
            int pd_base = (int)(_pdInner * innerlevel + _pdStep * step + 5.0 + _pdStr * str); // +5.0 is a hard-coded constant not in the file
            int md_base = (int)(_mdInner * innerlevel + _mdStep * step + _mdZen * zen);
            int par_base = (int)(_parInner * innerlevel + _parStep * step + _parDex * dex);
            int mar_base = (int)(_marInner * innerlevel + _marStep * step + _marInt * intel);
            int pdr_base = (int)(_pdrInner * innerlevel + _pdrStep * step + _pdrDex * dex);
            int mdr_base = (int)(_mdrInner * innerlevel + _mdrStep * step + _mdrInt * intel);

            o.Defence = def_base + (int)acc[W_DEF];
            o.MDefence = mdef_base + (int)acc[W_MDEF];
            o.MinDmg = (int)(pd_base * _pdMinMul) + wpnMin + (int)acc[W_ATKDMG];
            o.MaxDmg = (int)(pd_base * _pdMaxMul) + wpnMax + (int)acc[W_ATKDMG];
            o.MMinDmg = md_base + (int)acc[W_MATKDMG];
            o.MMaxDmg = md_base + (int)acc[W_MATKDMG];
            o.AtkRate = par_base + (int)acc[W_ATKRATE];
            o.AvoidRate = pdr_base + (int)acc[W_AVOID];
            o.MAtkRate = mar_base + (int)acc[W_MATKRATE];   // magic uses where#6 (m_attack_rate) — fixed bug
            o.MAvoidRate = mdr_base + (int)acc[W_MAVOID];   // magic uses where#7 (m_avoid_rate) — fixed bug
            return o;
        }

        // FirstCategory predicates, same as nd_itemtable.h.
        private static bool IsWeaponType(int t) => t == 0 || t == 15 || t == 19;
        private static bool IsClothType(int t) => t == 1 || t == 16 || t == 20;
        private static bool IsAccType(int t) => t == 9;
    }
}
