using System;
using System.Collections.Generic;
using System.Numerics;

namespace NineDragons.Assets
{
    /// <summary>
    /// Parsers for the per-map sidecar files that live next to each <c>.xtv</c> in
    /// <c>VR_ENVIRONMENT.XP</c>. Layouts ported from the verified blender_xtv
    /// <c>xtv_format.py</c> (round-trip byte-exact) and cross-checked with the client C++.
    ///
    ///   map_*.XNS — NPC placement list
    ///   map_*.XZI — area/warp/mob-spawn/PvP zones
    ///   map_*.XSS — environment sound instances (i32 count × 73 B records)
    ///   map_*.XFS — environment effect instances (i32 count × 70 B records)
    /// </summary>
    /// <summary>
    /// map_*.XNS — NPC placement + embedded dialog scripts.
    /// Ground truth: <c>DataStructure\XNPC.cpp</c> <c>_XNPCObjectManager::LoadNPCObjectList</c>
    /// (+ <c>_XNPCObject::LoadScript</c>), <c>Script\XNPCScript.cpp</c>
    /// <c>_XNPCMainScript::LoadScript</c>, <c>Script\XNPCScriptConditionItem.cpp</c>.
    ///
    /// Per NPC: 5×i32 (uniqueId, jobClass, tradeClass, npcClass, clanClass), then an OPTIONAL
    /// 3×i32 quest extension (Awaken-era maps) detected per-NPC by the owner's verified peek
    /// heuristic — 4 ints p0..p3 exist with p0,p1,p2 ∈ [0,10M) and p3 ∈ [0,512) — then
    /// len-str name, len-str roll, 68 B object record (i32 meshIndex + 16f matrix), i32 main
    /// script count, and that many main scripts (dialog trees).
    /// </summary>
    public sealed class XnsNpcList
    {
        public string ListFileName = "";                 // plain text (the client freads it raw)
        public List<XnsNpc> Npcs = new List<XnsNpc>();
        public int TrailingBytes;                        // bytes past the declared count (client ignores)

        public static XnsNpcList Parse(byte[] data, string name = null)
        {
            var r = new SpanReader(data, name ?? "XNS");
            var m = new XnsNpcList();
            m.ListFileName = XtvTerrain.Latin1TrimNul(r.Bytes(260));
            int count = r.I32();
            if (count < 0 || count > 65536)
                throw new InvalidDataException($"{r.Source}: implausible NPC count {count}");
            for (int i = 0; i < count; i++)
            {
                var n = new XnsNpc();
                n.UniqueId = r.I32();
                n.JobClass = r.I32();
                n.TradeClass = r.I32();
                n.NpcClass = r.I32();
                n.ClanClass = r.I32();

                // optional 3-int quest extension (see class remarks)
                if (r.Remaining >= 16)
                {
                    int p = r.Position;
                    int p0 = r.I32(), p1 = r.I32(), p2 = r.I32(), p3 = r.I32();
                    r.Position = p;
                    if (p0 >= 0 && p0 < 10_000_000 && p1 >= 0 && p1 < 10_000_000 &&
                        p2 >= 0 && p2 < 10_000_000 && p3 >= 0 && p3 < 512)
                    {
                        n.HasQuestExt = true;
                        n.QuestType = r.I32();
                        n.QuestId = r.I32();
                        n.QuestExtra = r.I32();
                    }
                }

                n.Name = XtvTerrain.Latin1(r.LenBytes());   // CP949 bytes preserved 1:1
                n.Roll = XtvTerrain.Latin1(r.LenBytes());
                n.MeshIndex = r.I32();
                n.Matrix = r.Mat4();

                int scripts = r.I32();
                if (scripts < 0 || scripts > 256)
                    throw new InvalidDataException($"{r.Source}: npc {n.UniqueId} implausible script count {scripts}");
                for (int s = 0; s < scripts; s++)
                    n.Scripts.Add(XnsMainScript.Parse(r));

                m.Npcs.Add(n);
            }
            // Map_Gyodo.XNS carries one extra appended NPC record past the declared count;
            // the client reads exactly `count` records and ignores the rest — so do we.
            m.TrailingBytes = r.Remaining;
            return m;
        }
    }

    public sealed class XnsNpc
    {
        public int UniqueId, JobClass, TradeClass, NpcClass, ClanClass;
        public bool HasQuestExt;
        public int QuestType, QuestId, QuestExtra;
        public string Name = "", Roll = "";
        public int MeshIndex;
        public Matrix4x4 Matrix;
        public List<XnsMainScript> Scripts = new List<XnsMainScript>();
        public Vector3 Position => new Vector3(Matrix.M41, Matrix.M42, Matrix.M43);
        public override string ToString() => $"npc {UniqueId} '{Name}' @({Position.X:0.#},{Position.Z:0.#}) scripts={Scripts.Count}";
    }

