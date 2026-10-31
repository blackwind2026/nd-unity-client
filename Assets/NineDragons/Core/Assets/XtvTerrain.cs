using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace NineDragons.Assets
{
    /// <summary>
    /// Parser for the client's <c>.xtv</c> terrain/map files (package <c>VR_ENVIRONMENT.XP</c>,
    /// one per map, versions 0.87/0.88 and 0.91).
    ///
    /// Ground truth, in order of authority:
    ///  1. <c>MeshControl\XLodTerrain.cpp</c> <c>LoadTerrain()</c> (client C++; the Linux build
    ///     additionally carries the owner's "Codex" resync helpers for 0.91 quirks — see below).
    ///  2. The blender_xtv plugin's <c>xtv_format.py</c> (byte-exact round-trip verified on
    ///     42 maps), from which this layout was ported.
    ///
    /// Layout (little-endian), after the 148-byte header:
    ///   f32[V*V] heights, u32[V*V] diffuse ARGB          (V = tileCount*8 + 1)
    ///   grass:   tileCount² × { u8 flag, [64 B blend if flag] }
    ///   pathfind: (tileCount«4)·tileCount·2 bytes
    ///   splat:   i32 layerCount (≤10), layerCount×5f transforms, layerCount×(i32 len + name),
    ///            tileCount² tiles: per layer i32 use (+64 B blend when use && layer>0);
    ///            if any use: per used layer>0 { u32 tri, tri*6 B indices },
    ///            then u32 tri0 + tri0*6 B + 64 B blend0
    ///   meshes:  i32 count × { i32 len, name bytes XOR 0xEF }
    ///   objects: i32 count, [v0.91: extra i32], count × 68 B { i32 meshIndex, 16f matrix }
    ///   functional objects, speedtrees, i32 collidePolyCount,
    ///   u32 0xFFFFAF05 + i32 obbCount × (68 B v0.88 | 72 B v0.91) records,
    ///   water { i32 n, n*4 B props, n*28 B heights, 8×32 B texnames },
    ///   envscript tileCount²×4 B, envsound name 32 B (XOR 0xEF),
    ///   weather (nominally 232×32 B — but VARIABLE, see below), path objects, splat cache
    ///   { u32 readSize ≤1024, tileCount² × readSize DDS images }, optional 0-padding.
    ///
    /// The weather block length varies per map (measured by the owner across all 43 maps:
    /// 40×7432, and one each 7200/6736/6504 bytes to the splat cache). The owner's client fix
    /// resyncs by scanning for the splat-cache signature <c>[u32 ≤1024]["DDS "]</c>; this parser
    /// does the same when the nominal size fails validation (<see cref="Resynced"/>).
    /// </summary>
    public sealed class XtvTerrain
    {
        public const uint Indicator = 0x0000AEDE;      // .xtv (client)
        public const uint ToolIndicator = 0x0000AE05;  // .XTG (tool)
        public const uint ObbIndicator = 0xFFFFAF05;
        public const int TileVerts = 9;                // vertices per tile edge
        public const int BlendSize = 64;               // 8×8 splat blend map
        public const int WeatherNominalSize = 232 * 32;

        // ---- header ----
        public float Version;
        public float Width, Height, GridSize;
        public int TileCount;
        public string ObjectListName = "";             // XOR-0xEF decoded
        public float[] BackgroundColor = new float[4];
        public int Fog, RangeBaseFog, TableFogExpMode, FogMode;
        public float ZNear, ZFar;
        public float[] Lod = new float[3];
        public float LodFarPlane, SkyboxHeight, LimitCamW, LimitCamH;

        public bool IsV91 => Version > 0.885f;
        public int VertexPerSide => TileCount * (TileVerts - 1) + 1;

        // ---- terrain ----
        public float[] Heights;                        // V*V, row-major [z*V + x]
        public uint[] DiffuseArgb;                     // V*V
        public byte[] GrassFlags;                      // tileCount²
        public Dictionary<int, byte[]> GrassBlends = new Dictionary<int, byte[]>();
        public byte[] Pathfind;                        // raw walkability grid

        // ---- splat ----
        public int LayerCount;
        public float[][] LayerTransforms = Array.Empty<float[]>();  // 5 floats each
        public string[] LayerTextureNames = Array.Empty<string>();  // plain (NOT XOR)
        public XtvTileSplat[] TileSplats = Array.Empty<XtvTileSplat>();

        // ---- scene ----
        public string[] MeshNames = Array.Empty<string>();          // XOR-0xEF decoded
        public List<XtvObject> Objects = new List<XtvObject>();
        public List<XtvFunctionalObject> FunctionalObjects = new List<XtvFunctionalObject>();
        public List<XtvSpeedTree> SpeedTrees = new List<XtvSpeedTree>();
        public int CollidePolyCount;
        public List<byte[]> ObbRecords = new List<byte[]>();        // 68/72 B raw
        public XtvWater Water;
        public byte[] EnvScript;                       // tileCount²×4 raw
        public string EnvSoundName = "";               // XOR-0xEF decoded

        // ---- tail ----
        public byte[] WeatherBlob;                     // raw weather (+path objects when resynced)
        public int PathObjectInsertIndex;              // only valid when !Resynced
        public byte[] PathCurvesRaw = Array.Empty<byte>(); // 72 B per curve, only when !Resynced
        public int SplatImageSize;
        public byte[][] SplatImages = Array.Empty<byte[]>(); // tileCount² DDS images
        public bool Resynced;                          // tail located by signature scan
        public int TrailingBytes;
        public bool ObjectsExtraInt;                   // object section carried an extra i32 after the count
        public bool ObjectsCompactAffine;              // v0.91 compact [lead][idx][15f] object records
        public XtvTriggerLayout TriggerLayout;         // which trigger encoding this file used
        public int SpeedTreeGapBytes;                  // unread gap before the speedtree section

        /// <summary>World-space X of height column j (same for Z of row i — maps are square).</summary>
        public float WorldCoord(int j) => -Width * 0.5f + GridSize * j;

        // ------------------------------------------------------------------ walkability ----
        // The .xtv <see cref="Pathfind"/> block is the client's walk/collision grid. It is a
        // (TileCount·16)² bit grid, packed as (TileCount·16) rows × TileCount little-endian
        // WORDs (row stride = TileCount WORDs = TileCount·16 bits). A SET bit means the cell is
        // BLOCKED (wall/water/cliff); a clear bit or "off the grid" means walkable.
        //
        // This is the same grid the server keeps as nfofile/obbdata/<map>_tile.xpft (it validates
        // MONSTER movement against it — nd-world src/nd_tile.h), so honoring it here keeps the
        // client walking where the real client and the server agree it can. Ground truth for the
        // bit test and the world→array conversion:
        //   _XLodTerrain::GetPathFindAreaInfo   (MeshControl/XLodTerrain.cpp:6034)
        //   caller conversion + clamp           (DataStructure/XUser.cpp:7652)
        //   click-target reject rule            (XProcess/XProc_MainGame.cpp:16278)
        //     arrayX = (worldX + Width/2) / (GridSize/2)   [GridSize/2 == 1.0 on stock maps]
        //     arrayZ likewise from worldZ;  row = arrayX, word = arrayZ>>4, bit = 0x8000>>(arrayZ&15)

        private void WorldToPathArray(float worldX, float worldZ, out int ax, out int az)
        {
            float cell = GridSize * 0.5f;                 // == m_GridRealSize/2 in the C++ client
            ax = (int)((worldX + Width * 0.5f) / cell);
            az = (int)((worldZ + Width * 0.5f) / cell);
        }

        /// <summary>Raw grid test on array coords — mirrors GetPathFindAreaInfo exactly, including
        /// its "out of range ⇒ blocked" rule (XLodTerrain.cpp:6036-6039).</summary>
        private bool CellBlocked(int ax, int az)
        {
            int lim = TileCount << 4;                     // TileCount·16
            if (ax < 0 || ax >= lim || az < 0 || az >= lim) return true;
            int b = (ax * TileCount + (az >> 4)) * 2;     // TileCount WORDs per row, 2 bytes each
            if (b < 0 || b + 1 >= Pathfind.Length) return true;
            ushort w = (ushort)(Pathfind[b] | (Pathfind[b + 1] << 8));
            return (w & (ushort)(0x8000 >> (az & 15))) != 0;   // g_XWordFilter[az&15]; SET = blocked
        }

        /// <summary>True if world (x,z) is walkable — the player must not step into a cell where
        /// this is false (XUser.cpp:7659). No grid ⇒ everywhere walkable. Off-map clamps to the
        /// edge cell exactly like the C++ caller (XUser.cpp:7654), never reading out of range.</summary>
        public bool IsWalkable(float worldX, float worldZ)
        {
            if (Pathfind == null || Pathfind.Length == 0 || GridSize <= 0f) return true;
            WorldToPathArray(worldX, worldZ, out int ax, out int az);
            int lim = TileCount << 4;
            if (ax < 0) ax = 0; else if (ax >= lim) ax = lim - 1;
            if (az < 0) az = 0; else if (az >= lim) az = lim - 1;
            return !CellBlocked(ax, az);
        }

        /// <summary>Number of the 8 neighbouring cells that are blocked
        /// (GetPathFindAreaNeighborInfo, XLodTerrain.cpp:6042). A click target with all 8 blocked
        /// (returns 8, i.e. &gt;7) is unreachable and the client refuses to path there.</summary>
        public int BlockedNeighborCount(float worldX, float worldZ)
        {
            if (Pathfind == null || Pathfind.Length == 0 || GridSize <= 0f) return 0;
            WorldToPathArray(worldX, worldZ, out int cx, out int cz);
            int sum = 0;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if ((dx != 0 || dz != 0) && CellBlocked(cx + dx, cz + dz)) sum++;
            return sum;
        }

        public static string DecodeXorName(byte[] raw)
        {
            var sb = new StringBuilder(raw.Length);
            foreach (byte b in raw)
            {
                if (b == 0) break;
                sb.Append((char)(b ^ 0xEF));
            }
            return sb.ToString();
        }

        /// <summary>Byte offset right after the mesh-name list (diagnostics).</summary>
        public int PosAfterMeshes { get; private set; }

        /// <summary>Parse only through the mesh-name list (for diagnostics of broken maps).</summary>
        public static XtvTerrain ParseFrontOnly(byte[] data, string name = null)
            => ParseCore(data, name, frontOnly: true);

        public static XtvTerrain Parse(byte[] data, string name = null)
            => ParseCore(data, name, frontOnly: false);

        private static XtvTerrain ParseCore(byte[] data, string name, bool frontOnly)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var r = new SpanReader(data, name ?? "XTV");
            var m = new XtvTerrain();

            // ---------------- header (148 B) ----------------
            uint ind = r.U32();
            if (ind != Indicator && ind != ToolIndicator)
                throw new InvalidDataException($"{r.Source}: bad indicator 0x{ind:X8}");
            m.Version = r.F32();
            m.Width = r.F32(); m.Height = r.F32(); m.GridSize = r.F32();
            m.TileCount = r.I32();
            if (m.TileCount <= 0 || m.TileCount > 256)
                throw new InvalidDataException($"{r.Source}: implausible tile count {m.TileCount}");
            m.ObjectListName = DecodeXorName(r.Bytes(32));
            for (int i = 0; i < 4; i++) m.BackgroundColor[i] = r.F32();
            m.Fog = r.I32(); m.RangeBaseFog = r.I32(); m.TableFogExpMode = r.I32(); m.FogMode = r.I32();
            m.ZNear = r.F32(); m.ZFar = r.F32();
            for (int i = 0; i < 3; i++) m.Lod[i] = r.F32();
            m.LodFarPlane = r.F32(); m.SkyboxHeight = r.F32();
            m.LimitCamW = r.F32(); m.LimitCamH = r.F32();
            r.Bytes(24);                                       // 6 reserved floats

            int tc = m.TileCount, v = m.VertexPerSide;

            // ---------------- terrain ----------------
            m.Heights = new float[v * v];
            for (int i = 0; i < m.Heights.Length; i++) m.Heights[i] = r.F32();
            m.DiffuseArgb = new uint[v * v];
            for (int i = 0; i < m.DiffuseArgb.Length; i++) m.DiffuseArgb[i] = r.U32();

            m.GrassFlags = new byte[tc * tc];
            for (int i = 0; i < tc * tc; i++)
            {
                byte flag = r.U8();
                m.GrassFlags[i] = flag;
                if (flag != 0) m.GrassBlends[i] = r.Bytes(BlendSize);
            }

            m.Pathfind = r.Bytes((tc << 4) * tc * 2);

            // ---------------- splat layers ----------------
            m.LayerCount = r.I32();
            if (m.LayerCount < 0 || m.LayerCount > 10)
                throw new InvalidDataException($"{r.Source}: splat layer overflow {m.LayerCount}");
            m.LayerTransforms = new float[m.LayerCount][];
            for (int i = 0; i < m.LayerCount; i++)
            {
                m.LayerTransforms[i] = new float[5];
                for (int k = 0; k < 5; k++) m.LayerTransforms[i][k] = r.F32();
            }
            m.LayerTextureNames = new string[m.LayerCount];
            for (int i = 0; i < m.LayerCount; i++)
                m.LayerTextureNames[i] = r.LenString();

            m.TileSplats = new XtvTileSplat[tc * tc];
            for (int t = 0; t < tc * tc; t++)
            {
                var ts = new XtvTileSplat { UseLayer = new int[m.LayerCount] };
                bool any = false;
                for (int layer = 0; layer < m.LayerCount; layer++)
                {
                    int use = r.I32();
                    ts.UseLayer[layer] = use;
                    if (use != 0)
                    {
                        any = true;
                        if (layer > 0) ts.Blends[layer] = r.Bytes(BlendSize);
                    }
                }
                if (any)
                {
                    for (int layer = 1; layer < m.LayerCount; layer++)
                    {
                        if (ts.UseLayer[layer] == 0) continue;
                        uint tri = r.U32();
                        if (tri > 100000) throw new InvalidDataException($"{r.Source}: tile {t} implausible splat tri {tri}");
                        ts.IndexBuffers[layer] = r.Bytes((int)tri * 6);
                    }
                    uint tri0 = r.U32();
                    if (tri0 > 100000) throw new InvalidDataException($"{r.Source}: tile {t} implausible base tri {tri0}");
                    ts.BaseIndexBuffer = r.Bytes((int)tri0 * 6);
                    ts.BaseBlend = r.Bytes(BlendSize);
                }
                m.TileSplats[t] = ts;
            }

            // ---------------- mesh list ----------------
            int meshCount = r.I32();
            if (meshCount < 0 || meshCount > 65536)
                throw new InvalidDataException($"{r.Source}: implausible mesh count {meshCount}");
            m.MeshNames = new string[meshCount];
            for (int i = 0; i < meshCount; i++)
                m.MeshNames[i] = DecodeXorName(r.LenBytes());
            m.PosAfterMeshes = r.Position;
            if (frontOnly) return m;

            // ---------------- objects → end (two v0.91 sub-variants) ----------------
            // The client reads [i32 count][count × 68 B] with NO extra int; SOME v0.91 maps
            // carry one extra i32 after the count (the blender plugin's reading, byte-verified
            // on those maps). Try the client layout first; on any downstream failure, roll
            // back and retry with the extra int.
            int sceneStart = r.Position;
            // Trigger layout differs between file generations and is not flagged in the file;
            // try the likely one for this version first, then the others. A wrong choice
            // desyncs immediately (OBB anchor / sanity checks throw), so first success wins.
            var triggerOrder = m.IsV91
                ? new[] { XtvTriggerLayout.V91, XtvTriggerLayout.V88, XtvTriggerLayout.None }
                : new[] { XtvTriggerLayout.V88, XtvTriggerLayout.None, XtvTriggerLayout.V91 };
            InvalidDataException last = null;
            foreach (bool extra in new[] { false, true })
                foreach (var layout in triggerOrder)
                {
                    try
                    {
                        r.Position = sceneStart;
                        m.ResetSceneSections();
                        ParseObjectsToEnd(m, r, data, extra, layout);
                        m.ObjectsExtraInt = extra;
                        m.TriggerLayout = layout;
                        return m;
                    }
                    catch (InvalidDataException ex) { last = ex; }
                }
            throw last;
        }

        private void ResetSceneSections()
        {
            Objects.Clear(); FunctionalObjects.Clear(); SpeedTrees.Clear(); ObbRecords.Clear();
            Water = null; EnvScript = null; EnvSoundName = "";
            WeatherBlob = null; PathCurvesRaw = Array.Empty<byte>();
            SplatImages = Array.Empty<byte[]>(); SplatImageSize = 0;
            Resynced = false; ObjectsCompactAffine = false;
        }

        private static void ParseObjectsToEnd(XtvTerrain m, SpanReader r, byte[] data, bool extraInt, XtvTriggerLayout layout)
        {
            int tc = m.TileCount;

            // ---------------- world objects ----------------
            int objCount = r.I32();
            if (objCount < 0 || objCount > 200000)
                throw new InvalidDataException($"{r.Source}: implausible object count {objCount}");
            if (extraInt) r.I32();                      // plugin-observed variant (value 0)
            var raw = r.Bytes(objCount * 68);

            // v0.91 "compact affine" content detection, exactly as the owner's client fix:
            // second record's int@0 == 1.0f bits and int@4 a small index → records are
            // [4 B lead][i32 meshIndex][15f affine matrix], _44 = 1.
            bool compact = false;
            if (objCount > 1)
            {
                int i0 = BitConverter.ToInt32(raw, 68);
                int i1 = BitConverter.ToInt32(raw, 68 + 4);
                compact = i0 == 0x3F800000 && i1 >= 0 && i1 < 4096;
            }
            m.ObjectsCompactAffine = compact;
            for (int i = 0; i < objCount; i++)
            {
                int o = i * 68;
                var obj = new XtvObject();
                if (compact)
                {
                    obj.MeshIndex = BitConverter.ToInt32(raw, o + 4);
                    obj.Matrix = ReadMatrix(raw, o + 8, 15);
                }
                else
                {
                    obj.MeshIndex = BitConverter.ToInt32(raw, o);
                    obj.Matrix = ReadMatrix(raw, o + 4, 16);
                }
                m.Objects.Add(obj);
            }

            // ---------------- functional objects ----------------
            // Some maps prefix this section with a bare version float 1.0 — the owner's client
            // peeks and skips it (XFunctionalObject.cpp "skipped leading version float").
            if (r.Remaining >= 4 && r.PeekByte(0) == 0x00 && r.PeekByte(1) == 0x00 &&
                r.PeekByte(2) == 0x80 && r.PeekByte(3) == 0x3F)
                r.I32();
            int foCount = r.I32();
            if (foCount < 0 || foCount > 4096)
                throw new InvalidDataException($"{r.Source}: implausible functional-object count {foCount}");
            for (int i = 0; i < foCount; i++)
            {
                var fo = new XtvFunctionalObject();
                fo.Name = Latin1(r.LenBytes());
                fo.UniqueId = r.I32();
                fo.ObjectType = r.I32();
                fo.WideViewRaw = r.I32();
                fo.Relocate = r.I32();
                // Only the LOW 16 bits are the wide-view boolean; the high half often carries
                // 0xFFFF (two 16-bit fields packed). Testing all 32 bits desyncs the stream
                // (measured over 48 maps: raw values {0,1,-65536,-65535}).
                if ((fo.WideViewRaw & 0xFFFF) != 0)
                {
                    fo.Camera = new float[4];
                    for (int k = 0; k < 4; k++) fo.Camera[k] = r.F32();
                    fo.SysMessage = Latin1(r.LenBytes());
                    fo.Description = Latin1(r.LenBytes());
                }
                fo.MeshIndex = r.I32();
                fo.Matrix = r.Mat4();
                if (layout != XtvTriggerLayout.None) fo.Triggers = ParseTriggers(r, layout);
                m.FunctionalObjects.Add(fo);
            }

            // ---------------- speedtrees ----------------
            // The section sits between here and the hard OBB anchor:
            //   [speedtrees][i32 collidePolyCount][u32 0xFFFFAF05]
            // Some maps leave a small GAP after the functional objects (the owner's client
            // resyncs by probing for a valid speedtree start — "Truc Thon" fix). We anchor on
            // the OBB indicator and accept the first start that parses EXACTLY to it.
            int obbPos = FindObbIndicator(data, r.Position);
            if (obbPos < 0)
                throw new InvalidDataException($"{r.Source}: OBB indicator not found after {r.Position}");
            int speedTreeEnd = obbPos - 4;              // collidePolyCount sits right before
            int sectionStart = r.Position;
            bool stOk = false;
            int scanLimit = Math.Min(speedTreeEnd - 4, sectionStart + 4096);
            for (int start = sectionStart; start <= scanLimit; start++)
            {
                r.Position = start;
                m.SpeedTrees.Clear();
                if (TryParseSpeedTrees(m, r, speedTreeEnd))
                {
                    m.SpeedTreeGapBytes = start - sectionStart;
                    stOk = true;
                    break;
                }
            }
            if (!stOk)
                throw new InvalidDataException($"{r.Source}: no valid speedtree section before OBB at {obbPos}");
            // r.Position == speedTreeEnd here.

            // ---------------- collide / OBB ----------------
            m.CollidePolyCount = r.I32();
            if (r.U32() != ObbIndicator)
                throw new InvalidDataException($"{r.Source}: bad OBB indicator at {r.Position - 4}");
            int obbCount = r.I32();
            if (obbCount < 0 || obbCount > 200000)
                throw new InvalidDataException($"{r.Source}: implausible OBB count {obbCount}");
            int obbSize = m.IsV91 ? 72 : 68;            // v0.91 adds a 4-byte field per record
            for (int i = 0; i < obbCount; i++) m.ObbRecords.Add(r.Bytes(obbSize));

            // ---------------- water ----------------
            int wcount = r.I32();
            if (wcount < 0 || wcount > 4096)
                throw new InvalidDataException($"{r.Source}: implausible water count {wcount}");
            m.Water = new XtvWater
            {
                Count = wcount,
                Props = r.Bytes(wcount * 4),
                Heights = r.Bytes(wcount * 28),
                TextureNames = new string[8]
            };
            for (int i = 0; i < 8; i++) m.Water.TextureNames[i] = Latin1TrimNul(r.Bytes(32));

            // ---------------- env script / sound ----------------
            m.EnvScript = r.Bytes(tc * tc * 4);
            m.EnvSoundName = DecodeXorName(r.Bytes(32));

            // ---------------- weather + path objects + splat cache ----------------
            // Nominal path first; if the splat signature check fails, resync by scanning
            // (the weather block length varies per map — the owner's client does the same).
            int tailStart = r.Position;
            bool ok = TryNominalTail(m, r, tc);
            if (!ok)
            {
                r.Position = tailStart;
                int splatStart = FindSplatSignature(data, tailStart);
                if (splatStart < 0)
                    throw new InvalidDataException($"{r.Source}: splat-cache signature not found after {tailStart}");
                m.WeatherBlob = r.Bytes(splatStart - tailStart);
                m.Resynced = true;
                ReadSplatCache(m, r, tc);
            }

            // Awaken maps may pad the end with zero bytes.
            int trailing = r.Remaining;
            if (trailing > 0)
            {
                var tail = r.Bytes(trailing);
                foreach (byte b in tail)
                    if (b != 0) throw new InvalidDataException($"{r.Source}: {trailing} non-zero trailing bytes");
            }
            m.TrailingBytes = trailing;
        }

        private static Matrix4x4 ReadMatrix(byte[] raw, int ofs, int floats)
        {
            var f = new float[16];
            for (int i = 0; i < floats; i++) f[i] = BitConverter.ToSingle(raw, ofs + i * 4);
            if (floats == 15) f[15] = 1f;               // compact affine: _44 implied
            return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7],
                                 f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }

        private static bool TryNominalTail(XtvTerrain m, SpanReader r, int tc)
        {
            try
            {
                if (r.Remaining < WeatherNominalSize + 12) return false;
                var weather = r.Bytes(WeatherNominalSize);
                int insert = r.I32();
                int curveCount = r.I32();
                if (curveCount < 0 || curveCount > 4096 || r.Remaining < curveCount * 72 + 4) return false;
                var curves = r.Bytes(curveCount * 72);
                int probe = r.Position;
                uint readSize = r.U32();
                if (readSize == 0 || readSize > 1024) return false;
                if (r.Remaining < 4 || !(r.PeekByte(0) == 'D' && r.PeekByte(1) == 'D' &&
                                          r.PeekByte(2) == 'S' && r.PeekByte(3) == ' ')) return false;
                r.Position = probe;
                m.WeatherBlob = weather;
                m.PathObjectInsertIndex = insert;
                m.PathCurvesRaw = curves;
                ReadSplatCache(m, r, tc);
                return true;
            }
            catch (InvalidDataException) { return false; }
        }

        private static void ReadSplatCache(XtvTerrain m, SpanReader r, int tc)
        {
            m.SplatImageSize = (int)r.U32();
            if (m.SplatImageSize <= 0 || m.SplatImageSize > 1024)
                throw new InvalidDataException($"{r.Source}: implausible splat image size {m.SplatImageSize}");
            m.SplatImages = new byte[tc * tc][];
            for (int i = 0; i < tc * tc; i++)
                m.SplatImages[i] = r.Bytes(m.SplatImageSize);
        }

        /// <summary>Parse the speedtree list; true iff it ends exactly at <paramref name="end"/>.</summary>
        private static bool TryParseSpeedTrees(XtvTerrain m, SpanReader r, int end)
        {
            try
            {
                int count = r.I32();
                if (count < 0 || count > 100) return false;
                for (int i = 0; i < count; i++)
                {
                    var name = r.Bytes(64);
                    if (name[0] == 0) return false;                    // tree entries carry a filename
                    uint inst = r.U32();
                    if (inst > 100000) return false;
                    var st = new XtvSpeedTree { Name = Latin1TrimNul(name), Positions = new Vector3[inst] };
                    for (int k = 0; k < inst; k++) st.Positions[k] = r.Vec3();
                    m.SpeedTrees.Add(st);
                }
                return r.Position == end;
            }
            catch (InvalidDataException) { return false; }
        }

        private static int FindObbIndicator(byte[] d, int from)
        {
            for (int i = from; i + 4 <= d.Length; i++)
                if (d[i] == 0x05 && d[i + 1] == 0xAF && d[i + 2] == 0xFF && d[i + 3] == 0xFF)
                    return i;
            return -1;
        }

        /// <summary>Scan for the splat-cache pattern <c>[u32 1..1024]["DDS "]</c> (owner's client fix).</summary>
        private static int FindSplatSignature(byte[] d, int from)
        {
            int limit = Math.Min(d.Length - 8, from + 256 * 1024);
            for (int i = from; i <= limit; i++)
            {
                if (d[i + 4] != 'D' || d[i + 5] != 'D' || d[i + 6] != 'S' || d[i + 7] != ' ') continue;
                uint size = (uint)(d[i] | d[i + 1] << 8 | d[i + 2] << 16 | d[i + 3] << 24);
                if (size >= 1 && size <= 1024) return i;
            }
            return -1;
        }

        /// <summary>
        /// Trigger script encodings observed in real data:
        /// V91 (plugin-verified maps): id, name, enable(i32), compare(u8), condCount(i32),
        ///   position(i32), conds, actions, excepted.
        /// V88 (JAENAM-class maps, decoded byte-by-byte from MAP_JAENAM_BOSS.xtv): id, name,
        ///   enable(i32), position(i32), condCount(i32), compare(u8), conds, actions, excepted.
        /// Condition = 96 B (i32 + 64 B + 28 B); action = 100 B (i32 + 64 B + 32 B) + len-str.
        /// </summary>
        private static List<XtvTrigger> ParseTriggers(SpanReader r, XtvTriggerLayout layout)
        {
            int count = r.I32();
            if (count < 0 || count > 1024)
                throw new InvalidDataException($"{r.Source}: implausible trigger count {count}");
            var list = new List<XtvTrigger>(count);
            for (int i = 0; i < count; i++)
            {
                var t = new XtvTrigger();
                t.Id = r.I32();
                t.Name = Latin1(r.LenBytes());
                t.Enable = r.I32();
                int ccount;
                if (layout == XtvTriggerLayout.V91)
                {
                    t.CompareClass = r.U8();
                    ccount = r.I32();
                    t.Position = r.I32();
                }
                else
                {
                    t.Position = r.I32();
                    ccount = r.I32();
                    t.CompareClass = r.U8();
                }
                if (ccount < 0 || ccount > 1024) throw new InvalidDataException($"{r.Source}: implausible trigger cond count {ccount}");
                for (int k = 0; k < ccount; k++) t.Conditions.Add(r.Bytes(4 + 64 + 28));
                t.Actions = ParseActions(r);
                t.ExceptedActions = ParseActions(r);
                list.Add(t);
            }
            return list;
        }

        private static List<XtvTriggerAction> ParseActions(SpanReader r)
        {
            int count = r.I32();
            if (count < 0 || count > 1024)
                throw new InvalidDataException($"{r.Source}: implausible trigger action count {count}");
            var list = new List<XtvTriggerAction>(count);
            for (int i = 0; i < count; i++)
                list.Add(new XtvTriggerAction { Head = r.Bytes(4 + 64 + 32), Message = Latin1(r.LenBytes()) });
            return list;
        }

        internal static string Latin1(byte[] b) => Encoding.GetEncoding(28591).GetString(b);
        internal static string Latin1TrimNul(byte[] b)
        {
            int n = Array.IndexOf(b, (byte)0);
            return Encoding.GetEncoding(28591).GetString(b, 0, n < 0 ? b.Length : n);
        }
    }

    public enum XtvTriggerLayout { None, V91, V88 }

    public sealed class XtvTileSplat
    {
        public int[] UseLayer = Array.Empty<int>();
        public Dictionary<int, byte[]> Blends = new Dictionary<int, byte[]>();        // layer>0 → 64 B
        public Dictionary<int, byte[]> IndexBuffers = new Dictionary<int, byte[]>();  // layer>0 → tri*3 u16
        public byte[] BaseIndexBuffer;    // layer-0 triangles
        public byte[] BaseBlend;          // 64 B
    }

    public struct XtvObject
    {
        public int MeshIndex;             // into XtvTerrain.MeshNames
        public Matrix4x4 Matrix;
    }

    public sealed class XtvFunctionalObject
    {
        public string Name = "";
        public int UniqueId;
        public int ObjectType;
        public int WideViewRaw;           // boolean lives in the LOW 16 bits only
        public int Relocate;
        public float[] Camera;            // 4 floats, only when wide-view
        public string SysMessage, Description;
        public int MeshIndex;
        public Matrix4x4 Matrix;
        public List<XtvTrigger> Triggers; // v0.91 only
        public bool WideView => (WideViewRaw & 0xFFFF) != 0;
    }

    public sealed class XtvTrigger
    {
        public int Id;
        public string Name = "";
        public int Enable;
        public byte CompareClass;
        public int Position;
        public List<byte[]> Conditions = new List<byte[]>();      // 96 B raw each
        public List<XtvTriggerAction> Actions = new List<XtvTriggerAction>();
        public List<XtvTriggerAction> ExceptedActions = new List<XtvTriggerAction>();
    }

    public sealed class XtvTriggerAction
    {
        public byte[] Head;               // 100 B raw (_XTRIGGERDATA_ACTION + id + name)
        public string Message = "";
    }

    public sealed class XtvSpeedTree
    {
        public string Name = "";          // .spt file in MR_STMODEL.XP
        public Vector3[] Positions = Array.Empty<Vector3>();
    }

    public sealed class XtvWater
    {
        public int Count;
        public byte[] Props;              // 4 B per surface
        public byte[] Heights;            // 28 B per surface
        public string[] TextureNames = Array.Empty<string>();
    }
}
