using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// Builds a real skinned, animatable character: an .xbp skeleton (<see cref="Skeleton"/>)
    /// plus body-part X3D meshes bound to it as SkinnedMeshRenderers. Vertices are stored in
    /// bind/model space and the X3D skin gives per-vertex bone influences (factor.BoneId is a
    /// skeleton bone node id), so bindposes come straight from the bones' bind transforms.
    /// </summary>
    public static class SkinnedCharacterFactory
    {
        public static GameObject BuildPlayer(AssetService assets, int sex, int faceId, int hairId, string name,
                                             out Skeleton skeleton, int amor = 0, int pants = 0, int cap = 0,
                                             int weapon = 0, int weaponModelIndex = 0, int shoes = 0, int maskId = 0)
        {
            skeleton = null;
            string g = sex == 0 ? "m" : "w";
            string xbpName = sex == 0 ? "MBiped.XBP" : "WBiped.XBP";

            Skeleton.LogScale = LogParts;
            var xbp = assets.GetRawModel(xbpName);
            if (xbp == null) return null;                 // no skeleton → caller falls back
            var skel = Skeleton.Build(xbp, name);
            skeleton = skel;

            var root = new GameObject("Character_" + name);
            skel.Root.SetParent(root.transform, false);

            if (LogParts && assets.CharModels != null)
                Debug.Log($"[CLUSTERS off={(sex == 0 ? 0 : CharModelTable.GenderSeparate)}]\n" +
                          assets.CharModels.Dump(sex == 0 ? 0 : CharModelTable.GenderSeparate));
            if (LogParts)
            {
                var sbB = new System.Text.StringBuilder("[BONES] ");
                foreach (var id in new[] { 2, 12, 13, 14, 15, 32, 33, 34, 35 })
                    if (id < skel.Bones.Length)
                    {
                        var pp = skel.Bones[id].position;
                        sbB.Append($"{id}:{skel.Bones[id].name.Replace("Bip01 ", "")}@({pp.x:0.00},{pp.y:0.00},{pp.z:0.00}) ");
                    }
                Debug.Log(sbB.ToString());
            }

            int built = 0;
            // face: client resolves it via g_UserModel_FaceType[extravalue + face - 1]
            // (XBipedController.cpp:321/342). The old code always used the base a_{g}_00_a, so faceId was
            // ignored and every character wore the SAME default face. Use the FACE cluster; fall back to
            // the base face only if the cluster has no entry / fails to load.
            string faceMesh = assets.FaceClusterMesh(sex, faceId);
            bool faceOk = faceMesh != null && AddSkinnedPart(assets, root.transform, skel, faceMesh);
            if (!faceOk) faceOk = AddSkinnedPart(assets, root.transform, skel, $"a_{g}_00_a.X3D");
            if (faceOk) built++;

            // DECO / costume (ngoại trang): a worn 치장 mask (cloth second-type 8) is a full-body costume on
            // the MASK cluster that REPLACES the whole outfit — the client hides UPPER/LOWER/CAP/HAIR/feet/
            // hands/backpack and shows only FACE + the mask mesh (XUser.cpp:3672-3767). We only hide the
            // outfit when the mask mesh actually skins on, so a missing/absent costume keeps the normal body
            // intact. Weapon stays visible (mounted separately by WeaponMount).
            bool maskOk = false;
            if (maskId > 0)
            {
                string maskMesh = assets.ClothClusterMesh("MASK", sex, maskId);
                if (maskMesh != null && AddSkinnedPart(assets, root.transform, skel, maskMesh))
                { maskOk = true; built++; if (LogParts) Debug.Log($"[DECO] mask id={maskId} mesh='{maskMesh}' -> costume shown (outfit hidden)"); }
                else if (LogParts) Debug.Log($"[DECO] mask id={maskId} mesh='{maskMesh ?? "(null)"}' absent -> normal outfit");
            }
            if (maskOk)
            {
                // costume replaces the body; skip every outfit part below. Face is already built above; the
                // weapon is mounted afterwards. Done.
                if (built == 0) { UnityEngine.Object.Destroy(root); return null; }
                return root;
            }

            // left hand is always the base body
            if (AddSkinnedPart(assets, root.transform, skel, $"a_{g}_00_c1.X3D")) built++;
            // right hand (bare). The held weapon is mounted SEPARATELY by WeaponMount after the
            // body is built (client cluster _XDEF_MODELCLUSTER_ITEM1 = g_ItemModel_Weapon[gender]
            // [sModelIndex], skinned to bones bo05/bo07); keeping it out of the body assembly lets
            // the weapon + enchant aura + other-player weapons share one code path (WeaponMount).
            if (AddSkinnedPart(assets, root.transform, skel, $"a_{g}_00_c2.X3D")) built++;

            // upper body: equipped armor mesh, else naked torso
            string upper = assets.ClothClusterMesh("UPPERBODY", sex, amor) ?? $"a_{g}_00_b.X3D";
            if (AddSkinnedPart(assets, root.transform, skel, upper)) built++;
            // lower body: equipped pants mesh, else naked legs
            string lowerCloth = assets.ClothClusterMesh("LOWERBODY", sex, pants);
            AddSkinnedPart(assets, root.transform, skel, lowerCloth ?? $"a_{g}_00_d.X3D");
            // feet are a SEPARATE cluster (LFOOT/RFOOT) the client ALWAYS renders (XUser.cpp:3222-3260
            // SetMeshModel LFOOT/RFOOT = g_UserModel_L/RFootType[extravalue + shoes.sModelIndex], or
            // [extravalue] = the base foot when no shoes). The lower body does not include the feet.
            // When a shoes item is worn we resolve the LFOOT/RFOOT cluster meshes exactly as the
            // client does (a_?_NN_e1/e2.X3D, verified skinned to bones 33-35 / 37-39); else the base
            // e1/e2. Always add, or the character has no feet/lower legs.
            string lfoot = assets.ClothClusterMesh("LFOOT", sex, shoes) ?? $"a_{g}_00_e1.X3D";
            string rfoot = assets.ClothClusterMesh("RFOOT", sex, shoes) ?? $"a_{g}_00_e2.X3D";
            AddSkinnedPart(assets, root.transform, skel, lfoot);
            AddSkinnedPart(assets, root.transform, skel, rfoot);
            // cap (only when equipped)
            string capMesh = assets.ClothClusterMesh("CAP", sex, cap);
            if (capMesh != null) AddSkinnedPart(assets, root.transform, skel, capMesh);

            // hair: the client hides hair when a cap is worn (XUser.cpp SetMeshModel HAIR = NULL
            // whenever modeldescript.cap != 0); otherwise it would poke through the hat.
            if (capMesh == null)
            {
                // hair mesh from the HAIR cluster (client g_UserModel_HairType[extravalue + hair - 1]) —
                // the item's EXACT style+colour. The old a_{g}_0N_f1 name-guess clamped hair to 1..6, so
                // every hair id >= 7 (e.g. LaSatNu's hair 12 → clamped to 6) rendered the WRONG hairstyle.
                // Fall back to the guess only if the cluster has no entry for this id.
                string hairMesh = assets.HairClusterMesh(sex, hairId);
                if (hairMesh == null || !AddSkinnedPart(assets, root.transform, skel, hairMesh))
                {
                    int hv = Mathf.Clamp(hairId, 1, 6);
                    if (!AddSkinnedPart(assets, root.transform, skel, $"a_{g}_0{hv}_f1.X3D"))
                        AddSkinnedPart(assets, root.transform, skel, $"a_{g}_01_f1.X3D");
                }
            }

            if (built == 0) { UnityEngine.Object.Destroy(root); return null; }
            return root;
        }

        /// <summary>
        /// Build a skinned + idle-animated NPC/monster from a single base X3D + its .xbp biped
        /// and idle/walk .XA (NPCModel.XMS / MobModel.XMS). Returns null when there is no usable
        /// skeleton/skin, so the caller can fall back to a static bind-pose model.
        /// </summary>
        /// <summary>when true, log why an entity failed to animate (set by -ndLogAnim).</summary>
        public static bool LogAnim;

        public static GameObject BuildAnimatedEntity(AssetService assets, string baseModel, string biped,
            string idleAnim, string walkAnim, string name, string actionAnim = null, string runAnim = null)
        {
            if (string.IsNullOrEmpty(biped) || string.IsNullOrEmpty(baseModel))
            { if (LogAnim) Debug.Log($"[ANIMFAIL] {name}: biped/base empty (biped='{biped}' base='{baseModel}')"); return null; }
            var xbp = assets.GetRawModel(biped);
            if (xbp == null || xbp.Nodes.Count == 0)
            { if (LogAnim) Debug.Log($"[ANIMFAIL] {name}: biped '{biped}' null/empty"); return null; }
            var skel = Skeleton.Build(xbp, name);

            var root = new GameObject("Anim_" + name);
            skel.Root.SetParent(root.transform, false);
            if (!AddSkinnedPart(assets, root.transform, skel, baseModel))
            {
                if (LogAnim) Debug.Log($"[ANIMFAIL] {name}: base '{baseModel}' skin add failed");
                UnityEngine.Object.Destroy(root);
                return null;
            }
            var sa = root.AddComponent<SkeletalAnimator>();
            sa.Skel = skel;
            // Mobs pass a separate RUN clip → walk/run selected per frame by RunMode; NPCs/others pass
            // no run and keep the single walk clip (glTF overlay gltf_anim\<clip>.glb wins over .XA).
            if (!string.IsNullOrEmpty(runAnim)) sa.SetClips(assets, idleAnim, walkAnim, runAnim);
            else sa.SetClips(assets, idleAnim, walkAnim);
            if (!string.IsNullOrEmpty(actionAnim)) sa.SetAction(assets, actionAnim);
            if (LogAnim) Debug.Log($"[ANIMOK] {name}: idle='{idleAnim}' walk='{walkAnim}' run='{runAnim}' bones={xbp.Nodes.Count}");
            return root;
        }

        /// <summary>
        /// Build a fully SELF-CONTAINED model from a single .glb that carries its OWN armature + skin
        /// (+ optional embedded animation) — the "complete model authored in Blender, no .xbp / no .XA"
        /// path. The skeleton comes from <see cref="Skeleton.BuildFromGlb"/> (glb skins[0]); the mesh is
        /// bound via <see cref="AddSkinnedPart"/> (JOINTS_0 = joint order = bone index, so it lands on the
        /// glb's own bones); animation, if any, is the glb's animations[0]/[1] (SetSelfClips). Returns
        /// null when the glb has no skin (caller can fall back to a static / .xbp build).
        /// NOTE: materials still resolve by texture NAME from TR_*.XP (GetCharacterMaterial); a model
        /// with custom textures the packs don't have shows the missing texture until embedded-glb-texture
        /// support is added.
        /// </summary>
        public static GameObject BuildSelfContainedGlb(AssetService assets, string modelName, string name,
                                                       out Skeleton skeleton)
        {
            skeleton = null;
            byte[] glb = assets.GlbBytes(modelName);
            if (glb == null)
            { if (LogAnim) Debug.Log($"[SELFGLB] {name}: glb '{modelName}' not found"); return null; }

            var skel = Skeleton.BuildFromGlb(glb, name);
            if (skel == null)
            { if (LogAnim) Debug.Log($"[SELFGLB] {name}: '{modelName}' has no skin/armature"); return null; }

            var root = new GameObject("SelfGlb_" + name);
            skel.Root.SetParent(root.transform, false);
            if (!AddSkinnedPart(assets, root.transform, skel, modelName))
            {
                if (LogAnim) Debug.Log($"[SELFGLB] {name}: '{modelName}' skin add failed");
                UnityEngine.Object.Destroy(root);
                return null;
            }

            var sa = root.AddComponent<SkeletalAnimator>();
            sa.Skel = skel;
            sa.SetSelfClips(glb);   // animation baked into the model glb itself (Blender actions)
            skeleton = skel;
            if (LogAnim) Debug.Log($"[SELFGLB] {name}: '{modelName}' bones={skel.Bones.Length} (self-contained glb)");
            return root;
        }

        /// <summary>when true, log each part's load/skin result (set by -ndLogParts).</summary>
        public static bool LogParts;
        /// <summary>when true, render parts as plain (non-skinned) meshes at raw vertex positions
        /// (set by -ndNoSkin) — isolates the raw model data from the skinning setup.</summary>
        public static bool NoSkin;

        /// <summary>Load an X3D body/equipment part and bind it to the skeleton as a
        /// SkinnedMeshRenderer (verts are model/bind space, skin factors give the bone ids). Public
        /// so WeaponMount can mount the held weapon on the same shared skeleton. <paramref name="skipBones"/>
        /// (optional) drops any geometry node whose skin uses ONLY those bones — WeaponMount passes the
        /// auxiliary weapon bones (bo07/bo08) so a sword's opposite-hip sheath node is not rendered,
        /// leaving just the held blade. Returns false when the file is missing or carries no skin.</summary>
        public static bool AddSkinnedPart(AssetService assets, Transform parent, Skeleton skel, string file,
                                          HashSet<int> skipBones = null)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var weights = new List<BoneWeight>();
            var subs = new Dictionary<string, List<int>>();
            var embTex = new Dictionary<string, Texture2D>();   // material name -> embedded glb texture (Blender models)
            bool anySkin = false;
            X3dModel model = null;

            // glTF overlay: a converted Data\Model\gltf\<base>.glb provides the MESH (Blender-editable),
            // bound to the SAME .xbp skeleton (JOINTS_0 = skel bone index). Weapons work too: skipBones
            // (the sheath's aux bones) is honoured per-primitive inside TryLoadGlbSkin.
            byte[] glbBytes = assets.GlbBytes(file);
            bool fromGlb = glbBytes != null &&
                           TryLoadGlbSkin(glbBytes, skel, verts, normals, uvs, weights, subs, ref anySkin, skipBones, embTex);
            if (fromGlb)
            {
                if (LogParts) Debug.Log($"[GLTF-SKIN] {file} (glb) verts={verts.Count} subs={subs.Count}");
            }
            else
            {
                model = assets.GetRawModel(file);
                if (model == null)
                {
                    if (LogParts) Debug.Log($"[PART] {file}: MODEL NULL (not found)");
                    return false;
                }

                foreach (var node in model.Nodes)
                {
                if (node.IsBiped || node.Vertices == null || node.Vertices.Length == 0) continue;
                // drop a node bound ONLY to skipped (auxiliary) bones — e.g. the weapon's sheath on bo07
                if (skipBones != null && NodeOnlyUses(node, skipBones))
                {
                    if (LogParts) Debug.Log($"[PART] {file}: node '{node.Name}' skipped (auxiliary weapon bone only)");
                    continue;
                }
                int baseIndex = verts.Count;
                for (int v = 0; v < node.Vertices.Length; v++)
                {
                    var vx = node.Vertices[v];
                    verts.Add(new Vector3(vx.Position.X, vx.Position.Y, vx.Position.Z));
                    normals.Add(new Vector3(vx.Normal.X, vx.Normal.Y, vx.Normal.Z));
                    uvs.Add(new Vector2(vx.Uv.X, vx.Uv.Y));
                    var bw = MakeWeight(node, v, skel, ref anySkin);
                    if (LogParts && file.Contains("00_b") && vx.Position.X > 0.30f && node.Skin != null && v < node.Skin.Length)
                    {
                        var sb = new System.Text.StringBuilder($"[ARMV] x={vx.Position.X:0.00} y={vx.Position.Y:0.00} rawFactors=");
                        foreach (var f in node.Skin[v]) sb.Append($"[{f.BoneId}:{skel.Bones[f.BoneId].name.Replace("Bip01 ", "")}={f.Weight:0.00}]");
                        sb.Append($" => bw=[{bw.boneIndex0}:{bw.weight0:0.00} {bw.boneIndex1}:{bw.weight1:0.00} {bw.boneIndex2}:{bw.weight2:0.00} {bw.boneIndex3}:{bw.weight3:0.00}]");
                        Debug.Log(sb.ToString());
                    }
                    weights.Add(bw);
                }
                string tex = node.TextureName ?? "";
                if (!subs.TryGetValue(tex, out var tris)) subs[tex] = tris = new List<int>();
                for (int i = 0; i + 2 < node.Indices.Length; i += 3)
                {
                    tris.Add(baseIndex + node.Indices[i]);
                    tris.Add(baseIndex + node.Indices[i + 1]);
                    tris.Add(baseIndex + node.Indices[i + 2]);
                }
                }   // end foreach node
            }       // end else (x3d path)
            if (verts.Count == 0 || !anySkin)
            {
                if (LogParts) Debug.Log($"[PART] {file}: SKIPPED verts={verts.Count} anySkin={anySkin}");
                return false;
            }
            if (LogParts && model != null)
            {
                var used = new SortedSet<int>();
                foreach (var w in weights)
                {
                    if (w.weight0 > 0) used.Add(w.boneIndex0);
                    if (w.weight1 > 0) used.Add(w.boneIndex1);
                    if (w.weight2 > 0) used.Add(w.boneIndex2);
                    if (w.weight3 > 0) used.Add(w.boneIndex3);
                }
                var sb2 = new System.Text.StringBuilder();
                foreach (var b in used) sb2.Append($"{b} ");
                Vector3 mn = new Vector3(1e9f, 1e9f, 1e9f), mx = new Vector3(-1e9f, -1e9f, -1e9f);
                foreach (var vv in verts) { mn = Vector3.Min(mn, vv); mx = Vector3.Max(mx, vv); }
                int geomNodes = 0; var nm = new System.Text.StringBuilder();
                foreach (var nd in model.Nodes)
                {
                    if (nd.IsBiped || nd.Vertices == null || nd.Vertices.Length == 0) continue;
                    geomNodes++;
                    var t = nd.Matrix.Translation;
                    nm.Append($"{nd.Name}(t={t.X:0.0},{t.Y:0.0},{t.Z:0.0}) ");
                }
                Debug.Log($"[PART] {file}: verts={verts.Count} x=[{mn.x:0.00}..{mx.x:0.00}] y=[{mn.y:0.00}..{mx.y:0.00}] z=[{mn.z:0.00}..{mx.z:0.00}] nodes={geomNodes} [{nm}] bones=[{sb2}]");
                // count verts whose DOMINANT bone is a limb-mid bone, to see if this part even
                // contains arm(13,14)/shin(33) geometry (sleeveless vest / shorts diagnosis)
                var cnt = new System.Collections.Generic.Dictionary<int, int>();
                foreach (var w in weights) { int b = w.boneIndex0; cnt.TryGetValue(b, out var n); cnt[b] = n + 1; }
                var sbC = new System.Text.StringBuilder("[DOMBONE] " + file + ": ");
                foreach (var b in new[] { 12, 13, 14, 15, 32, 33, 34, 35 })
                    if (cnt.TryGetValue(b, out var n) && n > 0) sbC.Append($"{b}({skel.Bones[b].name.Replace("Bip01 ", "")})={n} ");
                Debug.Log(sbC.ToString());
            }

            var mesh = new Mesh { name = file };
            if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.boneWeights = weights.ToArray();
            var keys = new List<string>(subs.Keys);
            mesh.subMeshCount = keys.Count;
            for (int s = 0; s < keys.Count; s++) mesh.SetTriangles(subs[keys[s]], s, false);

            // bindposes from the BIND pose (pose-independent), so a part added at runtime (live weapon equip)
            // skins correctly even while the skeleton is animating. At spawn these equal worldToLocalMatrix.
            mesh.bindposes = (Matrix4x4[])skel.BindWorldToLocal().Clone();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();   // NineDragons/Character normal map needs tangent space

            var go = new GameObject(file);
            go.transform.SetParent(parent, false);
            var mats = new Material[keys.Count];
            for (int s = 0; s < keys.Count; s++)
            {
                // character body/cloth is OPAQUE: many skin/cloth textures are DXT5 / 32-bit TGA
                // whose alpha is unused (0); the world alpha-cutout would clip them away entirely
                // (missing hands/legs/arms). Render them opaque instead.
                bool useEmb = embTex.TryGetValue(keys[s], out var et) && et != null;
                mats[s] = useEmb
                    ? assets.GetCharacterMaterialFromTexture(et, file + ":" + keys[s])   // Blender-embedded texture
                    : assets.GetCharacterMaterial(keys[s]);                              // pack texture by name
                if (useEmb && LogParts) Debug.Log($"[EMBTEX] {file} sub{s} '{keys[s]}' -> embedded glb texture {et.width}x{et.height}");
                if (LogParts)
                {
                    var tx = mats[s].mainTexture as Texture2D;
                    bool miss = tx != null && tx.name == "missing";
                    Debug.Log($"[TEX] {file} sub{s} tex='{keys[s]}' fmt={(tx != null ? tx.format.ToString() : "null")} {tx?.width}x{tx?.height} missing={miss}");
                }
            }
            if (NoSkin)
            {
                // plain static render at raw vertex positions (no bones) — diagnostic
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = mats;
                return true;
            }
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = skel.Bones;
            smr.rootBone = skel.BipRoot;
            smr.updateWhenOffscreen = true;
            smr.quality = SkinQuality.Bone4;   // 9Dragons verts blend up to 4 bones; a lower cap
                                               // truncates to a central bone and collapses limbs inward
            smr.sharedMaterials = mats;
            return true;
        }

        /// <summary>True when every weighted skin factor of this node references a bone in
        /// <paramref name="skip"/> (the node is bound solely to auxiliary bones, e.g. bo07 sheath).</summary>
        private static bool NodeOnlyUses(X3dNode node, HashSet<int> skip)
        {
            if (node.Skin == null || node.Skin.Length == 0) return false;   // rigid → keep
            bool anyWeighted = false;
            foreach (var facs in node.Skin)
            {
                if (facs == null) continue;
                foreach (var f in facs)
                {
                    if (f.Weight <= 0f) continue;
                    anyWeighted = true;
                    if (!skip.Contains(f.BoneId)) return false;   // uses a non-auxiliary bone → keep
                }
            }
            return anyWeighted;   // all weighted factors were auxiliary → drop
        }

        /// <summary>Load MESH + skin from a converted .glb and bind it to the supplied .xbp skeleton.
        /// The mesh is Blender-editable; the skeleton + animation stay the game's. glTF is right-handed
        /// (converter negated Z + reversed winding), so we negate Z back and un-reverse winding to land
        /// in the .xbp bind space. JOINTS_0 values are skeleton bone indices directly (converter used
        /// factor.BoneId = skel index, joints emitted in bone order). Returns false if not a skinned glb.</summary>
        private static bool TryLoadGlbSkin(byte[] glb, Skeleton skel, List<Vector3> verts, List<Vector3> normals,
            List<Vector2> uvs, List<BoneWeight> weights, Dictionary<string, List<int>> subs, ref bool anySkin,
            HashSet<int> skipBones = null, Dictionary<string, Texture2D> embTex = null)
        {
            if (glb == null || glb.Length < 20) return false;
            try
            {
                var br = new BinaryReader(new MemoryStream(glb));
                if (br.ReadUInt32() != 0x46546C67u) return false;              // "glTF"
                br.ReadUInt32(); br.ReadUInt32();                              // version, length
                uint jlen = br.ReadUInt32(); br.ReadUInt32();                  // JSON chunk len + type
                byte[] jb = br.ReadBytes((int)jlen);
                br.ReadUInt32(); br.ReadUInt32();                             // BIN chunk len + type
                byte[] bin = br.ReadBytes((int)(glb.Length - br.BaseStream.Position));
                var root = JObject.Parse(Encoding.UTF8.GetString(jb));
                var accessors = (JArray)root["accessors"]; var views = (JArray)root["bufferViews"];
                var materials = root["materials"] as JArray; var meshes = (JArray)root["meshes"];
                (int off, int len) View(int bv) { var v = (JObject)views[bv]; return ((int?)v["byteOffset"] ?? 0, (int)v["byteLength"]); }
                float[] F(int acc, int comp) { var a = (JObject)accessors[acc]; var (vo, _) = View((int)a["bufferView"]);
                    int st = vo + ((int?)a["byteOffset"] ?? 0), c = (int)a["count"]; var r = new float[c * comp];
                    for (int i = 0; i < r.Length; i++) r[i] = BitConverter.ToSingle(bin, st + i * 4); return r; }
                int[] J4(int acc) { var a = (JObject)accessors[acc]; var (vo, _) = View((int)a["bufferView"]);
                    int st = vo + ((int?)a["byteOffset"] ?? 0), c = (int)a["count"], ct = (int)a["componentType"]; var r = new int[c * 4];
                    for (int i = 0; i < c * 4; i++) r[i] = ct == 5121 ? bin[st + i] : BitConverter.ToUInt16(bin, st + i * 2); return r; }
                int[] IdxA(int acc) { var a = (JObject)accessors[acc]; var (vo, _) = View((int)a["bufferView"]);
                    int st = vo + ((int?)a["byteOffset"] ?? 0), c = (int)a["count"], ct = (int)a["componentType"]; var r = new int[c];
                    for (int i = 0; i < c; i++) r[i] = ct == 5123 ? BitConverter.ToUInt16(bin, st + i * 2) : ct == 5125 ? (int)BitConverter.ToUInt32(bin, st + i * 4) : bin[st + i]; return r; }

                // embedded textures (a Blender-authored model whose textures aren't in TR_*.XP):
                // map material name -> decoded Texture2D from the glb's own images. Converted (noembed)
                // models have no images, so this stays empty and the pack-by-name path is used.
                if (embTex != null && materials != null)
                {
                    var images = root["images"] as JArray;
                    var textures = root["textures"] as JArray;
                    if (images != null && textures != null)
                    {
                        foreach (JObject mat in materials)
                        {
                            string mn = (string)mat["name"] ?? "";
                            if (string.IsNullOrEmpty(mn) || embTex.ContainsKey(mn)) continue;
                            var pbr = mat["pbrMetallicRoughness"] as JObject;
                            var bct = pbr?["baseColorTexture"] as JObject;
                            if (bct == null) continue;
                            int ti = (int)bct["index"];
                            if (ti < 0 || ti >= textures.Count) continue;
                            var texj = (JObject)textures[ti];
                            if (texj["source"] == null) continue;
                            int si = (int)texj["source"];
                            if (si < 0 || si >= images.Count) continue;
                            var img = (JObject)images[si];
                            if (img["bufferView"] == null) continue;              // embedded images only
                            var (io, il) = View((int)img["bufferView"]);
                            var bytes = new byte[il]; System.Array.Copy(bin, io, bytes, 0, il);
                            var t2d = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                            if (t2d.LoadImage(bytes)) embTex[mn] = t2d;
                        }
                    }
                }

                var prims = (JArray)meshes[0]["primitives"];
                int nb = skel.Bones.Length;
                foreach (JObject prim in prims)
                {
                    var attr = (JObject)prim["attributes"];
                    if (attr["JOINTS_0"] == null || attr["WEIGHTS_0"] == null) return false;   // not skinned
                    var pos = F((int)attr["POSITION"], 3);
                    var nor = attr["NORMAL"] != null ? F((int)attr["NORMAL"], 3) : null;
                    var uv = attr["TEXCOORD_0"] != null ? F((int)attr["TEXCOORD_0"], 2) : null;
                    var joi = J4((int)attr["JOINTS_0"]);
                    var wei = F((int)attr["WEIGHTS_0"], 4);
                    var idx = IdxA((int)prim["indices"]);
                    int vc = pos.Length / 3, baseIndex = verts.Count;

                    // WeaponMount: drop a primitive whose skin uses ONLY auxiliary bones (the opposite-hip
                    // sheath on bo07/bo08) — same as the x3d NodeOnlyUses, but per-primitive (converter
                    // emits one primitive per source node).
                    if (skipBones != null && skipBones.Count > 0)
                    {
                        bool anyW = false, onlyAux = true;
                        for (int i = 0; i < vc && onlyAux; i++)
                            for (int k = 0; k < 4; k++)
                            {
                                if (wei[i * 4 + k] <= 0f) continue;
                                anyW = true;
                                if (!skipBones.Contains(joi[i * 4 + k])) { onlyAux = false; break; }
                            }
                        if (anyW && onlyAux) continue;   // sheath node → skip
                    }
                    for (int i = 0; i < vc; i++)
                    {
                        verts.Add(new Vector3(pos[i * 3], pos[i * 3 + 1], -pos[i * 3 + 2]));           // RH -> LH
                        normals.Add(nor != null ? new Vector3(nor[i * 3], nor[i * 3 + 1], -nor[i * 3 + 2]) : Vector3.up);
                        uvs.Add(uv != null ? new Vector2(uv[i * 2], uv[i * 2 + 1]) : Vector2.zero);
                        int b0 = joi[i * 4], b1 = joi[i * 4 + 1], b2 = joi[i * 4 + 2], b3 = joi[i * 4 + 3];
                        float w0 = wei[i * 4], w1 = wei[i * 4 + 1], w2 = wei[i * 4 + 2], w3 = wei[i * 4 + 3];
                        if (b0 >= nb) { b0 = 0; }               if (b1 >= nb) { b1 = 0; w1 = 0; }
                        if (b2 >= nb) { b2 = 0; w2 = 0; }       if (b3 >= nb) { b3 = 0; w3 = 0; }
                        float s = w0 + w1 + w2 + w3; if (s <= 0f) { w0 = 1f; s = 1f; }
                        weights.Add(new BoneWeight {
                            boneIndex0 = b0, weight0 = w0 / s, boneIndex1 = b1, weight1 = w1 / s,
                            boneIndex2 = b2, weight2 = w2 / s, boneIndex3 = b3, weight3 = w3 / s });
                        anySkin = true;
                    }
                    string tex = "";
                    if (prim["material"] != null && materials != null)
                    { int mi = (int)prim["material"]; if (mi >= 0 && mi < materials.Count) tex = (string)materials[mi]["name"] ?? ""; }
                    if (!subs.TryGetValue(tex, out var tris)) subs[tex] = tris = new List<int>();
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    { tris.Add(baseIndex + idx[i]); tris.Add(baseIndex + idx[i + 2]); tris.Add(baseIndex + idx[i + 1]); }  // un-reverse winding
                }
                return verts.Count > 0;
            }
            catch (Exception e) { if (LogParts) Debug.Log($"[GLTF-SKIN] parse fail: {e.Message}"); return false; }
        }

        private static BoneWeight MakeWeight(X3dNode node, int vertex, Skeleton skel, ref bool anySkin)
        {
            var bw = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            if (node.Skin == null || vertex >= node.Skin.Length) return bw;
            var factors = node.Skin[vertex];
            if (factors == null || factors.Length == 0) return bw;

            // take up to 4 strongest influences; boneId indexes the skeleton directly
            int i0 = 0, i1 = 0, i2 = 0, i3 = 0;
            float w0 = 0, w1 = 0, w2 = 0, w3 = 0;
            for (int f = 0; f < factors.Length; f++)
            {
                int id = factors[f].BoneId;
                if (id < 0 || id >= skel.Bones.Length) continue;
                float w = factors[f].Weight;
                if (w > w0) { i3=i2;w3=w2; i2=i1;w2=w1; i1=i0;w1=w0; i0=id;w0=w; }
                else if (w > w1) { i3=i2;w3=w2; i2=i1;w2=w1; i1=id;w1=w; }
                else if (w > w2) { i3=i2;w3=w2; i2=id;w2=w; }
                else if (w > w3) { i3=id;w3=w; }
            }
            float sum = w0 + w1 + w2 + w3;
            if (sum <= 0f) return bw;
            anySkin = true;
            return new BoneWeight
            {
                boneIndex0 = i0, weight0 = w0 / sum,
                boneIndex1 = i1, weight1 = w1 / sum,
                boneIndex2 = i2, weight2 = w2 / sum,
                boneIndex3 = i3, weight3 = w3 / sum,
            };
        }
    }
}
