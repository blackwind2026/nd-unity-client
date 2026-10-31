using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json.Linq;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// X3dModel (M6 core decoder) → UnityEngine.Mesh.
    ///
    /// For STATIC world meshes the client draws every node's vertex buffer under the object's
    /// single placement matrix (node matrices only matter for the animated variants), so a
    /// static model becomes ONE mesh whose vertices are used exactly as stored, with one
    /// submesh per distinct texture. Winding is kept as-is: D3D and Unity are both left-handed
    /// with clockwise front faces (and the object shader culls off anyway).
    /// </summary>
    public static class MeshFactory
    {
        public sealed class StaticModel
        {
            public Mesh Mesh;
            public string[] SubmeshTextures;   // texture name per submesh
            public int VertexCount;
        }

        public static StaticModel BuildStatic(X3dModel model, string name)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            // texture name → triangle index list
            var subs = new Dictionary<string, List<int>>();

            foreach (var node in model.Nodes)
            {
                if (node.IsBiped || node.VertexCount == 0 || node.Indices == null) continue;
                int baseIndex = verts.Count;
                foreach (var v in node.Vertices)
                {
                    verts.Add(new Vector3(v.Position.X, v.Position.Y, v.Position.Z));
                    normals.Add(new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z));
                    uvs.Add(new Vector2(v.Uv.X, v.Uv.Y));
                }
                string tex = node.TextureName ?? "";
                if (!subs.TryGetValue(tex, out var tris))
                    subs[tex] = tris = new List<int>();
                for (int i = 0; i + 2 < node.Indices.Length; i += 3)
                {
                    tris.Add(baseIndex + node.Indices[i]);
                    tris.Add(baseIndex + node.Indices[i + 1]);
                    tris.Add(baseIndex + node.Indices[i + 2]);
                }
            }

            var mesh = new Mesh { name = name };
            if (verts.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            var keys = subs.Keys.ToArray();
            mesh.subMeshCount = keys.Length;
            for (int s = 0; s < keys.Length; s++)
                mesh.SetTriangles(subs[keys[s]], s, false);
            mesh.RecalculateBounds();

            return new StaticModel { Mesh = mesh, SubmeshTextures = keys, VertexCount = verts.Count };
        }

        /// <summary>Build a StaticModel from a converted .glb (Blender-editable). Mesh only (no skin);
        /// one submesh per material, SubmeshTextures = material names (= texture names, so callers'
        /// GetTexture/GetGrassMaterial work unchanged). glTF is right-handed (exporter negated Z +
        /// reversed winding) so Z is negated back and winding un-reversed into the client's D3D/Unity
        /// left-handed space. Used by the grass path and any GetStaticModel caller. Null if not a glb.</summary>
        public static StaticModel BuildStaticGlb(byte[] glb, string name)
        {
            try
            {
                var br = new BinaryReader(new MemoryStream(glb));
                if (br.ReadUInt32() != 0x46546C67u) return null;
                br.ReadUInt32(); br.ReadUInt32();
                uint jlen = br.ReadUInt32(); br.ReadUInt32();
                byte[] jb = br.ReadBytes((int)jlen);
                br.ReadUInt32(); br.ReadUInt32();
                byte[] bin = br.ReadBytes((int)(glb.Length - br.BaseStream.Position));
                var root = JObject.Parse(Encoding.UTF8.GetString(jb));
                var accessors = (JArray)root["accessors"]; var views = (JArray)root["bufferViews"];
                var materials = root["materials"] as JArray; var meshes = (JArray)root["meshes"];
                (int off, int len) View(int bv) { var v = (JObject)views[bv]; return ((int?)v["byteOffset"] ?? 0, (int)v["byteLength"]); }
                float[] F(int acc, int comp) { var a = (JObject)accessors[acc]; var (vo, _) = View((int)a["bufferView"]);
                    int st = vo + ((int?)a["byteOffset"] ?? 0), c = (int)a["count"]; var r = new float[c * comp];
                    for (int i = 0; i < r.Length; i++) r[i] = BitConverter.ToSingle(bin, st + i * 4); return r; }
                int[] IdxA(int acc) { var a = (JObject)accessors[acc]; var (vo, _) = View((int)a["bufferView"]);
                    int st = vo + ((int?)a["byteOffset"] ?? 0), c = (int)a["count"], ct = (int)a["componentType"]; var r = new int[c];
                    for (int i = 0; i < c; i++) r[i] = ct == 5123 ? BitConverter.ToUInt16(bin, st + i * 2) : ct == 5125 ? (int)BitConverter.ToUInt32(bin, st + i * 4) : bin[st + i]; return r; }

                var verts = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>();
                var subs = new Dictionary<string, List<int>>();
                foreach (JObject prim in (JArray)meshes[0]["primitives"])
                {
                    var attr = (JObject)prim["attributes"];
                    var pos = F((int)attr["POSITION"], 3);
                    var nor = attr["NORMAL"] != null ? F((int)attr["NORMAL"], 3) : null;
                    var uv = attr["TEXCOORD_0"] != null ? F((int)attr["TEXCOORD_0"], 2) : null;
                    var idx = IdxA((int)prim["indices"]);
                    int vc = pos.Length / 3, baseIndex = verts.Count;
                    for (int i = 0; i < vc; i++)
                    {
                        verts.Add(new Vector3(pos[i * 3], pos[i * 3 + 1], -pos[i * 3 + 2]));
                        normals.Add(nor != null ? new Vector3(nor[i * 3], nor[i * 3 + 1], -nor[i * 3 + 2]) : Vector3.up);
                        uvs.Add(uv != null ? new Vector2(uv[i * 2], uv[i * 2 + 1]) : Vector2.zero);
                    }
                    string tex = "";
                    if (prim["material"] != null && materials != null)
                    { int mi = (int)prim["material"]; if (mi >= 0 && mi < materials.Count) tex = (string)materials[mi]["name"] ?? ""; }
                    if (!subs.TryGetValue(tex, out var tris)) subs[tex] = tris = new List<int>();
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    { tris.Add(baseIndex + idx[i]); tris.Add(baseIndex + idx[i + 2]); tris.Add(baseIndex + idx[i + 1]); }
                }
                if (verts.Count == 0) return null;
                var mesh = new Mesh { name = name };
                if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(verts); mesh.SetNormals(normals); mesh.SetUVs(0, uvs);
                var keys = subs.Keys.ToArray();
                mesh.subMeshCount = keys.Length;
                for (int s = 0; s < keys.Length; s++) mesh.SetTriangles(subs[keys[s]], s, false);
                mesh.RecalculateBounds();
                return new StaticModel { Mesh = mesh, SubmeshTextures = keys, VertexCount = verts.Count };
            }
            catch { return null; }
        }
    }
}
