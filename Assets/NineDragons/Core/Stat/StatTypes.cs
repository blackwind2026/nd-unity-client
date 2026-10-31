namespace NineDragons.Stat
{
    /// <summary>
    /// Character attributes + class + inner level, the base inputs to the stat engine.
    /// Mirrors the C++ <c>nd_stats_compute</c> parameters (baseCon/baseStr/baseZen/baseInt/baseDex,
    /// cClass, innerlevel). These are the raw attribute points the server stores in the
    /// <c>charac</c> DB row (before any item/set bonus).
    /// </summary>
    public struct NdCharacter
    {
        public int Con;
        public int Str;
        public int Zen;
        public int Int;
        public int Dex;
        public int InnerLevel;   // 1-based; step = (InnerLevel-1)/12 + 1
        public int Class;        // 0..11 (0 = Lãng nhân); clamped to 0 if out of range

        public NdCharacter(int con, int str, int zen, int intel, int dex, int innerLevel, int cClass)
        {
            Con = con; Str = str; Zen = zen; Int = intel; Dex = dex;
            InnerLevel = innerLevel; Class = cClass;
        }
    }

    /// <summary>
    /// The 20 equipment slots (0..19). Each slot carries an item id and its FirstCategory type
    /// (0/15/19 weapon, 1/16/20 cloth, 9 accessory/tín vật). Cloth SET bonuses only count
    /// slots 0..4 (upper/lower/gloves/shoes/cap), exactly as the server does.
    /// This matches the C++ <c>invId[20]</c> / <c>invType[20]</c> arrays.
    /// </summary>
    public struct NdEquipment
    {
        public const int SlotCount = 20;

        public int[] Ids;    // length 20
        public int[] Types;  // length 20 (FirstCategory)

        public static NdEquipment Empty()
        {
            return new NdEquipment { Ids = new int[SlotCount], Types = new int[SlotCount] };
        }

        /// <summary>Set item id+type in a slot (0..19). Returns this for chaining in tests.</summary>
        public NdEquipment Set(int slot, int id, int type)
        {
            if (Ids == null) { Ids = new int[SlotCount]; Types = new int[SlotCount]; }
            if (slot >= 0 && slot < SlotCount) { Ids[slot] = id; Types[slot] = type; }
            return this;
        }
    }

    /// <summary>
    /// Fully derived stats — 1:1 with the C++ <c>NdStats</c> struct that the server prints on
    /// its <c>[STAT]</c> log lines. Fields are the numbers M5/UI display.
    /// </summary>
    public struct NdStats
    {
        public int MaxHP, MaxForce, MaxConc;
        public int MinDmg, MaxDmg, Defence, MDefence;
        public int MMinDmg, MMaxDmg;          // magic (khi công) damage
        public int AtkRate, AvoidRate;        // physical accuracy / avoid
        public int MAtkRate, MAvoidRate;      // magic accuracy / avoid
        // diagnostics (same as the C++ struct tail) — how much came from item/set options
        public int AddCon, AddStr, AddZen, AddInt, AddDex, SumStamina, AddMaxLife;
    }
}
