using System.Collections.Generic;
using UnityEngine;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// XtvTerrain (M6 core decoder) → chunked terrain meshes.
    ///
    /// The .xtv stores a V×V height grid (V = tileCount·8 + 1), a V×V baked ARGB diffuse map
    /// (the client's lighting lives in it) and up to 10 splat layers. Phase 1 renders
    /// height + vertex color + the base detail texture tiled — visually close to the client
    /// without the splat blend passes (TODO noted in SPEC).
    ///
    /// Grid convention (client `world_x/world_z`): heights[i·V + j] sits at
    /// x = -width/2 + grid·j, z = -width/2 + grid·i.
    /// </summary>
    public static class TerrainFactory
    {
        private const int ChunkQuads = 32;    // 33×33 verts per chunk

        public static GameObject Build(XtvTerrain t, Material material, Transform parent,
                                       bool withColliders = false, bool planarUv = false)
        {
            var root = new GameObject("Terrain");
            root.transform.SetParent(parent, false);

            int v = t.VertexPerSide;
            int chunks = (v - 1 + ChunkQuads - 1) / ChunkQuads;
            for (int ci = 0; ci < chunks; ci++)
                for (int cj = 0; cj < chunks; cj++)
                    BuildChunk(t, v, ci, cj, material, root.transform, withColliders, planarUv);
            return root;
        }

        private static void BuildChunk(XtvTerrain t, int v, int ci, int cj, Material material,
                                       Transform parent, bool withCollider, bool planarUv)
        {
            int i0 = ci * ChunkQuads, j0 = cj * ChunkQuads;
            int ni = Mathf.Min(ChunkQuads, v - 1 - i0) + 1;   // verts along z
            int nj = Mathf.Min(ChunkQuads, v - 1 - j0) + 1;   // verts along x
            if (ni < 2 || nj < 2) return;

            var verts = new Vector3[ni * nj];
            var colors = new Color32[ni * nj];
            var uvs = new Vector2[ni * nj];
            // tile the detail texture once per terrain tile (8 grid cells) — close to client look
            float uvScale = 1f / (8f * t.GridSize);

            for (int i = 0; i < ni; i++)
                for (int j = 0; j < nj; j++)
                {
                    int gi = i0 + i, gj = j0 + j;
                    float x = t.WorldCoord(gj), z = t.WorldCoord(gi);
                    float y = t.Heights[gi * v + gj];
                    int idx = i * nj + j;
                    verts[idx] = new Vector3(x, y, z);
                    uint argb = t.DiffuseArgb[gi * v + gj];
                    colors[idx] = new Color32((byte)(argb >> 16), (byte)(argb >> 8),
                                              (byte)argb, (byte)(argb >> 24));
                    // planar full-map UV for the composed splat-cache ground, else tiled detail
                    uvs[idx] = planarUv
                        ? new Vector2((float)gj / (v - 1), (float)gi / (v - 1))
                        : new Vector2(x * uvScale, z * uvScale);
                }

            var tris = new List<int>((ni - 1) * (nj - 1) * 6);
            for (int i = 0; i < ni - 1; i++)
                for (int j = 0; j < nj - 1; j++)
                {
                    int a = i * nj + j, b = a + 1, c = a + nj, d = c + 1;
                    // clockwise when seen from +Y (left-handed front face)
                    tris.Add(a); tris.Add(d); tris.Add(c);
                    tris.Add(a); tris.Add(b); tris.Add(d);
                }

            var mesh = new Mesh { name = $"terrain_{ci}_{cj}" };
            mesh.vertices = verts;
            mesh.colors32 = colors;
            mesh.uv = uvs;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(mesh.name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            if (withCollider)
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        /// <summary>Sample terrain height at world (x, z) — bilinear over the grid.</summary>
        public static float HeightAt(XtvTerrain t, float x, float z)
        {
            int v = t.VertexPerSide;
            float fj = (x + t.Width * 0.5f) / t.GridSize;
            float fi = (z + t.Width * 0.5f) / t.GridSize;
            int j = Mathf.Clamp((int)fj, 0, v - 2);
            int i = Mathf.Clamp((int)fi, 0, v - 2);
            float tx = Mathf.Clamp01(fj - j), tz = Mathf.Clamp01(fi - i);
            float h00 = t.Heights[i * v + j], h01 = t.Heights[i * v + j + 1];
            float h10 = t.Heights[(i + 1) * v + j], h11 = t.Heights[(i + 1) * v + j + 1];
            return Mathf.Lerp(Mathf.Lerp(h00, h01, tx), Mathf.Lerp(h10, h11, tx), tz);
        }
    }
}
