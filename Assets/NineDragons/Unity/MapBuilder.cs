using System.Text;
using UnityEngine;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// Builds a whole map into the scene from the M6 core loaders:
    /// terrain chunks, static world objects, functional objects (gates etc.) and NPC markers.
    /// Phase-1 renderer — no splat blending, no skinned NPC models, no water surface yet.
    /// </summary>
    public static class MapBuilder
    {
        public sealed class Result
        {
            public GameObject Root;
            public XtvTerrain Terrain;
            public int ObjectsPlaced, ObjectsMissing, FunctionalPlaced, NpcMarkers;
            public int TerrainVerts, Trees;
            public WeatherFactory.Kind Weather;

            public override string ToString() =>
                $"objects {ObjectsPlaced} (+{FunctionalPlaced} functional, {ObjectsMissing} missing mesh), " +
                $"npc markers {NpcMarkers}, terrain verts {TerrainVerts}";
        }

        /// <summary>Animate NPCs/mobs with per-entity skeletons (skinned + idle/run .XA).</summary>
        public static bool AnimateEntities = true;

        public static Result Build(AssetService assets, string mapName, bool terrainColliders = false)
        {
            var res = new Result();
            var root = new GameObject(mapName);
            res.Root = root;

            var t = assets.LoadTerrain(mapName);
            res.Terrain = t;
            res.TerrainVerts = t.VertexPerSide * t.VertexPerSide;

            // ---- terrain ----
            // prefer the faithful multi-layer detail splat (the client's DrawTerrain); fall back
            // to the composed low-res splat-cache ground, then to the tiled base detail texture.
            Material terrainMat = GroundSplat.Build(t, assets, 1.35f);
            bool planar = false;
            string terrainPath = "splat";
            if (terrainMat == null)
            {
                terrainMat = new Material(NdShaders.TerrainVC) { name = "terrain" };
                var ground = GroundTexture.Build(t);
                planar = ground != null;
                terrainMat.mainTexture = ground != null ? ground
                    : assets.GetTexture(t.LayerTextureNames.Length > 0 ? t.LayerTextureNames[0] : null);
                terrainPath = ground != null ? "composedGround" : "tiledBase(NO ROAD)";
            }
            // chẩn đoán "đường không hiện": path nào đang dùng + có layer đường (load_*) không
            int roadLayer = -1;
            for (int li = 0; li < t.LayerTextureNames.Length; li++)
                if (t.LayerTextureNames[li] != null && t.LayerTextureNames[li].ToLowerInvariant().StartsWith("load")) { roadLayer = li; break; }
            Debug.Log($"[TERRAIN] map={mapName} path={terrainPath} layers={t.LayerCount} planar={planar} roadLayer={roadLayer} 2dArray={UnityEngine.SystemInfo.supports2DArrayTextures}");
            TerrainFactory.Build(t, terrainMat, root.transform, terrainColliders, planar);

            // water surfaces + speedtree proxies
            EnvironmentFactory.BuildWater(t, root.transform);
            res.Trees = EnvironmentFactory.BuildTrees(t, assets, root.transform);
            GrassFactory.Build(t, assets, root.transform);
            res.Weather = WeatherFactory.Build(t, root.transform);

            // ---- static world objects ----
            var objectsRoot = new GameObject("Objects").transform;
            objectsRoot.SetParent(root.transform, false);
            foreach (var obj in t.Objects)
            {
                if (obj.MeshIndex < 0 || obj.MeshIndex >= t.MeshNames.Length) { res.ObjectsMissing++; continue; }
                if (PlaceModel(assets, t.MeshNames[obj.MeshIndex], obj.Matrix, objectsRoot))
                    res.ObjectsPlaced++;
                else
                    res.ObjectsMissing++;
            }

            // ---- functional objects (gates, doors, event props) ----
            var funcRoot = new GameObject("FunctionalObjects").transform;
            funcRoot.SetParent(root.transform, false);
            foreach (var fo in t.FunctionalObjects)
            {
                if (fo.MeshIndex < 0 || fo.MeshIndex >= t.MeshNames.Length) continue;
                if (PlaceModel(assets, t.MeshNames[fo.MeshIndex], fo.Matrix, funcRoot))
                    res.FunctionalPlaced++;
            }

            // ---- NPC markers (skinned NPC models are phase 2) ----
            var npcs = assets.LoadNpcs(mapName);
            if (npcs != null)
            {
                var npcRoot = new GameObject("NPCs").transform;
                npcRoot.SetParent(root.transform, false);
                int nAnim = 0, nStatic = 0, nInvis = 0;
                foreach (var npc in npcs.Npcs)
                {
                    // animated skinned NPCs are available (assets.BuildAnimatedNpc) but a per-NPC
                    // skeleton for dozens of NPCs is a heavy startup cost, so towns use the fast
                    // static bind-pose model; the player stays fully skinned+animated.
                    if (AnimateEntities)
                    {
                        var animated = assets.BuildAnimatedNpc(npc.MeshIndex, npc.UniqueId.ToString());
                        if (animated != null)
                        {
                            animated.transform.SetParent(npcRoot, false);
                            UnityConvert.ApplyPlacement(animated.transform, npc.Matrix);
                            GroundToTerrain(animated.transform, t);
                            AttachNpcNameplate(animated, npc, assets);   // VN name plate: gold if quest-giver, else cream
                            AttachNpcClickable(animated, npc, assets);   // click to walk up + open its conversation
                            res.NpcMarkers++; nAnim++;
                            continue;
                        }
                    }

                    var model = assets.GetNpcModel(npc.MeshIndex);
                    var go = new GameObject($"npc_{npc.UniqueId}_{npc.MeshIndex}");
                    go.transform.SetParent(npcRoot, false);
                    UnityConvert.ApplyPlacement(go.transform, npc.Matrix);   // NPC model feet at origin
                    GroundToTerrain(go.transform, t);

                    if (model != null && model.Mesh.vertexCount > 0)
                    {
                        go.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
                        var mats = new Material[model.SubmeshTextures.Length];
                        for (int i = 0; i < mats.Length; i++)
                            mats[i] = assets.GetObjectMaterial(model.SubmeshTextures[i]);
                        go.AddComponent<MeshRenderer>().sharedMaterials = mats;
                        AttachNpcNameplate(go, npc, assets);   // VN name plate: gold if quest-giver, else cream
                        AttachNpcClickable(go, npc, assets);   // click to walk up + open its conversation
                        nStatic++;
                    }
                    else nInvis++;
                    // no green-capsule placeholder for an NPC whose model failed to load — it
                    // clutters the town; the NPC node still exists at its spot, just invisible.
                    res.NpcMarkers++;
                }
                Debug.Log($"[NPCSUM] total={npcs.Npcs.Count} animated={nAnim} staticVisible={nStatic} invisible={nInvis} (AnimateEntities={AnimateEntities})");
            }

            return res;
        }

        private static bool PlaceModel(AssetService assets, string meshFile,
                                       System.Numerics.Matrix4x4 matrix, Transform parent)
        {
            // glTF overlay: a converted Data\Model\gltf\<name>.glb wins over the .x3d (self-contained
            // mesh + embedded textures). Instantiate the shared template and place it like any object.
            var glb = assets.GetObjectGlbTemplate(meshFile);
            if (glb != null)
            {
                var gi = UnityEngine.Object.Instantiate(glb);
                gi.name = meshFile;
                gi.SetActive(true);
                gi.transform.SetParent(parent, false);
                UnityConvert.ApplyPlacement(gi.transform, matrix);
                return true;
            }

            var model = assets.GetStaticModel(meshFile);
            if (model == null || model.Mesh.vertexCount == 0) return false;

            var go = new GameObject(meshFile);
            go.transform.SetParent(parent, false);
            UnityConvert.ApplyPlacement(go.transform, matrix);

            go.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
            var mats = new Material[model.SubmeshTextures.Length];
            for (int i = 0; i < mats.Length; i++)
                mats[i] = assets.GetObjectMaterial(model.SubmeshTextures[i]);
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            // collider only on large objects (buildings) so the camera doesn't clip through them;
            // skipping small props keeps mesh-collider baking (startup cost) down
            return true;
        }

        /// <summary>Give a map NPC the persistent over-head nameplate the client shows: occupation
        /// (roll) line above the name, in a FIXED colour (never the mob level bands) — gold #FFC436 for
        /// a quest-giver, cream #DBC488 otherwise. The name/roll are the VIETNAMESE strings from
        /// NDTextDB "NPCDialog" (name = UniqueId*1000+1, roll = +2; XNPCScript.cpp:706), NOT the Korean
        /// CP949 bytes in the .xns. NPCs with no localized entry are suppressed (no over-head text),
        /// matching the base client, which shows only VN-localized NPC names.</summary>
        private static void AttachNpcNameplate(GameObject host, XnsNpc npc, AssetService assets)
        {
            if (host == null || npc == null || assets == null) return;
            if (!assets.NpcDisplay(npc.UniqueId, out var name, out var roll)) return;   // no VN name -> suppress
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(roll)) return;
            // show the name as the main line (fall back to the roll if the name is missing)
            string mainLine = !string.IsNullOrEmpty(name) ? name : roll;
            string titleLine = (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(roll)) ? roll : null;
            bool quest = npc.HasQuestExt && npc.QuestId > 0;
            var col = WorldNameplate.NpcColor(quest);
            float headY = NpcHeadHeight(host.transform);
            var np = host.AddComponent<WorldNameplate>();
            // NAME line = the NPC colour (gold quest-giver / cream normal); OCCUPATION line above it =
            // WHITE, matching the client (_XSC_DEFAULT_WHITE for the roll, _XSC_INFORMATION/gold for the name).
            np.SetupSimple(host.transform, headY, mainLine, col, titleLine, Color.white);
        }

        /// <summary>Make a map NPC clickable: a capsule collider sized to the model on the NPC layer,
        /// plus an <see cref="NpcId"/> carrying its UniqueId/roll so a click can walk up to it and open its
        /// conversation (quest / shop / warehouse / craft / refine). Without this an NPC has no collider,
        /// so a click passes straight through it to the ground — which is why "click NPC không được".</summary>
        private static void AttachNpcClickable(GameObject host, XnsNpc npc, AssetService assets)
        {
            if (host == null || npc == null) return;
            // size a capsule to the visible model (feet at the pivot); fall back to a person-sized box.
            float h = 1.8f, r = 0.5f;
            float top = float.NegativeInfinity;
            foreach (var rend in host.GetComponentsInChildren<Renderer>())
            {
                if (rend is ParticleSystemRenderer) continue;
                if (rend.bounds.max.y > top) top = rend.bounds.max.y;
            }
            if (!float.IsNegativeInfinity(top))
                h = Mathf.Clamp(top - host.transform.position.y, 0.6f, 6f);

            var cap = host.AddComponent<CapsuleCollider>();
            cap.direction = 1;               // Y-axis
            cap.height = h;
            cap.radius = Mathf.Clamp(h * 0.28f, 0.3f, 0.9f);
            cap.center = new Vector3(0f, h * 0.5f, 0f);

            var id = host.AddComponent<NpcId>();
            id.UniqueId = npc.UniqueId;
            id.MeshIndex = npc.MeshIndex;
            id.JobClass = npc.JobClass;
            id.HasQuest = npc.HasQuestExt && npc.QuestId > 0;
            // localized name/role (NDTextDB, same source as the over-head plate) — NOT the raw CP949
            // bytes in the .xns (those render as mojibake). Fall back to the raw name only if unlocalized.
            if (assets.NpcDisplay(npc.UniqueId, out var locName, out var locRoll))
            { id.Name = locName ?? ""; id.Roll = locRoll ?? ""; }
            else { id.Name = npc.Name ?? ""; id.Roll = npc.Roll ?? ""; }

            SetLayerRecursive(host, NpcId.NpcLayer);
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
        }

        private static float NpcHeadHeight(Transform root)
        {
            float top = float.NegativeInfinity;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
                if (r.bounds.max.y > top) top = r.bounds.max.y;
            if (float.IsNegativeInfinity(top)) return 1.9f;
            return Mathf.Clamp(top - root.position.y + 0.3f, 1.0f, 6.5f);
        }

        /// <summary>Drop an entity onto the terrain surface at its XZ (NPCs are authored with a Y
        /// that need not match our terrain mesh, so they float/sink without this).</summary>
        private static void GroundToTerrain(Transform t, XtvTerrain terrain)
        {
            if (terrain == null) return;
            var p = t.localPosition;
            p.y = TerrainFactory.HeightAt(terrain, p.x, p.z);
            t.localPosition = p;
        }

        /// <summary>Human-readable map inventory for logs.</summary>
        public static string Describe(XtvTerrain t)
        {
            var sb = new StringBuilder();
            sb.Append($"v{t.Version:0.00} tiles={t.TileCount} world={t.Width}x{t.Height} ");
            sb.Append($"meshes={t.MeshNames.Length} objects={t.Objects.Count} ");
            sb.Append($"func={t.FunctionalObjects.Count} trees={t.SpeedTrees.Count} water={t.Water.Count}");
            return sb.ToString();
        }
    }
}
