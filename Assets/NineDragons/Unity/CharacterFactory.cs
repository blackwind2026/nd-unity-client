using System.Collections.Generic;
using UnityEngine;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// Assembles a real 9Dragons character from its body-part meshes (MR_BASEMODEL.XP),
    /// textured from TR_CHARACTER.XP — the parts the client's CharactorModel.XMS clusters
    /// list. Naming (verified in CharactorModel.XMS + MR_BASEMODEL directory):
    ///   a_&lt;m|w&gt;_NN_a  = face/head, _b = upper body, _c1/_c2 = hands, _d = lower body,
    ///   _e1/_e2 = feet, _fN = hair. NN 00 = base naked skin.
    ///
    /// The X3D body meshes are skinned (physique), but their vertices are stored in BIND
    /// pose (model space, feet at origin, ~1.18 units tall = _XDEF_CHARACTERAVERAGEHEIGHT),
    /// so rendering them statically already gives a correct standing character. Skeleton +
    /// XA animation are layered on top separately (see CharacterAnimator).
    /// </summary>
    public static class CharacterFactory
    {
        // part suffixes assembled for a naked base character
        private static readonly string[] BodyParts = { "a", "b", "c1", "c2", "d", "e1", "e2" };

        public static GameObject BuildPlayer(AssetService assets, int sex, int faceId, int hairId, string name)
        {
            string g = sex == 0 ? "m" : "w";     // 0 = male in the client's model tables
            var root = new GameObject("Character_" + name);
            // parts live under a "visual" child so a procedural animator can bob/sway them
            // without fighting the controller-owned root position
            var visual = new GameObject("visual").transform;
            visual.SetParent(root.transform, false);

            int built = 0;
            foreach (var part in BodyParts)
                if (AddPart(assets, visual, $"a_{g}_00_{part}.X3D")) built++;

            // hair: base style; hairId selects the f-variant later (clamped to what exists)
            int hv = Mathf.Clamp(hairId, 1, 6);
            if (!AddPart(assets, visual, $"a_{g}_0{hv}_f1.X3D"))
                AddPart(assets, visual, $"a_{g}_01_f1.X3D");

            if (built == 0)
            {
                var cap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                cap.GetComponent<Collider>().enabled = false;   // must not block click-to-move raycasts
                cap.transform.SetParent(visual, false);
                cap.transform.localPosition = Vector3.up;
            }
            return root;
        }

        private static bool AddPart(AssetService assets, Transform parent, string file)
        {
            var model = assets.GetStaticModel(file);
            if (model == null || model.Mesh.vertexCount == 0) return false;

            var go = new GameObject(file);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
            var mats = new Material[model.SubmeshTextures.Length];
            for (int i = 0; i < mats.Length; i++)
                mats[i] = assets.GetObjectMaterial(model.SubmeshTextures[i]);
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            return true;
        }
    }
}
