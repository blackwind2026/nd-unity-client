using UnityEngine;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// Ground grass using the client's real grass models (GrassModel.XMS) placed EXACTLY where the
    /// map author painted them: each grass tile carries a 64-byte (8x8) attribute array (the .xtv
    /// GrassBlends = client m_pGrassAttribArray) where a non-zero byte is the 1-based grass TYPE at
    /// that sub-cell. This reproduces the correct species per map and keeps grass out of buildings
    /// (unpainted cells stay empty). Swayed by NdGrassModel, GPU-instanced.
    /// </summary>
    public static class GrassFactory
    {
        public static int Build(XtvTerrain t, AssetService assets, Transform parent, int maxGrass = 14000)
        {
            if (t.GrassFlags == null || t.GrassBlends == null || assets == null) return 0;
            var names = assets.GetGrassModelNames();
            if (names == null || names.Length == 0) return 0;

            // shared models + wind materials per grass type (XMS order == attrib index)
            var models = new MeshFactory.StaticModel[names.Length];
            var mats = new Material[names.Length][];
            for (int i = 0; i < names.Length; i++)
            {
                var sm = assets.GetStaticModel(names[i]);
                if (sm == null || sm.Mesh == null || sm.Mesh.vertexCount == 0) continue;
                BakeHeightColor(sm.Mesh);   // color.g = normalized height -> shader roots the lower stalk
                models[i] = sm;
                var mm = new Material[sm.SubmeshTextures.Length];
                for (int s = 0; s < mm.Length; s++) mm[s] = assets.GetGrassMaterial(sm.SubmeshTextures[s]);
                mats[i] = mm;
            }

            int tc = t.TileCount;
            float g = t.GridSize;

            // count painted cells so we can stride down to the cap while staying spread out
            int total = 0;
            foreach (var kv in t.GrassBlends)
            {
                var a = kv.Value; if (a == null) continue;
                for (int s = 0; s < a.Length; s++) if (a[s] != 0) total++;
            }
            if (total == 0) return 0;
            int stride = Mathf.Max(1, Mathf.CeilToInt((float)total / maxGrass));

            var root = new GameObject("Grass").transform;
            root.SetParent(parent, false);
            var rnd = new System.Random(12345);
            int made = 0, seen = 0;

            foreach (var kv in t.GrassBlends)
            {
                var attrib = kv.Value;
                if (attrib == null) continue;
                int ti = kv.Key;
                // Tiles are X-major in the file (native loop `for i(=X) for j(=Z)`): ti/tc is the
                // X tile index, ti%tc the Z tile index (XLodTerrain.cpp:1375-1472). Heights are a
                // separate Z-major flat grid, so tying baseX to ti%tc would transpose the grass.
                int xT = ti / tc, zT = ti % tc;
                float baseX = t.WorldCoord(xT * 8), baseZ = t.WorldCoord(zT * 8);
                for (int sub = 0; sub < attrib.Length; sub++)
                {
                    byte tt = attrib[sub];
                    if (tt == 0) continue;
                    if ((seen++ % stride) != 0) continue;                 // thin evenly to the cap
                    int typeIdx = Mathf.Clamp(tt - 1, 0, names.Length - 1);
                    if (models[typeIdx] == null) continue;

                    // Within-tile sub-cell is X-major: native places grass at
                    // X = seedpos/8, Z = seedpos%8 (XLodTerrain.cpp:3760-3761).
                    int sx = sub / 8, sy = sub % 8;
                    float jx = (float)(rnd.NextDouble() - 0.5) * g * 0.7f;
                    float jz = (float)(rnd.NextDouble() - 0.5) * g * 0.7f;
                    float x = baseX + (sx + 0.5f) * g + jx;
                    float z = baseZ + (sy + 0.5f) * g + jz;
                    float y = TerrainFactory.HeightAt(t, x, z);

                    var go = new GameObject("grass");
                    go.transform.SetParent(root, false);
                    go.transform.localPosition = new Vector3(x, y, z);
                    go.transform.localRotation = Quaternion.Euler(0, (float)(rnd.NextDouble() * 360.0), 0);
                    float s = 0.85f + (float)rnd.NextDouble() * 0.4f;
                    go.transform.localScale = new Vector3(s, s, s);
                    go.AddComponent<MeshFilter>().sharedMesh = models[typeIdx].Mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterials = mats[typeIdx];
                    made++;
                    if (made >= maxGrass) return made;
                }
            }
            return made;
        }

        // Store normalized height (0 at the lowest vertex .. 1 at the top) in vertex color.g so the
        // NdGrassModel shader can strongly root the lower stalk and wave only the tips.
        private static void BakeHeightColor(Mesh mesh)
        {
            var existing = mesh.colors;
            if (existing != null && existing.Length == mesh.vertexCount && mesh.vertexCount > 0 && existing[0].g != 0f)
                return;   // already baked
            var v = mesh.vertices;
            float miny = float.MaxValue, maxy = float.MinValue;
            for (int i = 0; i < v.Length; i++) { if (v[i].y < miny) miny = v[i].y; if (v[i].y > maxy) maxy = v[i].y; }
            float inv = 1f / Mathf.Max(0.01f, maxy - miny);
            var cols = new Color[v.Length];
            for (int i = 0; i < v.Length; i++) cols[i] = new Color(0f, Mathf.Clamp01((v[i].y - miny) * inv), 0f, 1f);
            mesh.colors = cols;
        }
    }
}
