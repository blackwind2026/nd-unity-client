using System;
using UnityEngine;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// Non-terrain scenery from the .xtv: water surfaces, SpeedTree instances (proxy geometry,
    /// since .spt is proprietary), and a sky. Water layout matches the client / blender plugin:
    /// per terrain tile a 4-byte property (bit 6 = enabled) + 28-byte record whose first float
    /// is the surface height. SpeedTree positions are game (x, y-up, z) — Unity is Y-up so they
    /// map 1:1.
    /// </summary>
    public static class EnvironmentFactory
    {
        private const int TileVerts = 9;   // 8 quads per tile edge

        public static void BuildWater(XtvTerrain t, Transform parent)
        {
            if (t.Water == null || t.Water.Count < t.TileCount * t.TileCount) return;
            int tc = t.TileCount;
            float step = t.GridSize * (TileVerts - 1);
            var mat = WaterMaterial();

            var verts = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            var uvs = new System.Collections.Generic.List<Vector2>();
            for (int i = 0; i < tc * tc; i++)
            {
                uint prop = BitConverter.ToUInt32(t.Water.Props, i * 4);
                if (((prop >> 6) & 1) == 0) continue;
                float h = BitConverter.ToSingle(t.Water.Heights, i * 28);
                int row = i / tc, col = i % tc;
                float x0 = t.WorldCoord(col * (TileVerts - 1));
                float z0 = t.WorldCoord(row * (TileVerts - 1));
                float x1 = x0 + step, z1 = z0 + step;
                int b = verts.Count;
                verts.Add(new Vector3(x0, h, z0)); verts.Add(new Vector3(x1, h, z0));
                verts.Add(new Vector3(x1, h, z1)); verts.Add(new Vector3(x0, h, z1));
                uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0));
                uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
            }
            if (verts.Count == 0) return;

            var mesh = new Mesh { name = "water" };
            if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            var go = new GameObject("Water");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        public static int BuildTrees(XtvTerrain t, AssetService assets, Transform parent)
        {
            if (t.SpeedTrees == null || t.SpeedTrees.Count == 0) return 0;
            var root = new GameObject("Trees").transform;
            root.SetParent(parent, false);
            var rnd = new System.Random(9271);
            int made = 0;
            // Real SpeedTree geometry, baked offline by the actual SDK (NdTreeModel/TreeFactory) and
            // scaled to the client's per-species height (speedtreelist <_TREESCALE_>). Species we
            // could not bake fall back to the old crossed-quad impostor.
            foreach (var st in t.SpeedTrees)
            {
                if (st.Positions == null || st.Positions.Length == 0) continue;

                bool real = TreeFactory.Has(assets, st.Name);
                float sdkScale = 1f;
                if (real)
                {
                    float natH = TreeFactory.NaturalHeight(assets, st.Name);
                    float targetH = assets.GetTreeScale(st.Name);
                    if (targetH <= 0f) targetH = 10f;                 // sane default height if list lacks it
                    sdkScale = (natH > 0.01f) ? targetH / natH : 0.2f;
                }

                Mesh impMesh = null; Material impMat = null; TreeKind kind = default;
                if (!real)
                {
                    kind = TreeKind.Classify(st.Name);
                    impMesh = ImpostorMesh(kind.Conifer);
                    impMat = FoliageMaterial(kind.Conifer, kind.Snow);
                }

                foreach (var p in st.Positions)
                {
                    float gy = TerrainFactory.HeightAt(t, p.X, p.Z);
                    float yaw = (float)(rnd.NextDouble() * 360.0);
                    if (real)
                    {
                        var go = TreeFactory.Build(assets, st.Name, root);
                        if (go == null) continue;
                        go.transform.localPosition = new Vector3(p.X, gy, p.Z);
                        go.transform.localRotation = Quaternion.Euler(0, yaw, 0);
                        float s = sdkScale * (0.92f + (float)rnd.NextDouble() * 0.16f);
                        go.transform.localScale = new Vector3(s, s, s);
                    }
                    else
                    {
                        var go = new GameObject("tree");
                        go.transform.SetParent(root, false);
                        go.transform.localPosition = new Vector3(p.X, gy, p.Z);
                        go.transform.localRotation = Quaternion.Euler(0, yaw, 0);
                        float h = kind.Height * (0.85f + (float)rnd.NextDouble() * 0.3f);
                        go.transform.localScale = new Vector3(h * 0.62f, h, h * 0.62f);
                        go.AddComponent<MeshFilter>().sharedMesh = impMesh;
                        go.AddComponent<MeshRenderer>().sharedMaterial = impMat;
                    }
                    made++;
                }
            }
            return made;
        }

        private struct TreeKind
        {
            public bool Conifer, Snow;
            public float Height;
            public static TreeKind Classify(string name)
            {
                string n = (name ?? "").ToLowerInvariant();
                bool conifer = n.Contains("pine") || n.Contains("spruce") || n.Contains("cedar") ||
                               n.Contains("juniper") || n.Contains("cypress") || n.Contains("sequoia") ||
                               n.Contains("frough") || n.Contains("fir");
                bool snow = n.Contains("snow") || n.Contains("winter");
                float h = 8.5f;
                if (n.Contains("_big")) h *= 1.5f;
                if (n.Contains("sequoia") || n.Contains("scarytree") || n.Contains("baobab")) h *= 1.4f;
                return new TreeKind { Conifer = conifer, Snow = snow, Height = h };
            }
        }

        /// <summary>Sky: gradient dome + tinted ambient. Removes the flat gray void.</summary>
        public static void BuildSky(Camera cam)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.92f);   // hazy blue
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.85f, 0.9f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.7f, 0.68f, 0.6f);
            RenderSettings.ambientGroundColor = new Color(0.35f, 0.3f, 0.25f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.62f, 0.74f, 0.9f);
            RenderSettings.fogStartDistance = 120f;
            RenderSettings.fogEndDistance = 900f;
        }

        // --- materials & meshes ---
        private static Material WaterMaterial()
        {
            // transparent-ish blue via the object shader tint; alpha handled by a dedicated shader
            var m = new Material(NdShaders.Water);
            m.SetColor("_Tint", new Color(0.12f, 0.34f, 0.55f, 0.65f));
            return m;
        }

        // --- tree impostors: 3 crossed vertical quads (unit height, ~unit width), textured ---
        private static readonly Mesh[] _impostor = new Mesh[2];   // [0]=broadleaf, [1]=conifer
        private static Mesh ImpostorMesh(bool conifer)
        {
            int idx = conifer ? 1 : 0;
            if (_impostor[idx] != null) return _impostor[idx];
            var verts = new System.Collections.Generic.List<Vector3>();
            var uvs = new System.Collections.Generic.List<Vector2>();
            var norm = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            // conifers a touch narrower than broadleaf
            float hw = conifer ? 0.5f : 0.58f;
            for (int q = 0; q < 3; q++)
            {
                float a = q * Mathf.PI / 3f;                 // 0, 60, 120 degrees
                var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var nrm = new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a));
                int b = verts.Count;
                verts.Add(-dir * hw); verts.Add(dir * hw);
                verts.Add(dir * hw + Vector3.up); verts.Add(-dir * hw + Vector3.up);
                uvs.Add(new Vector2(0, 0)); uvs.Add(new Vector2(1, 0));
                uvs.Add(new Vector2(1, 1)); uvs.Add(new Vector2(0, 1));
                for (int k = 0; k < 4; k++) norm.Add(nrm);
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b); tris.Add(b + 3); tris.Add(b + 2);
            }
            var mesh = new Mesh { name = conifer ? "tree_conifer" : "tree_broadleaf" };
            mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetNormals(norm);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            _impostor[idx] = mesh;
            return mesh;
        }

        private static readonly System.Collections.Generic.Dictionary<int, Material> _foliageMats
            = new System.Collections.Generic.Dictionary<int, Material>();
        private static Material FoliageMaterial(bool conifer, bool snow)
        {
            int key = (conifer ? 1 : 0) | (snow ? 2 : 0);
            if (_foliageMats.TryGetValue(key, out var m)) return m;
            m = new Material(NdShaders.Foliage) { name = $"foliage_{key}", mainTexture = TreeTexture(conifer, snow) };
            m.SetColor("_Tint", Color.white);
            m.SetFloat("_Cutoff", 0.45f);
            m.SetFloat("_Sway", 0.05f);
            _foliageMats[key] = m;
            return m;
        }

        private static readonly System.Collections.Generic.Dictionary<int, Texture2D> _treeTex
            = new System.Collections.Generic.Dictionary<int, Texture2D>();
        private static Texture2D TreeTexture(bool conifer, bool snow)
        {
            int key = (conifer ? 1 : 0) | (snow ? 2 : 0);
            if (_treeTex.TryGetValue(key, out var cached)) return cached;
            const int W = 96, H = 128;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true)
            { name = $"tree_{key}", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[W * H];
            var trunkCol = new Color(0.34f, 0.24f, 0.15f);
            for (int y = 0; y < H; y++)
            {
                float v = (float)y / (H - 1);                // 0 bottom .. 1 top
                for (int x = 0; x < W; x++)
                {
                    float u = (float)x / (W - 1);
                    float dx = Mathf.Abs(u - 0.5f);
                    Color col; float a = 0f;

                    // trunk
                    float trunkTop = conifer ? 0.16f : 0.34f;
                    if (v < trunkTop && dx < 0.045f + 0.02f * (1f - v)) { col = trunkCol * (0.8f + 0.2f * Hash(x, y)); a = 1f; }
                    else col = Color.black;

                    // canopy silhouette
                    float fol = 0f;
                    if (conifer)
                    {
                        float fv = (v - 0.12f) / 0.88f;      // 0 at canopy base .. 1 tip
                        if (fv >= 0 && fv <= 1f)
                        {
                            const int tiers = 5;
                            float local = (fv * tiers) % 1f;              // drooping branch tiers
                            float hw = (1f - fv) * 0.46f * (0.62f + 0.38f * local);
                            hw *= 0.9f + 0.16f * (Hash(x * 3, y) - 0.5f); // ragged edge
                            if (dx < hw) fol = 1f;
                        }
                    }
                    else
                    {
                        float cx = u - 0.5f, cy = v - 0.66f;
                        float r = (cx * cx) / (0.42f * 0.42f) + (cy * cy) / (0.34f * 0.34f);
                        r += 0.18f * (Hash(x, y * 2) - 0.5f);            // blobby edge
                        if (v > 0.28f && r < 1f) fol = 1f;
                    }

                    if (fol > 0f)
                    {
                        float shade = 0.72f + 0.28f * Hash(x * 2 + 1, y * 2 + 1);   // leaf clumps
                        float depth = 1f - Mathf.Clamp01(dx * 1.4f);               // edges lighter
                        Color green = conifer
                            ? Color.Lerp(new Color(0.10f, 0.26f, 0.12f), new Color(0.26f, 0.44f, 0.20f), 1f - depth)
                            : Color.Lerp(new Color(0.14f, 0.32f, 0.14f), new Color(0.34f, 0.52f, 0.24f), 1f - depth);
                        col = green * shade;
                        if (snow)
                        {
                            // snow settles on upper faces / tips
                            float snowAmt = Mathf.Clamp01((v - 0.35f) * 1.1f) * (0.35f + 0.5f * Hash(x + 7, y + 3));
                            if (conifer) snowAmt *= 0.6f + 0.4f * (1f - ((v * 5f) % 1f)); // tier tops
                            col = Color.Lerp(col, new Color(0.92f, 0.94f, 0.97f), Mathf.Clamp01(snowAmt));
                        }
                        a = 1f;
                    }
                    px[y * W + x] = new Color32((byte)(col.r * 255), (byte)(col.g * 255), (byte)(col.b * 255), (byte)(a * 255));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true);
            _treeTex[key] = tex;
            return tex;
        }

        private static float Hash(int x, int y)
        {
            int h = x * 374761393 + y * 668265263;
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0x7fffffff) / (float)0x7fffffff;
        }
    }
}