    /// <summary>One dialog tree of an NPC (<c>_XNPCMainScript</c>).</summary>
    public sealed class XnsMainScript
    {
        public string ScriptName = "";
        public int CompareClass;
        public List<XnsCondition> Conditions = new List<XnsCondition>();
        // 5 greeting slots × 5 kinds (prev, general, hostility, customer, hero-stay)
        public string[][] GreetingMessages = new string[5][];
        public int[][] GreetingSounds = new int[5][];
        public string[] GoodbyeMessages = new string[4];
        public int[] GoodbyeSounds = new int[4];
        public List<XnsSubScript> SubScripts = new List<XnsSubScript>();

        internal static XnsMainScript Parse(SpanReader r)
        {
            var s = new XnsMainScript();
            s.ScriptName = XtvTerrain.Latin1TrimNul(r.Bytes(32));
            s.CompareClass = r.I32();

            int ccount = r.I32();
            if (ccount < 0 || ccount > 1024)
                throw new InvalidDataException($"{r.Source}: implausible npc condition count {ccount}");
            for (int i = 0; i < ccount; i++) s.Conditions.Add(XnsCondition.Parse(r));

            for (int i = 0; i < 5; i++)
            {
                s.GreetingMessages[i] = new string[5];
                s.GreetingSounds[i] = new int[5];
                for (int k = 0; k < 5; k++) s.GreetingMessages[i][k] = XtvTerrain.Latin1(r.LenBytes());
                for (int k = 0; k < 5; k++) s.GreetingSounds[i][k] = r.I32();
            }
            for (int i = 0; i < 4; i++) s.GoodbyeMessages[i] = XtvTerrain.Latin1(r.LenBytes());
            for (int i = 0; i < 4; i++) s.GoodbyeSounds[i] = r.I32();

            int sub = r.I32();
            if (sub < 0 || sub > 1024)
                throw new InvalidDataException($"{r.Source}: implausible npc subscript count {sub}");
            for (int i = 0; i < sub; i++) s.SubScripts.Add(XnsSubScript.Parse(r));
            return s;
        }
    }

    /// <summary>One trigger condition (<c>_XNPCScriptConditionItem</c>: i32 id + 64 B name + 28 B data + len-str).</summary>
    public sealed class XnsCondition
    {
        public int Id;
        public string ConditionName = "";
        public byte[] Data;                  // 28 B _XNPCSUBSCRIPT_CONDITION raw
        public string CompareString = "";

        internal static XnsCondition Parse(SpanReader r)
        {
            var c = new XnsCondition();
            c.Id = r.I32();
            c.ConditionName = XtvTerrain.Latin1TrimNul(r.Bytes(64));
            c.Data = r.Bytes(28);
            c.CompareString = XtvTerrain.Latin1(r.LenBytes());
            return c;
        }
    }

    /// <summary>One dialog menu entry (<c>_XNPCScriptItem</c>).</summary>
    public sealed class XnsSubScript
    {
        public int Enable, ScriptClass, CompareClass;
        public int[] Conditions = new int[13];
        public int[] Quantities = new int[5];
        public string QuantityString = "", Message = "";
        public string[] Msg1 = new string[5], Msg2 = new string[5], Msg3 = new string[5];
        public List<XnsCondition> SubConditions = new List<XnsCondition>();

        internal static XnsSubScript Parse(SpanReader r)
        {
            var s = new XnsSubScript();
            s.Enable = r.I32();
            s.ScriptClass = r.I32();
            s.CompareClass = r.I32();
            for (int i = 0; i < 13; i++) s.Conditions[i] = r.I32();
            for (int i = 0; i < 5; i++) s.Quantities[i] = r.I32();
            s.QuantityString = XtvTerrain.Latin1(r.LenBytes());
            s.Message = XtvTerrain.Latin1(r.LenBytes());
            for (int j = 0; j < 5; j++)
            {
                s.Msg1[j] = XtvTerrain.Latin1(r.LenBytes());
                s.Msg2[j] = XtvTerrain.Latin1(r.LenBytes());
                s.Msg3[j] = XtvTerrain.Latin1(r.LenBytes());
            }
            int ccount = r.I32();
            if (ccount < 0 || ccount > 1024)
                throw new InvalidDataException($"{r.Source}: implausible subscript condition count {ccount}");
            for (int i = 0; i < ccount; i++) s.SubConditions.Add(XnsCondition.Parse(r));
            return s;
        }
    }

