using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// A character bone hierarchy. Two sources feed the SAME core builder:
    ///  * <see cref="Build"/> — an .xbp skeleton (MBiped.XBP / npc_*.xbp): each XBP node is a bone
    ///    whose Matrix is its bind world TM (D3D).
    ///  * <see cref="BuildFromGlb"/> — the armature baked into a converted / Blender-authored .glb
    ///    (skins[0]): the bone's bind world is recovered from its inverseBindMatrix and mapped D3D↔RH
    ///    with the SAME F=diag(1,1,-1) the converter used, so the resulting skeleton is identical in
    ///    construction to the .xbp one and the animator's CONJUGATE-quaternion convention still holds.
    ///    This is the "complete model built in Blender, no .xbp needed" path.
    /// Each bone is one Transform placed at its bind world pose, parented by ParentId; body-part skins
    /// reference bones by index (JOINTS_0 = joint order = bone index), so <see cref="Bones"/> is indexed
    /// directly by that id.
    /// </summary>
    public sealed class Skeleton
    {
        public readonly Transform Root;          // skeleton container (== root bone's parent)
        public readonly Transform[] Bones;       // indexed by bone node id
        public readonly Transform BipRoot;       // the root bone (Bip01)
        public readonly Dictionary<string, int> ByName;   // bone name → id
        private readonly Matrix4x4[] _bindLocalTRS;
        private Matrix4x4[] _bindW2L;                      // cached bind-pose world→local (pose-independent)

        /// <summary>Bind-pose world→local matrix per bone, CAPTURED at the first call (spawn, when the skeleton
        /// is still at bind/T-pose) and reused thereafter. Skinning a part added at RUNTIME (a live weapon equip)
        /// must use THESE, not the live worldToLocalMatrix — otherwise the mesh binds against the current
        /// ANIMATED pose and renders collapsed/off-screen even though the renderer exists. Body parts added at
        /// spawn get the same (correct) matrices, so they are unaffected.</summary>
        public Matrix4x4[] BindWorldToLocal()
        {
            if (_bindW2L != null) return _bindW2L;
            int n = Bones.Length;
            var w2l = new Matrix4x4[n];
            for (int b = 0; b < n; b++)
                w2l[b] = Bones[b] != null ? Bones[b].worldToLocalMatrix : Matrix4x4.identity;
            _bindW2L = w2l;   // first call is at spawn (bind pose) → this snapshot IS the bind pose
            return _bindW2L;
        }

        // LH<->RH mirror (D3D <-> glTF): negate Z. Identical to the converter's F.
        private static readonly Matrix4x4 Fmirror = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));

        private Skeleton(Transform root, Transform[] bones, Transform bip, Matrix4x4[] bindLocal,
                         Dictionary<string, int> byName)
        { Root = root; Bones = bones; BipRoot = bip; _bindLocalTRS = bindLocal; ByName = byName; }

        public static Skeleton Build(X3dModel xbp, string name)
        {
            var nodes = xbp.Nodes;
            return BuildCore(name, nodes.Count,
                i => nodes[i].Name,
                i => nodes[i].ParentId,
                i => UnityConvert.ToUnity(nodes[i].Matrix));
        }

        /// <summary>Build the skeleton from a .glb's own armature (skins[0]) — no .xbp needed. Returns
        /// null if the glb has no skin. Bone k corresponds to skins[0].joints[k], so a JOINTS_0 index
        /// (0..J-1) maps straight to <see cref="Bones"/>[k], exactly like the .xbp path.</summary>
        public static Skeleton BuildFromGlb(byte[] glb, string name)
        {
            if (glb == null || glb.Length < 20) return null;
            try
            {
                var br = new BinaryReader(new MemoryStream(glb));
                if (br.ReadUInt32() != 0x46546C67u) return null;              // "glTF"
                br.ReadUInt32(); br.ReadUInt32();                            // version, length
                uint jlen = br.ReadUInt32(); br.ReadUInt32();                // JSON chunk len + type
                byte[] jb = br.ReadBytes((int)jlen);
                br.ReadUInt32(); br.ReadUInt32();                           // BIN chunk len + type
                byte[] bin = br.ReadBytes((int)(glb.Length - br.BaseStream.Position));
                var root = JObject.Parse(Encoding.UTF8.GetString(jb));

                var skins = root["skins"] as JArray;
                if (skins == null || skins.Count == 0) return null;
                var skin0 = (JObject)skins[0];
                var jointsArr = (JArray)skin0["joints"];
                int J = jointsArr.Count;
                if (J == 0) return null;
                var nodes = (JArray)root["nodes"];
                var accessors = (JArray)root["accessors"];
                var views = (JArray)root["bufferViews"];

                (int off, int len) View(int bv) { var v = (JObject)views[bv]; return ((int?)v["byteOffset"] ?? 0, (int)v["byteLength"]); }
                // read a MAT4 accessor (count × 16 floats, row-major listing M11..M44 as the converter wrote)
                float[] Mat4Acc(int acc)
                {
                    var a = (JObject)accessors[acc]; var (vo, _) = View((int)a["bufferView"]);
                    int st = vo + ((int?)a["byteOffset"] ?? 0), c = (int)a["count"]; var r = new float[c * 16];
                    for (int i = 0; i < r.Length; i++) r[i] = BitConverter.ToSingle(bin, st + i * 4);
                    return r;
                }

                var jointNode = new int[J];                     // bone k -> glTF node index
                var nodeToJoint = new Dictionary<int, int>();
                for (int k = 0; k < J; k++) { jointNode[k] = (int)jointsArr[k]; nodeToJoint[jointNode[k]] = k; }

                // parent per bone, from the node "children" hierarchy
                var parent = new int[J];
                for (int k = 0; k < J; k++) parent[k] = -1;
                for (int k = 0; k < J; k++)
                {
                    var ch = ((JObject)nodes[jointNode[k]])["children"] as JArray;
                    if (ch == null) continue;
                    foreach (var c in ch)
                        if (nodeToJoint.TryGetValue((int)c, out int ck)) parent[ck] = k;
                }

                var names = new string[J];
                for (int k = 0; k < J; k++) names[k] = (string)((JObject)nodes[jointNode[k]])["name"] ?? ("bone" + k);

                // bind world per bone: Wrh = inv(IBM); Wd3d(Unity) = F * Wrh_unity * F.
                float[] ibm = Mat4Acc((int)skin0["inverseBindMatrices"]);
                var world = new Matrix4x4[J];
                for (int k = 0; k < J; k++)
                {
                    var ibmU = new Matrix4x4();                 // ToUnity(Ibm): row j of the stored matrix -> Unity column j
                    ibmU.SetColumn(0, new Vector4(ibm[k * 16 + 0], ibm[k * 16 + 1], ibm[k * 16 + 2], ibm[k * 16 + 3]));
                    ibmU.SetColumn(1, new Vector4(ibm[k * 16 + 4], ibm[k * 16 + 5], ibm[k * 16 + 6], ibm[k * 16 + 7]));
                    ibmU.SetColumn(2, new Vector4(ibm[k * 16 + 8], ibm[k * 16 + 9], ibm[k * 16 + 10], ibm[k * 16 + 11]));
                    ibmU.SetColumn(3, new Vector4(ibm[k * 16 + 12], ibm[k * 16 + 13], ibm[k * 16 + 14], ibm[k * 16 + 15]));
                    world[k] = Fmirror * ibmU.inverse * Fmirror;
                }

                return BuildCore(name, J, k => names[k], k => parent[k], k => world[k]);
            }
            catch (Exception e) { Debug.LogWarning($"[GLTF-SKEL] BuildFromGlb '{name}': {e.Message}"); return null; }
        }

        // Shared construction: create bones, parent by ParentId, place each at its bind WORLD pose
        // (scale stripped from the basis exactly like the client _XMeshMath_UnitMatrixFromMatrix so
        // Unity's rotation extraction stays valid), then record bind-local TRS + name index.
        private static Skeleton BuildCore(string name, int n, Func<int, string> nameOf,
                                          Func<int, int> parentOf, Func<int, Matrix4x4> worldOf)
        {
            var root = new GameObject("skeleton_" + name).transform;
            var bones = new Transform[n];
            for (int i = 0; i < n; i++)
                bones[i] = new GameObject($"{i}:{nameOf(i)}").transform;
            for (int i = 0; i < n; i++)
            {
                int p = parentOf(i);
                bones[i].SetParent((p >= 0 && p < n) ? bones[p] : root, false);
            }

            // Place worlds parent-first (a child's local is derived from the parent's already-set world).
            // For .xbp this is plain index order; a Blender armature may list joints in any order.
            foreach (int i in TopoOrder(n, parentOf))
            {
                var m = worldOf(i);
                bones[i].position = m.GetColumn(3);
                Vector3 c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2);
                if (LogScale && (i == 13 || i == 14 || i == 15 || i == 32 || i == 33 || i == 3))
                    Debug.Log($"[SCALE] bone {i} {nameOf(i)}: |c0|={c0.magnitude:0.000} |c1|={c1.magnitude:0.000} |c2|={c2.magnitude:0.000}");
                if (c0.sqrMagnitude > 1e-12f && c1.sqrMagnitude > 1e-12f && c2.sqrMagnitude > 1e-12f)
                {
                    var mn = Matrix4x4.identity;
                    mn.SetColumn(0, c0.normalized);
                    mn.SetColumn(1, c1.normalized);
                    mn.SetColumn(2, c2.normalized);
                    bones[i].rotation = mn.rotation;
                }
                else bones[i].rotation = m.rotation;
            }

            var bindLocal = new Matrix4x4[n];
            var byName = new Dictionary<string, int>();
            int bip = 0;
            for (int i = 0; i < n; i++)
            {
                bindLocal[i] = Matrix4x4.TRS(bones[i].localPosition, bones[i].localRotation, bones[i].localScale);
                var nm = nameOf(i);
                if (!string.IsNullOrEmpty(nm) && !byName.ContainsKey(nm)) byName[nm] = i;
                if (parentOf(i) < 0 && bip == 0) bip = i;
            }
            return new Skeleton(root, bones, n > 0 ? bones[bip] : root, bindLocal, byName);
        }

        // parents before children; tolerant of arbitrary joint order and of broken parent links.
        private static int[] TopoOrder(int n, Func<int, int> parentOf)
        {
            var order = new int[n]; var placed = new bool[n]; int w = 0;
            while (w < n)
            {
                int before = w;
                for (int i = 0; i < n; i++)
                {
                    if (placed[i]) continue;
                    int p = parentOf(i);
                    if (p < 0 || p >= n || placed[p]) { order[w++] = i; placed[i] = true; }
                }
                if (w == before)   // cycle / bad link: place the rest as-is
                    for (int i = 0; i < n; i++) if (!placed[i]) { order[w++] = i; placed[i] = true; }
            }
            return order;
        }

        public static bool LogScale;
        public Vector3 BindLocalPos(int id) => _bindLocalTRS[id].GetColumn(3);
        public Quaternion BindLocalRot(int id) => _bindLocalTRS[id].rotation;
    }
}