    public sealed class XziAreaList
    {
        public const int CategoryMobSpawn = 2;
        public const int CategoryMapWarp = 6;
        public const int CategoryPvpArea = 7;
        public const int ShapeBox = 0;

        public List<XziArea> Areas = new List<XziArea>();

        public static XziAreaList Parse(byte[] data, string name = null)
        {
            var r = new SpanReader(data, name ?? "XZI");
            var m = new XziAreaList();
            int count = r.I32();
            if (count < 0 || count > 65536)
                throw new InvalidDataException($"{r.Source}: implausible area count {count}");
            for (int i = 0; i < count; i++)
            {
                var a = new XziArea();
                a.Category = r.U8();
                a.Num = r.I32();
                a.Name = XtvTerrain.Latin1TrimNul(r.Bytes(21));
                a.Shape = r.U8();
                if (a.Shape == ShapeBox)
                {
                    a.BoxPoints = new float[8];
                    for (int k = 0; k < 8; k++) a.BoxPoints[k] = r.F32();
                }
                else
                {
                    a.Center = new Vector2(r.F32(), r.F32());
                    a.Radius = r.F32();
                }
                a.Special = r.I32();
                if (a.Category == CategoryMapWarp)
                {
                    a.TargetZone = r.I32();
                    a.TargetWarp = r.I32();
                }
                if (a.Category == CategoryMobSpawn)
                {
                    int tcount = r.I32();
                    if (tcount < 0 || tcount > 4096) throw new InvalidDataException($"{r.Source}: implausible mob type count {tcount}");
                    for (int k = 0; k < tcount; k++)
                        a.MobTypes.Add((r.I32(), r.I32()));
                    int pcount = r.I32();
                    if (pcount < 0 || pcount > 65536) throw new InvalidDataException($"{r.Source}: implausible spawn point count {pcount}");
                    a.SpawnPointsRaw = r.Bytes(pcount * 12);       // 2 floats + int each
                }
                if (a.Category == CategoryPvpArea)
                {
                    a.PvpType = r.U8();
                    a.PvpSafezones = new int[4];
                    for (int k = 0; k < 4; k++) a.PvpSafezones[k] = r.I32();
                    a.PvpComplete = r.I32();
                }
                if (a.Special == 6)
                {
                    for (int k = 0; k < 3; k++)
                        a.MultiTargets.Add(new XziMultiTarget
                        {
                            Zone = r.I32(),
                            Warp = r.I32(),
                            Passage = r.I32(),
                            Name = XtvTerrain.Latin1TrimNul(r.Bytes(21))
                        });
                }
                m.Areas.Add(a);
            }
            if (r.Remaining != 0)
                throw new InvalidDataException($"{r.Source}: {r.Remaining} trailing bytes");
            return m;
        }
    }

    public sealed class XziArea
    {
        public byte Category;             // 2=mob spawn, 6=map warp, 7=PvP, others = plain area
        public int Num;
        public string Name = "";
        public byte Shape;                // 0=box (8 floats), else circle (center+radius)
        public float[] BoxPoints;
        public Vector2 Center;
        public float Radius;
        public int Special;               // 6 → 3 extra multi-warp targets
        public int TargetZone, TargetWarp;
        public List<(int type, int count)> MobTypes = new List<(int, int)>();
        public byte[] SpawnPointsRaw;     // (f32 x, f32 z, i32) per point
        public byte PvpType;
        public int[] PvpSafezones;
        public int PvpComplete;
        public List<XziMultiTarget> MultiTargets = new List<XziMultiTarget>();
    }

    public sealed class XziMultiTarget
    {
        public int Zone, Warp, Passage;
        public string Name = "";
    }

    /// <summary>Fixed-record sidecars: XSS (73 B: i32 + matrix + 5 B) and XFS (70 B: i32 + matrix + 2 B).</summary>
    public static class MapFixedRecords
    {
        public static List<byte[]> ParseXss(byte[] data, string name = null) => ParseFixed(data, 73, name ?? "XSS");
        public static List<byte[]> ParseXfs(byte[] data, string name = null) => ParseFixed(data, 70, name ?? "XFS");

        private static List<byte[]> ParseFixed(byte[] data, int recSize, string src)
        {
            var r = new SpanReader(data, src);
            int count = r.I32();
            var list = new List<byte[]>();
            for (int i = 0; i < Math.Max(count, 0); i++)
                list.Add(r.Bytes(recSize));
            if (r.Remaining != 0)
                throw new InvalidDataException($"{src}: {r.Remaining} trailing bytes");
            return list;
        }
    }
}
