using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Nd.Session;
using NineDragons.Assets;
using Debug = UnityEngine.Debug;

namespace NineDragons.Client
{
    /// <summary>
    /// The playable slice: login (M1/M2) → select character → enter world (M5) → load the
    /// zone's map (M6 core + render) → stand the character on the terrain, with live
    /// monsters/players from the entity stream. Server-authoritative: this only displays
    /// what <see cref="GameSession"/> reports.
    /// </summary>
    public sealed class NdGameClient : MonoBehaviour
    {
        [Header("Server (nd-world live)")]
        public string host = "127.0.0.1";
        public int loginPort = 48300;
        public int gsPort = 25019;
        public string account = "aladdin";
        public string password = "";          // scrubbed for public release; set via -ndPassword
        [Tooltip("Exact character name; empty = first character on the account")]
        public string characterName = "";

        [Header("Data")]
        public string dataPath = ClientData.Root;   // auto-prefers C:\9Dragons Unity (glTF sandbox) when present
        [Tooltip("Crypto lump; empty = StreamingAssets/lump.dat")]
        public string lumpPath = "";

        [Header("Debug")]
        [Tooltip("Save a screenshot this many seconds after entering the world (0 = off)")]
        public float screenshotAfter = 8f;
        [Tooltip("Auto-walk this far (+X,+Z) after entering, to self-test movement without input")]
        public float autoWalk = 0f;
        private Vector3 _startPos;

        private GameSession _session;
        private AssetService _assets;
        private XtvTerrain _terrain;
        private GameObject _mapRoot;    // the whole built map (terrain+objects+NPCs+water) — torn down on a zone reload
        private int _builtZone = -1;    // zone the current map was built for (detects vé di chuyển reloads)
        private bool _villagePicker;    // key-E Move Village list is open
        private Vector2 _villageScroll;
        private WeatherFactory.Kind _weather;
        private Transform _player;
        private PlayerController _control;
        private GameHud _hud;   // kept so a vé-di-chuyển reload can re-point the minimap/name at the new map
        private int _sex, _face, _hair, _level;
        // Force EVERY other player's face to this value (0 = use the real op27 face). Kept at 0: the
        // client mapping is correct; op27's face value is what the GS binary broadcasts (server-side).
        private const int ForceOtherFace = 0;
        private int _amor, _pants, _cap, _weapon, _weaponModelIndex, _shoes;
        private int _weaponType;   // object-type vũ khí local (0/15/19) để mount/icon đúng bảng
        private int _weaponGlowDefault;   // preview default enchant grade for the aura (0 = off in live)
        private string _mapName = "";
        private readonly Dictionary<ushort, Transform> _mobs = new Dictionary<ushort, Transform>();
        private readonly Dictionary<ushort, SkeletalAnimator> _mobAnims = new Dictionary<ushort, SkeletalAnimator>();
        private readonly Dictionary<ushort, Transform> _players = new Dictionary<ushort, Transform>();
        // Mob đang chiến với char → thời điểm hết "quay mặt vào char" (Time.time). Làm mới mỗi đòn/skill.
        private readonly Dictionary<ushort, float> _mobFaceUntil = new Dictionary<ushort, float>();
        private const float MobFaceHold = 6f;   // giây giữ hướng mặt vào char sau mỗi đòn/bị đánh (chỉ áp khi mob ĐỨNG; đủ phủ cooldown giữa 2 đòn để mob ko quay đi rồi quay lại)
        private readonly HashSet<ushort> _playerModeled = new HashSet<ushort>();   // players drawn with the real body (not the marker)
        private readonly Dictionary<ushort, float> _apprReqAt = new Dictionary<ushort, float>();  // next time to (re)request a player's appearance
        private Transform _mobRoot;
        private float _shotAt = -1f;
        private int _fxTestId;         // -ndFxTest <id>: re-play this effect template on the preview char
        private float _fxNext;
        private string _shotName = "nd_screenshot.png";   // preview overrides so a live client can't clobber it
        private int _npLogged;   // throttle for the [NAMEPLATE] level→colour diagnostic
        private string _status = "starting...";
        private bool _demoMode;   // -ndPanelDemo 1 : build HUD + character window with synthetic data, no server

        private static float ArgF(string name, float fallback)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == name && float.TryParse(a[i + 1], out var v)) return v;
            return fallback;
        }

        /// <summary>the string value after <paramref name="name"/> on the command line, or null.</summary>
        private static string ArgS(string name)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == name) return a[i + 1];
            return null;
        }

        private void Start()
        {
            // a live network client must keep pumping even when the window has no focus
            Application.runInBackground = true;
            // The terrain mesh is wound the D3D way (front faces DOWN — the reason its material
            // renders with Cull Off). Its MeshCollider inherits that winding, so a click ray coming
            // DOWN from the camera hits the terrain's back face — which Unity ignores by default,
            // making every ground click miss. Let queries hit backfaces so click-to-move lands.
            Physics.queriesHitBackfaces = true;
            // Skinned character verts blend up to 4 bones. The build's DEFAULT quality level can be
            // "Very Low" (skinWeights=1) / "Low" (=2): at RENDER time every limb-mid vertex is then
            // truncated to its single dominant bone, so the upper-arm/forearm collapse onto the
            // shoulder (buried in the torso) and the shin collapses onto the knee — leaving the
            // rigid single-bone extremities (hands, feet) floating with a gap. BakeMesh ignores the
            // cap, which is why the CPU bake looked complete while the GPU/CPU render dropped them.
            // Pin the richest quality level and unlimited skin weights so the whole limb renders.
            QualitySettings.SetQualityLevel(QualitySettings.names.Length - 1, true);
            QualitySettings.skinWeights = SkinWeights.Unlimited;
            autoWalk = ArgF("-ndAutoWalk", autoWalk);
            AssetService.LogLoose = ArgF("-ndLogLoose", 0f) > 0f || ArgF("-ndLogParts", 0f) > 0f;   // log loose-overlay model hits

            // Layout-verification path (no server): -ndPanelDemo 1 builds only the HUD +
            // character window with synthetic Session data so every C-window tab can be
            // screenshot-verified offline. The real world/login path is skipped entirely.
            if (ArgF("-ndPanelDemo", 0f) > 0f)
            {
                try { BuildPanelDemo(); }
                catch (Exception ex) { _status = "DEMO FAILED: " + ex.Message; Debug.LogError("[NdGameClient] demo " + ex); }
                return;
            }

            // Nameplate-verification path (no server): -ndNameplateDemo 1 spawns a row of fake mobs
            // at every level band + the player, so the WorldNameplate look can be screenshot-verified
            // offline when the live server is unreachable.
            if (ArgF("-ndNameplateDemo", 0f) > 0f)
            {
                try { BuildNameplateDemo(); }
                catch (Exception ex) { _status = "DEMO FAILED: " + ex.Message; Debug.LogError("[NdGameClient] nameplate demo " + ex); }
                return;
            }

            // Target-frame verification (no server): -ndTargetDemo 1 fabricates a normal mob + a boss
            // + a quest mob in the session and lays their target-status frames in a row at the top, so
            // the three frame/icon variants can be screenshot-verified offline.
            if (ArgF("-ndTargetDemo", 0f) > 0f)
            {
                try { BuildTargetDemo(); }
                catch (Exception ex) { _status = "DEMO FAILED: " + ex.Message; Debug.LogError("[NdGameClient] target demo " + ex); }
                return;
            }

            // Equipment-verification path (no server): -ndCharPreview 1 builds ONE dressed, animated
            // character (real worn armor/pants/cap/shoes + a held weapon + enchant aura) in front of
            // a fixed camera, so the shoes/weapon-model/weapon-glow can be screenshot-verified offline.
            if (ArgF("-ndCharPreview", 0f) > 0f)
            {
                try { BuildCharPreview(); }
                catch (Exception ex) { _status = "PREVIEW FAILED: " + ex.Message; Debug.LogError("[NdGameClient] char preview " + ex); }
                return;
            }

            // Round-trip proof (no server): -ndLoadGlb <path> loads a .glb (from the x3dgltf tool /
            // Blender) with GlbLoader and shows it in a fixed-camera scene → screenshot nd_glbpreview.png.
            var glbArg = ArgS("-ndLoadGlb");
            if (!string.IsNullOrEmpty(glbArg))
            {
                try { BuildGlbPreview(glbArg); }
                catch (Exception ex) { _status = "GLB LOAD FAILED: " + ex.Message; Debug.LogError("[NdGameClient] glb load " + ex); }
                return;
            }

            // Self-contained model path (no server, no .xbp): -ndSelfGlb <modelName> builds a model
            // straight from a .glb's OWN armature + skin + baked animation (Skeleton.BuildFromGlb →
            // SkinnedCharacterFactory.BuildSelfContainedGlb) — proof a complete Blender build runs in
            // game without any .xbp/.XA. -ndGlbSkelCheck first logs the numeric bone delta between the
            // .xbp skeleton and the same character's glb-recovered skeleton.
            var selfArg = ArgS("-ndSelfGlb");
            if (!string.IsNullOrEmpty(selfArg))
            {
                try { BuildSelfGlbPreview(selfArg); }
                catch (Exception ex) { _status = "SELFGLB FAILED: " + ex.Message; Debug.LogError("[NdGameClient] selfglb " + ex); }
                return;
            }

            try
            {
                RunLoginAndBuild();
            }
            catch (Exception ex)
            {
                _status = "FAILED: " + ex.Message;
                Debug.LogError("[NdGameClient] " + ex);
            }
        }

        // ------------------------------------------------------------ glb round-trip preview (no server)
        // Loads a .glb with glTFast (the standard Unity glTF importer) — handles skinning + RH→LH so the
        // result is clean (like the three.js render). Also the path a Blender-authored model takes into game.
        private void BuildGlbPreview(string path)
        {
            _demoMode = true;
            _shotName = "nd_glbpreview.png";
            if (!System.IO.File.Exists(path)) { _status = "glb not found: " + path; Debug.LogError("[glb] not found " + path); return; }

            var cam = Camera.main;
            if (cam == null) cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
            cam.farClipPlane = 500f;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.1f;
            sun.transform.rotation = Quaternion.Euler(48f, 150f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.60f);

            _ = LoadGlbAsync(path, cam);
        }

        private async System.Threading.Tasks.Task LoadGlbAsync(string path, Camera cam)
        {
            try
            {
                var gltf = new GLTFast.GltfImport();
                bool ok = await gltf.LoadFile(path, new System.Uri(path));
                if (!ok) { _status = "glTFast load failed"; Debug.LogError("[glb] glTFast load failed: " + path); return; }
                var go = new GameObject(System.IO.Path.GetFileNameWithoutExtension(path));
                bool inst = await gltf.InstantiateMainSceneAsync(go.transform);
                if (!inst) { _status = "glTFast instantiate failed"; Debug.LogError("[glb] instantiate failed"); return; }

                var rends = go.GetComponentsInChildren<Renderer>();
                if (rends.Length == 0) { _status = "glb: no renderers"; return; }
                var b = rends[0].bounds; foreach (var r in rends) b.Encapsulate(r.bounds);
                float rad = Mathf.Max(0.1f, b.extents.magnitude);
                cam.transform.position = b.center + new Vector3(0f, 0f, -rad * 2.4f);
                cam.transform.LookAt(b.center);
                cam.nearClipPlane = Mathf.Max(0.01f, rad / 100f);
                _status = $"glb: {rends.Length} renderers, size {b.size}";
                Debug.Log($"[glb] glTFast loaded {path}: {rends.Length} renderers bounds={b.size}");
                _shotAt = Time.time + 1.5f;   // fire the verification screenshot
            }
            catch (Exception ex) { _status = "glb load ex: " + ex.Message; Debug.LogError("[glb] " + ex); }
        }

        // ------------------------------------------------------------ self-contained glb preview (no server)
        // Builds a model from a single .glb that carries its own armature + skin (+ animation) through the
        // GAME's own pipeline (Skeleton.BuildFromGlb + SkinnedCharacterFactory + SkeletalAnimator), i.e. no
        // .xbp and no .XA — the "complete model built in Blender" workflow. Same fixed-camera scene as the
        // char preview; screenshot nd_selfglb.png.
        private void BuildSelfGlbPreview(string modelName)
        {
            _demoMode = true;
            _shotName = "nd_selfglb.png";
            ClientData.Root = dataPath;

            var cam = Camera.main;
            if (cam == null) cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
            cam.farClipPlane = 500f;
            if (ArgF("-ndNoPostFx", 0f) <= 0f) NdPostFx.Attach(cam);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.1f;
            sun.transform.rotation = Quaternion.Euler(48f, 150f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.55f);

            try { _assets = new AssetService(); _assets.Open(); }
            catch (Exception ex) { _status = "assets unavailable: " + ex.Message; Debug.LogError("[selfglb] " + ex); return; }

            SkinnedCharacterFactory.LogAnim = true;
            SkinnedCharacterFactory.LogParts = ArgF("-ndLogParts", 0f) > 0f;
            if (ArgF("-ndGlbSkelCheck", 0f) > 0f) GlbSkelCheck();

            var model = SkinnedCharacterFactory.BuildSelfContainedGlb(_assets, modelName, "Self", out _skeleton);
            if (model == null) { _status = "selfglb build failed: " + modelName; Debug.LogError("[selfglb] build failed: " + modelName); return; }

            var rends = model.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) { _status = "selfglb: no renderers"; return; }
            var b = rends[0].bounds; foreach (var r in rends) b.Encapsulate(r.bounds);
            float rad = Mathf.Max(0.1f, b.extents.magnitude);
            cam.transform.position = b.center + new Vector3(0f, b.extents.y * 0.15f, -rad * 2.4f);
            cam.transform.LookAt(b.center);
            cam.nearClipPlane = Mathf.Max(0.01f, rad / 100f);
            _status = $"selfglb '{modelName}': {rends.Length} renderers, size {b.size}";
            Debug.Log($"[selfglb] '{modelName}' {rends.Length} renderers bounds={b.size} bones={_skeleton?.Bones.Length}");
            _shotAt = Time.time + 1.5f;
        }

        // Numeric proof the glb-recovered skeleton matches the .xbp one: build both for the base male
        // (MBiped.XBP vs a_m_00_a.glb, converted from it) and log the worst per-bone world position and
        // rotation delta. ~0 means Skeleton.BuildFromGlb reconstructs the bind pose correctly.
        private void GlbSkelCheck()
        {
            try
            {
                var xbp = _assets.GetRawModel("MBiped.XBP");
                if (xbp == null) { Debug.Log("[SKELCHK] MBiped.XBP not found"); return; }
                var skA = Skeleton.Build(xbp, "cmpA");
                var glb = _assets.GlbBytes("a_m_00_a");
                if (glb == null) { Debug.Log("[SKELCHK] a_m_00_a.glb not found"); return; }
                var skB = Skeleton.BuildFromGlb(glb, "cmpB");
                if (skB == null) { Debug.Log("[SKELCHK] BuildFromGlb returned null"); return; }

                float maxPos = 0f, maxAng = 0f; string worstP = "", worstA = "";
                foreach (var kv in skA.ByName)
                {
                    if (!skB.ByName.TryGetValue(kv.Key, out int ib)) continue;
                    var ta = skA.Bones[kv.Value]; var tb = skB.Bones[ib];
                    float dp = Vector3.Distance(ta.position, tb.position);
                    float da = Quaternion.Angle(ta.rotation, tb.rotation);
                    if (dp > maxPos) { maxPos = dp; worstP = kv.Key; }
                    if (da > maxAng) { maxAng = da; worstA = kv.Key; }
                }
                Debug.Log($"[SKELCHK] bones A={skA.Bones.Length} B={skB.Bones.Length} | max pos delta={maxPos:0.0000}u @'{worstP}' | max rot delta={maxAng:0.00}deg @'{worstA}' (want ~0)");
                UnityEngine.Object.Destroy(skA.Root.gameObject);
                UnityEngine.Object.Destroy(skB.Root.gameObject);
            }
            catch (Exception ex) { Debug.LogError("[SKELCHK] " + ex); }
        }

        // ------------------------------------------------------------ character preview (no server)
        // Builds a fully dressed, idle-animated character to verify shoes + weapon model + weapon
        // aura offline. Equipment ids are auto-picked from item_table (first piece per slot that
        // actually has a model), overridable with -ndAmor/-ndPants/-ndCap/-ndShoes/-ndWeapon and
        // -ndWeaponModel; the aura grade with -ndWeaponGlow (default 8 so it is visible).
        private void BuildCharPreview()
        {
            _demoMode = true;
            _shotName = "nd_charpreview.png";   // own file: a concurrent live client writes nd_screenshot.png
            ClientData.Root = dataPath;
            _sex = (int)ArgF("-ndSex", 0f);
            _face = Mathf.Max(1, (int)ArgF("-ndFace", 1f));
            _hair = Mathf.Max(1, (int)ArgF("-ndHair", 1f));

            var cam = Camera.main;
            if (cam == null) cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
            cam.farClipPlane = 200f;
            if (ArgF("-ndNoPostFx", 0f) <= 0f) NdPostFx.Attach(cam);   // bloom + tone-map

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            sun.transform.rotation = Quaternion.Euler(48f, 150f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.55f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(2f, 1f, 2f);
            ground.GetComponent<Renderer>().sharedMaterial = MarkerMaterial(new Color(0.20f, 0.22f, 0.26f));
            Destroy(ground.GetComponent<Collider>());

            try { _assets = new AssetService(); _assets.Open(); }
            catch (Exception ex) { _status = "assets unavailable: " + ex.Message; Debug.LogError("[char-preview] " + ex); return; }

            // auto-pick a real, MODELLED outfit + weapon from the item table (fall back to args)
            int amor = 0, pants = 0, cap = 0, gloves = 0, shoes = 0, weapon = 0;
            if (_assets.Items != null)
            {
                foreach (var kv in _assets.Items.Clothes)
                {
                    var cl = kv.Value;
                    if (cl.ModelIndex <= 0) continue;                 // needs a body mesh
                    if (cl.SecondType == 0 && amor == 0) amor = kv.Key;
                    else if (cl.SecondType == 1 && pants == 0) pants = kv.Key;
                    else if (cl.SecondType == 2 && gloves == 0) gloves = kv.Key;
                    else if (cl.SecondType == 3 && shoes == 0) shoes = kv.Key;   // giày
                    else if (cl.SecondType == 4 && cap == 0) cap = kv.Key;
                    if (amor != 0 && pants != 0 && shoes != 0) break;   // the pieces we most want to see
                }
                // prefer a single-hand SHORT weapon (SecondType 0 — a sword/knife/club held cleanly in
                // one hand) whose sModelIndex maps to a mesh present in THIS build; fall back to any
                // weapon with a resolvable mesh. (Ids 1-12 and many high indices are placeholders with
                // ModelIndex 0 or absent meshes — those are skipped.)
                int fallbackWeapon = 0;
                foreach (var kv in _assets.Items.Weapons)
                {
                    if (kv.Value.ModelIndex <= 0) continue;
                    string m = _assets.WeaponMesh(_sex, kv.Key);
                    if (m == null || !_assets.HasModel(m)) continue;   // skip placeholders / absent meshes (glb hoặc .X3D)
                    if (fallbackWeapon == 0) fallbackWeapon = kv.Key;
                    if (kv.Value.SecondType == 0) { weapon = kv.Key; break; }    // SHORT = one-hand held blade
                }
                if (weapon == 0) weapon = fallbackWeapon;
            }
            _amor = (int)ArgF("-ndAmor", amor);
            _pants = (int)ArgF("-ndPants", pants);
            _cap = (int)ArgF("-ndCap", cap);
            _shoes = (int)ArgF("-ndShoes", shoes);
            _weapon = (int)ArgF("-ndWeapon", weapon);
            _weaponModelIndex = (int)ArgF("-ndWeaponModel", 0f);
            Debug.Log($"[char-preview] sex={_sex} amor={_amor} pants={_pants} cap={_cap} shoes={_shoes} weapon={_weapon} wmodel={_weaponModelIndex}");

            // a synthetic session (no connection) so WeaponMount can read Me.Weapon
            string lump = string.IsNullOrEmpty(lumpPath)
                ? Path.Combine(Application.streamingAssetsPath, "lump.dat") : lumpPath;
            try { _session = new GameSession(lump); }
            catch (Exception ex) { Debug.LogWarning("[char-preview] session: " + ex.Message); _session = null; }
            if (_session != null) { _session.Me.Weapon = _weapon; _session.Me.Shoes = _shoes; }

            // weapon refine aura uses the REAL equipped refine by default (0 = no forced preview); the
            // grade-8 preview looked wrong ("hào quang bậy") on unrefined gear, so it is opt-in via -ndWeaponGlow N.
            _weaponGlowDefault = (int)ArgF("-ndWeaponGlow", 0f);

            BuildPlayerMarker("Preview");

            // idle animation so the pose (and weapon grip) reads naturally
            if (_skeleton != null && ArgF("-ndNoAnim", 0f) <= 0f)
            {
                string p = _sex == 0 ? "Mz" : "Wz";
                var sa = _player.gameObject.AddComponent<SkeletalAnimator>();
                sa.Skel = _skeleton;
                // -ndPreviewIdle N: render a specific biped motion index as the idle (diagnose e.g. the
                // sword peace idle 103 for a lean). Default = the base breathing idle.
                int pidle = (int)ArgF("-ndPreviewIdle", -1f);
                string idleName = pidle >= 0 ? _assets.CharBiped(_sex).ClipFor(pidle) : $"{p}_a_x_bre_a.xa";
                string runName = $"{p}_a_x_run_a.xa";
                Debug.Log($"[char-preview] idle motion={pidle} clip={idleName}");
                sa.SetClips(_assets, idleName, runName);   // glTF overlay (gltf_anim) wins over .XA
            }

            // frame the character: camera slightly above centre, looking at the torso; face the camera
            _player.position = Vector3.zero;
            _player.rotation = Quaternion.LookRotation(Vector3.forward);   // front looks toward -Z (camera)
            var target = new Vector3(0f, 0.95f, 0f);
            cam.transform.position = target + new Vector3(0.35f, 0.15f, -2.6f);
            cam.transform.LookAt(target);

            // -ndFxTest <id>: spawn a skill-VFX template on the preview character and keep replaying it
            // (mesh effects fade over ~1.5s) so the screenshot catches it. Proves VisualEffect.xes parse
            // + EffectFactory end to end, offline.
            _fxTestId = (int)ArgF("-ndFxTest", 0f);
            if (_fxTestId != 0)
            {
                EffectFactory.Log = true;
                var t = _assets.Effects;   // force-load + log the table
                EffectFactory.Play(_assets, _fxTestId, _skeleton, _player);
                _fxNext = Time.time + 3.0f;
            }

            _status = "";
            _shotAt = Time.time + (_fxTestId != 0 ? 1.0f : (screenshotAfter > 0f ? Mathf.Min(screenshotAfter, 4f) : 4f));
            Debug.Log("[char-preview] built (offline equipment verify)");
        }


        // ------------------------------------------------------------ offline demo
        private void BuildPanelDemo()
        {
            _demoMode = true;
            _shotName = "nd_paneldemo.png";   // own file: a concurrent LIVE client writes nd_screenshot.png
            ClientData.Root = dataPath;

            // dark background so the overlay UI reads clearly in the screenshot
            var cam = Camera.main;
            if (cam != null) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = ArgF("-ndBrightBg", 0f) > 0f ? new Color(0.35f, 0.62f, 0.30f) : new Color(0.10f, 0.11f, 0.13f); }

            // real HUD/window art if the client data is present; null → styled fallbacks
            try { _assets = new AssetService(); _assets.Open(); }
            catch (Exception ex) { Debug.LogWarning("[NdGameClient] demo assets unavailable: " + ex.Message); _assets = null; }

            // a Session with no connection — lump.dat is loaded but Login/EnterWorld are never called
            string lump = string.IsNullOrEmpty(lumpPath)
                ? Path.Combine(Application.streamingAssetsPath, "lump.dat") : lumpPath;
            try { _session = new GameSession(lump); }
            catch (Exception ex) { Debug.LogWarning("[NdGameClient] demo session: " + ex.Message); _session = null; }

            if (_session != null)
            {
                // values copied 1:1 from the user's real Nhân-vật reference screenshot (ui_ref/nhan vat.png)
                var me = _session.Me;
                me.Name = "Aladdin";
                me.Class = 5; me.ClassGrade = 11;
                // InnerLevel drives the grade line: 239 → grade 20 ("Thiên Ngoại Tiên"), sublevel 11.
                // Leave GradeName empty so it resolves LIVE from the string DB (proves the wire path).
                me.InnerLevel = 239; me.NickTitle = "vô danh tiểu tốt";
                // CHARAC_INFO carries cur + the STALE BASE max (this is the cur>max bug seen live).
                me.CurLife = 5642; me.MaxLife = 980;
                me.CurForce = 3599; me.MaxForce = 300;
                me.CurConc = 300;  me.MaxConc = 300;
                // op210 sub13 MAXSTAT push (server-authoritative, cash-folded) → the real max.
                // After this the HUD reads 5642/5656 and 3599/3599 (cur ≤ max), not 5642/980.
                me.ApplyMaxStatPush(new[] { 5656, 3599, 3599 });
                me.Honor = 12840; me.LeftPoint = 0; me.LeftMasteryPoint = 0;
                me.WoundRate = 0; me.InsideWoundRate = 0;
                me.Contribution = 0; me.Retribution = 0; me.FuryParameter = 5200;
                // op210 sub16: def mdef avoid mavoid atkRate matkRate crit mcrit critAvoid minDmg maxDmg mMinDmg mMaxDmg moveSpeed
                me.ApplyStatPush16(new[] { 319, 255, 453, 181, 475, 189, 0, 0, 0, 107, 214, 384, 384, 100 });
                // op210 sub17: str con zen int dex yin yang soul spirit (CL=Str20 SL=Con49 CK=Zen20 TT=Int16 MT=Dex76)
                me.ApplyStatPush17(new[] { 20, 49, 20, 16, 76, 0, 0, 0, 0 });
                _level = 239;
                _session.MySect = 5;   // Võ Đang
                if (_assets != null) _session.Skills = _assets.Skills;
                // learned martial arts matching the real client sample (names resolve from the
                // Vietnamese string DB, section Skill_Table_Name): 1002=Ngọc Huyền Quy Chân Tâm Pháp,
                // 2=Thiên hạ tam quyền, 1=Dưỡng Sinh Đạo Dẫn Thuật, 5030=Lưu vân thân pháp (khinh công)
                _session.LearnedSkills.Add(new GameSession.LearnedSkill { Slot = 0, Id = 1002, Step = 2, Exp = 4100, State = 0 });
                _session.LearnedSkills.Add(new GameSession.LearnedSkill { Slot = 1, Id = 2,    Step = 6, Exp = 900,  State = 0 });
                _session.LearnedSkills.Add(new GameSession.LearnedSkill { Slot = 2, Id = 1,    Step = 9, Exp = 0,    State = 0 });
                _session.LearnedSkills.Add(new GameSession.LearnedSkill { Slot = 3, Id = 5030, Step = 9, Exp = 0,    State = 0 });
                // backpack items + money so the Hành-trang (I) window shows content. Use REAL ids from
                // item_table so the slots render real Item_*.tga icons. Fill ALL 5 bags × 25 = 125 slots
                // (m_UserItemList 25..149) so every bag page is full (chủ: "5 hành trang, mỗi hành trang
                // 25 ô đều chứa vật phẩm"). Items are round-robined across categories → each bag shows a mix.
                me.Money = 1234567;
                SeedFullInventory(me);
                // unlock ALL 4 extra bags so the full 5-tab row is verifiable offline: NPC1/2 (slot 23/24)
                // + cash1/2 (slot 21/22) → 5 tabs.
                me.Inventory.Add(new Nd.Packets.InvenItem { Slot = 23, Type = 8, Id = 1, Count = 1 });
                me.Inventory.Add(new Nd.Packets.InvenItem { Slot = 24, Type = 8, Id = 1, Count = 1 });
                me.Inventory.Add(new Nd.Packets.InvenItem { Slot = 21, Type = 8, Id = 1, Count = 1 });
                me.Inventory.Add(new Nd.Packets.InvenItem { Slot = 22, Type = 8, Id = 1, Count = 1 });
                // real worn gear (weapon + upper/lower/cap/gloves/shoes cloth) so the doll shows real icons.
                // cloth SecondType: 0 upper, 1 lower, 2 gloves(아대), 3 shoes(giày), 4 cap
                if (_assets != null && _assets.Items != null)
                {
                    foreach (var wid in _assets.Items.Weapons.Keys) { me.Weapon = wid; break; }
                    // pick the first item PER SLOT that actually has an icon (IconNo>0); many low-id
                    // pieces are icon-less placeholders (e.g. cap 1081 → iconNo 0 → blank cell).
                    foreach (var kv in _assets.Items.Clothes)
                    {
                        if (kv.Value.IconNo <= 0) continue;
                        var st = kv.Value.SecondType;
                        if (st == 0 && me.Amor == 0) me.Amor = kv.Key;
                        else if (st == 1 && me.Pants == 0) me.Pants = kv.Key;
                        else if (st == 2 && me.Gloves == 0) me.Gloves = kv.Key;
                        else if (st == 3 && me.Shoes == 0) me.Shoes = kv.Key;
                        else if (st == 4 && me.Cap == 0) me.Cap = kv.Key;
                        if (me.Amor != 0 && me.Pants != 0 && me.Cap != 0 && me.Gloves != 0 && me.Shoes != 0) break;
                    }
                }
            }

            // seed a few entities around the player so the minimap radar shows blips
            // (players=white, monsters=red, NPCs=yellow) and the zoom control is verifiable.
            _session.Me.X = 0; _session.Me.Z = 0;
            _session.World.Players[9001] = new Nd.Session.PlayerEntity { Uid = 9001, X = 60, Z = 40, Name = "Bằng hữu" };
            _session.World.Players[9002] = new Nd.Session.PlayerEntity { Uid = 9002, X = -80, Z = 90, Name = "Đồng đạo" };
            _session.World.Monsters[9101] = new Nd.Session.MonsterEntity { Id = 9101, X = 110, Z = -70, Alive = true };
            _session.World.Monsters[9102] = new Nd.Session.MonsterEntity { Id = 9102, X = -50, Z = -110, Alive = true };
            _session.World.Monsters[9103] = new Nd.Session.MonsterEntity { Id = 9103, X = 150, Z = 20, Alive = true };

            // wire the sound archive so SFX (incl. window open/close) work in the demo path too
            if (_assets != null) AudioService.SetAssets(_assets);

            var hud = new GameObject("HUD").AddComponent<GameHud>();
            hud.Session = _session;
            hud.Assets = _assets;
            hud.MapName = "map_demo";
            hud.Level = _level;
            hud.MinimapNpcs.Add(new Vector2(-30, 30));    // NPC (yellow) — shopkeeper
            hud.MinimapNpcs.Add(new Vector2(70, -20));     // NPC (yellow) — quest giver
            // real minimap art if a us_Minimap_<base>.tga is present (else the dark radar ground)
            if (_assets != null)
            {
                var mm = _assets.GetTexture("us_Minimap_hanater.tga") ?? _assets.GetTexture("us_Minimap_bamboo.tga");
                if (mm != null && mm.width > 4) hud.MinimapTex = mm;
            }

            _status = "";
            _shotAt = Time.time + (screenshotAfter > 0f ? Mathf.Min(screenshotAfter, 4f) : 4f);
            Debug.Log("[NdGameClient] panel-demo built (offline layout verify)");
        }

        // Fill every bag slot (m_UserItemList 25..149 = 5 bags × 25) with REAL item-table items that have a
        // resolvable icon, round-robined across categories so each bag page shows a full, varied grid. Type
        // codes are the 0-based _XGI_FC used by AssetService.ItemIcon (0 weapon, 1 cloth, 9 accessory,
        // 3 potion, 4 consuming, 5 elixir, 7 resource, 8 quest). Count varies so stack numbers are exercised.
        private void SeedFullInventory(Nd.Session.MyCharacter me)
        {
            const int Start = 25, End = 149;   // POCKETSTART .. last cash-bag slot (150 total, 0..24 reserved)
            var items = _assets != null ? _assets.Items : null;
            if (items == null)
            {
                // no item table → at least fill the pocket with a couple of visible stacks
                for (int s = Start; s <= Start + 24; s++)
                    me.Inventory.Add(new Nd.Packets.InvenItem { Slot = (byte)s, Type = 5, Id = (ushort)(3001 + (s & 1)), Count = (byte)(s % 90 + 1) });
                return;
            }

            // per-category queues, KEEPING only ids whose icon actually resolves (so no blank cells)
            var cats = new (int type, System.Collections.Generic.IEnumerable<int> ids, byte count)[]
            {
                (0, items.Weapons.Keys,     1),   // weapon (Item_Wea_*)
                (1, items.Clothes.Keys,     1),   // cloth  (Item_Clo_*)
                (9, items.Accessories.Keys, 1),   // accessory (Item_Acc_*)
                (3, items.Potions.Keys,    20),   // potion (Item_pot_*)
                (4, items.Consuming.Keys,   5),   // consuming (Item_Con_*)
                (5, items.Elixir.Keys,      3),   // elixir (ITEM_ELIXIR_*)
                (7, items.Resource.Keys,   99),   // resource (Item_Res_*)
                (8, items.Quest.Keys,       1),   // quest (Item_que_*)
            };
            var queues = new System.Collections.Generic.List<System.Collections.Generic.Queue<Nd.Packets.InvenItem>>();
            foreach (var c in cats)
            {
                var q = new System.Collections.Generic.Queue<Nd.Packets.InvenItem>();
                foreach (var id in c.ids)
                {
                    if (_assets.ItemIcon(c.type, id) == null) continue;   // only items that render an icon
                    q.Enqueue(new Nd.Packets.InvenItem { Type = (byte)c.type, Id = (ushort)id, Count = c.count });
                }
                if (q.Count > 0) queues.Add(q);
            }

            // round-robin across categories → each bag gets a mix; stop when a slot cannot be filled
            int slot = Start, ci = 0;
            while (slot <= End && queues.Count > 0)
            {
                int qi = ci % queues.Count;
                var q = queues[qi];
                if (q.Count == 0) { queues.RemoveAt(qi); continue; }
                var src = q.Dequeue();
                me.Inventory.Add(new Nd.Packets.InvenItem { Slot = (byte)slot++, Type = src.Type, Id = src.Id, Count = src.Count });
                ci++;
            }
        }

        // ------------------------------------------------------------ nameplate demo (no server)
        private void BuildNameplateDemo()
        {
            _demoMode = true;
            ClientData.Root = dataPath;
            _level = 50;   // reference player level; mob names colour by (mobLevel - 50)

            var cam = Camera.main;
            if (cam == null) cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.14f, 0.20f, 0.16f);
            cam.farClipPlane = 500f;
            cam.transform.position = new Vector3(-1.5f, 3.2f, -13f);
            cam.transform.rotation = Quaternion.Euler(8f, 0f, 0f);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(6f, 1f, 6f);
            ground.GetComponent<Renderer>().sharedMaterial = MarkerMaterial(new Color(0.22f, 0.28f, 0.22f));
            Destroy(ground.GetComponent<Collider>());

            // real HUD art (mi_party.tga HP bar) + localized mob names if the client data is present
            try { _assets = new AssetService(); _assets.Open(); }
            catch (Exception ex) { Debug.LogWarning("[nameplate-demo] assets unavailable: " + ex.Message); _assets = null; }

            // one fake mob per colour band (level relative to the player's 50), varied vital
            // raws chosen for short localized names so all seven colour bands read cleanly
            var bands = new (int lvl, int vital, int raw)[]
            {
                (56, 100, 1), (53, 85, 4), (51, 70, 5), (50, 55, 6),
                (49, 40, 8), (47, 25, 9), (44, 10, 2),
            };
            const float spacing = 2.0f;
            float x0 = -(bands.Length - 1) * spacing * 0.5f;

            // player marker (far left of the mob row) with a title+name plate
            var pcap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            pcap.name = "DemoPlayer";
            pcap.transform.position = new Vector3(x0 - 2.6f, 1f, 0f);
            pcap.GetComponent<Renderer>().sharedMaterial = MarkerMaterial(new Color(0.35f, 0.55f, 0.85f));
            Destroy(pcap.GetComponent<Collider>());
            AddPlayerNameplate(pcap.transform, "Aladdin", "Thiên Ngoại Tiên");

            int uid = 2000;
            void SpawnMob(float x, float z, int lvl, int vital, int raw)
            {
                var cap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                cap.name = $"DemoMob_{raw}_{z:0}";
                cap.transform.position = new Vector3(x, 1f, z);
                cap.transform.localScale = new Vector3(0.7f, 0.9f, 0.7f);
                cap.GetComponent<Renderer>().sharedMaterial = MarkerMaterial(new Color(0.5f, 0.32f, 0.3f));
                Destroy(cap.GetComponent<Collider>());
                var ent = new Nd.Session.MonsterEntity
                {
                    Id = (ushort)(uid++), WireType = (ushort)(4000 + raw),
                    Level = lvl, VitalRate = (sbyte)vital, Alive = true,
                };
                AddMobNameplate(cap.transform, ent);
            }

            // near row (z=0): the seven colour bands
            for (int i = 0; i < bands.Length; i++)
                SpawnMob(x0 + i * spacing, 0f, bands[i].lvl, bands[i].vital, bands[i].raw);

            // FAR row (z=42): same nameplates far behind — proof that the screen text stays the SAME
            // pixel size regardless of distance (fixed-pixel, not distance-scaled).
            for (int i = 0; i < 4; i++)
                SpawnMob(x0 + i * spacing, 42f, bands[i].lvl, bands[i].vital, bands[i].raw);

            _status = "";
            _shotAt = Time.time + (screenshotAfter > 0f ? Mathf.Min(screenshotAfter, 4f) : 4f);
            Debug.Log("[NdGameClient] nameplate-demo built (offline nameplate verify)");
        }

        // ------------------------------------------------------------ target-frame demo (no server)
        // Fabricates a normal / boss / quest mob (preferring REAL monster_ref.bmo types so names,
        // party glyph, rank and level come from the live data) and hands them to the HUD's
        // TargetFrame in -ndTargetDemo row mode, so all three frame variants render in one screenshot.
        private void BuildTargetDemo()
        {
            _demoMode = true;
            ClientData.Root = dataPath;

            var cam = Camera.main;
            if (cam == null) cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.14f, 0.17f);

            try { _assets = new AssetService(); _assets.Open(); }
            catch (Exception ex) { Debug.LogWarning("[target-demo] assets unavailable: " + ex.Message); _assets = null; }

            string lump = string.IsNullOrEmpty(lumpPath)
                ? Path.Combine(Application.streamingAssetsPath, "lump.dat") : lumpPath;
            try { _session = new GameSession(lump, _assets != null ? _assets.Monsters : null); }
            catch (Exception ex) { Debug.LogWarning("[target-demo] session: " + ex.Message); _session = new GameSession(lump); }

            _session.Me.Name = "Aladdin";
            _session.Me.InnerLevel = 50;   // reference level for the mob-level chevron
            _level = 50;

            // pick real normal-beast / boss / quest raw types (name must resolve) from monster_ref.bmo
            int normalRaw = 0, bossRaw = 0, questRaw = 0;
            var tbl = _assets != null ? _assets.Monsters : null;
            if (tbl != null)
            {
                foreach (var kv in tbl.Monsters)
                {
                    var r = kv.Value;
                    if (string.IsNullOrEmpty(_assets.MobName(r.RawId))) continue;   // need a display name
                    if (questRaw == 0 && r.Rank == 1) questRaw = r.RawId;
                    else if (bossRaw == 0 && (r.Rank == 3 || r.Rank == 4 || r.Rank == 8)) bossRaw = r.RawId;
                    else if (normalRaw == 0 && r.Rank == 0 && r.Party >= 11 && r.Party <= 16) normalRaw = r.RawId;
                    if (normalRaw != 0 && bossRaw != 0 && questRaw != 0) break;
                }
                if (normalRaw == 0)   // relax the beast-party constraint if none matched
                    foreach (var kv in tbl.Monsters)
                    { var r = kv.Value; if (r.Rank == 0 && !string.IsNullOrEmpty(_assets.MobName(r.RawId))) { normalRaw = r.RawId; break; } }
            }

            void Add(ushort id, int raw, int vital, int fbLevel, int fbParty, int fbRank, int fbAttr)
            {
                var ent = new Nd.Session.MonsterEntity
                { Id = id, WireType = (ushort)(4000 + raw), VitalRate = (sbyte)vital, Alive = true };
                ent.Enrich(tbl);                       // real Level/Party/Rank/Attribute for a real raw
                if (raw == 0 || ent.Level == 0)        // synthetic fallback so the variant still shows
                { ent.Level = fbLevel; ent.Party = (sbyte)fbParty; ent.Rank = (sbyte)fbRank; ent.Attribute = (byte)fbAttr; }
                _session.World.Monsters[id] = ent;
                Debug.Log($"[target-demo] mob {id} raw={ent.RawType} name='{(_assets != null ? _assets.MobName(ent.RawType) : "")}' " +
                          $"lv={ent.Level} party={ent.Party} rank={ent.Rank} attr={ent.Attribute} vital={ent.VitalRate}");
            }
            Add(5001, normalRaw, 100, 48, 12, 0, 0);   // normal beast  (獸 glyph, shows "(Lv N)")
            Add(5002, bossRaw,    70, 80,  8, 3, 2);   // boss           (boss plate, no level)
            Add(5003, questRaw,   40, 55,  8, 1, 4);   // quest / named  (quest deco, no level)

            if (_assets != null) AudioService.SetAssets(_assets);
            var hud = new GameObject("HUD").AddComponent<GameHud>();
            hud.Session = _session; hud.Assets = _assets; hud.MapName = "target_demo"; hud.Level = _level;

            _status = "";
            _shotAt = Time.time + (screenshotAfter > 0f ? Mathf.Min(screenshotAfter, 4f) : 4f);
            Debug.Log("[NdGameClient] target-demo built (offline target-frame verify)");
        }

        private void RunLoginAndBuild()
        {
            string lump = string.IsNullOrEmpty(lumpPath)
                ? Path.Combine(Application.streamingAssetsPath, "lump.dat") : lumpPath;

            ClientData.Root = dataPath;
            _assets = new AssetService();
            _assets.Open();

            // pass the monster_ref.bmo table so MonsterEntity.Enrich fills each mob's real Level —
            // without it every mob has Level 0 and the nameplate level-colour is wrong (all "blue").
            _session = new GameSession(lump, _assets.Monsters) { Log = m => Debug.Log("[net] " + m) };

            // Command-line overrides so ONE build can point at localhost (WSL) OR the VPS without a
            // rebuild: -ndHost <ip> -ndLoginPort <p> -ndGsPort <p> -ndAccount <a> -ndPassword <pw> -ndChar <name>.
            // (Trước đây host="127.0.0.1" bị nướng cứng vào build → chỉ nối được localhost.)
            { var v = ArgS("-ndHost");     if (!string.IsNullOrEmpty(v)) host = v; }
            { var v = ArgS("-ndAccount");  if (!string.IsNullOrEmpty(v)) account = v; }
            { var v = ArgS("-ndPassword"); if (!string.IsNullOrEmpty(v)) password = v; }
            { var v = ArgS("-ndChar");     if (!string.IsNullOrEmpty(v)) characterName = v; }
            loginPort = (int)ArgF("-ndLoginPort", loginPort);
            gsPort    = (int)ArgF("-ndGsPort", gsPort);

            _status = $"connecting to {host}:{loginPort}...";
            _session.Connect(host, loginPort, gsPort, account, password);

            var list = _session.RequestCharacterList();
            if (list.Count == 0)
            {
                // KHÔNG throw (trước đây đóng/"FAILED" khó hiểu): account chưa có nhân vật TRÊN SERVER VỪA NỐI.
                // Thường là do nối nhầm server (localhost thay vì VPS) hoặc server đó chưa tạo char cho tài khoản.
                _status = $"Tài khoản '{account}' chưa có nhân vật trên server {host}:{gsPort}.\n" +
                          "→ Kiểm tra: trỏ -ndHost sang đúng server (VPS), hoặc tạo nhân vật cho tài khoản này trên server đó.";
                Debug.LogWarning("[NdGameClient] " + _status);
                return;
            }
            var entry = list.Entries[0];
            foreach (var e in list.Entries)
                if (e.Name == characterName) { entry = e; break; }
            string sel = entry.Name;
            _sex = entry.Sex; _face = entry.Face; _hair = entry.Hair; _level = entry.InnerLevel;
            _amor = entry.Amor; _pants = entry.Pants; _cap = entry.Cap; _weapon = entry.Weapon; _weaponType = entry.WeaponType;
            _shoes = entry.Shoes;                        // giày — worn shoes item (LFOOT/RFOOT cluster)
            _weapon = (int)ArgF("-ndWeapon", _weapon);   // debug: force a weapon id to preview the held mesh
            _shoes = (int)ArgF("-ndShoes", _shoes);      // debug: force a shoes id to preview the foot mesh
            _level = (int)ArgF("-ndPlayerLevel", _level);   // debug: override the player level to test mob name colours live
            // mid-session level-up: refresh the reference level the nameplate colour path reads, so mob
            // name colours update without a relog (LEVEL_UP_RESULT 106).
            _session.LevelChanged += lvl => { _level = lvl; Debug.Log($"[NdGameClient] level-up -> inner level {lvl} (nameplate colours refreshed)"); };
            // Mob đánh mình (op50) → quay mặt nó vào char trong ~3s; combat đánh liên tục nên timer cứ
            // được làm mới → mob luôn hướng vào char khi đang chiến (giống client thật).
            _session.MonsterAttacked += (mid, atk) => { _mobFaceUntil[mid] = Time.time + MobFaceHold; };
            _session.MonsterSkill    += (mid, sk)  => { _mobFaceUntil[mid] = Time.time + MobFaceHold; };
            // bị player đánh trúng → quay mặt vào player (con đang quay lưng bị đánh sẽ xoay lại đối mặt).
            _session.MonsterDamaged  += (mid, dmg) => { if (dmg > 0) _mobFaceUntil[mid] = Time.time + MobFaceHold; };

            _status = $"selecting '{sel}'...";
            _session.SelectCharacter(sel);
            _session.MySect = entry.Group;   // sect / môn phái for skill-table school gating (not in CHARAC_INFO)
            _session.Me.ApplySelectEntry(entry);   // 5 base attributes (Str/Zen/Int/Con/Dex) — from the char-list row
            var me = _session.Me;
            Debug.Log($"[NdGameClient] '{me.Name}' zone={me.Zone} pos=({me.X:0.##},{me.Z:0.##}) " +
                      $"HP {me.CurLife}/{me.MaxLife}");

            // zone → map
            var zones = _assets.LoadZoneTable();
            string mapName = zones.MapNameFor(me.Zone)
                ?? throw new InvalidOperationException($"no map for zone {me.Zone}");
            _mapName = mapName;

            // zone background music — persistent singleton, looping, crossfaded on map change
            AudioService.PlayForMap(_assets, mapName);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _status = $"building {mapName}...";
            var result = MapBuilder.Build(_assets, mapName, terrainColliders: true);
            _terrain = result.Terrain;
            _mapRoot = result.Root;
            _weather = result.Weather;
            WorldNameplate.OccluderTerrain = result.Terrain;   // nameplates hide behind hills, not through them
            Debug.Log($"[TIME] MapBuilder.Build {sw.ElapsedMilliseconds}ms — {result}"); sw.Restart();

            _status = "entering world...";
            _session.EnterWorld();
            _session.Pump(1500);      // MONSTER_EXIST burst etc.
            Debug.Log($"[TIME] EnterWorld+Pump {sw.ElapsedMilliseconds}ms"); sw.Restart();

            int wmi = (int)ArgF("-ndWeaponModel", 0f);   // debug: force a WEAPON cluster index
            Debug.Log($"[EQUIP] amor={_amor} pants={_pants} cap={_cap} weapon={_weapon} sex={_sex} " +
                      $"weaponMesh={(wmi > 0 ? _assets.WeaponMeshByModelIndex(_sex, wmi) : _assets.WeaponMesh(_sex, _weapon)) ?? "(none)"}");
            _weaponModelIndex = wmi;
            BuildPlayerMarker(me.Name);
            Debug.Log($"[TIME] BuildPlayer {sw.ElapsedMilliseconds}ms"); sw.Restart();
            _mobRoot = new GameObject("LiveEntities").transform;
            SyncEntities();
            Debug.Log($"[TIME] SyncEntities {sw.ElapsedMilliseconds}ms ({_session.Monsters.Count} mobs)"); sw.Restart();
            SetupCameraAndControl();
            Debug.Log($"[TIME] SetupCamera {sw.ElapsedMilliseconds}ms");

            _builtZone = me.Zone;
            _session.InGame = true;   // arm the vé-di-chuyển teleport reload (login CHARAC_INFO is now past)

            _status = $"{me.Name} in {mapName} — {_session.Monsters.Count} mobs streamed";
            if (screenshotAfter > 0f) _shotAt = Time.time + screenshotAfter;

            // close inspection view: hard-place a fixed camera in front of the character (no
            // TpsCamera pull-in, so it never buries in nearby cliffs) to see arms/legs clearly.
            if (ArgF("-ndInspect", 0f) > 0f)
            {
                var cam0 = Camera.main;
                var tps = cam0.GetComponent<TpsCamera>();
                if (tps != null) tps.enabled = false;
                if (_control != null) _control.enabled = false;   // freeze the character
                if (_control != null) _control.enabled = false;
                _player.rotation = Quaternion.LookRotation(Vector3.forward);   // mesh faces -forward → its front looks toward -Z
                var target = _player.position + Vector3.up * 0.9f;
                cam0.transform.position = target + new Vector3(0.15f, 0.25f, -3.0f);  // camera at -Z → sees the character's front
                cam0.transform.LookAt(target);
            }

            _startPos = _player.position;
            if (autoWalk > 0f)
                _control.SetDestination(_startPos + new Vector3(autoWalk, 0f, autoWalk));

            // debug: exercise the exact click->camera-ray->terrain-raycast path a mouse click uses,
            // to prove click-to-move works in the built player (autowalk skips the raycast).
            if (ArgF("-ndTestClick", 0f) > 0f)
            {
                var cam = Camera.main;
                var ray = cam.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.35f, 0f));
                bool hit = Physics.Raycast(ray, out var h, 5000f);
                Debug.Log($"[TESTCLICK] screen={Screen.width}x{Screen.height} camPos={cam.transform.position} " +
                          $"rayHit={hit} point={(hit ? h.point.ToString("0.0") : "-")} " +
                          $"collider={(hit ? h.collider.name : "-")}");
                if (hit) { _control.SetDestination(h.point); Debug.Log("[TESTCLICK] SetDestination -> moving"); }
            }

            // debug: relocate the character to the monster cluster so a screenshot shows the
            // character among mobs/NPCs/scenery (visual check only, not a server move)
            if (ArgF("-ndCheckMobs", 0f) > 0f) RelocateToMobs();

            // debug: inject a fake other-player (appearance = mine) next to me, since the solo
            // nd-world server never broadcasts op27 — exercises the other-player render path
            if (ArgF("-ndFakePlayer", 0f) > 0f)
            {
                var m2 = _session.Me;
                _session.World.Players[(ushort)64000] = new Nd.Session.PlayerEntity
                {
                    Uid = 64000, HasAppearance = true, Name = "TestPlayer",
                    Sex = _sex, Face = _face, Hair = _hair,
                    Amor = _amor, Pants = _pants, Cap = _cap, Weapon = 0,
                    X = m2.X + 2.5f, Z = m2.Z + 1.5f, GotoX = m2.X, GotoZ = m2.Z
                };
            }
        }

        /// <summary>Unlit tinted material that survives build stripping (primitive default
        /// materials use the Standard shader, which is stripped from this build).</summary>
        private static Material MarkerMaterial(Color c)
        {
            var m = new Material(NdShaders.ObjectCutout) { mainTexture = Texture2D.whiteTexture };
            m.SetColor("_Tint", c);
            return m;
        }

        private Skeleton _skeleton;

        private void BuildPlayerMarker(string name)
        {
            // real skinned + skeletal-animated character by default; static parts if no skeleton
            SkinnedCharacterFactory.LogParts = ArgF("-ndLogParts", 0f) > 0f;
            SkinnedCharacterFactory.NoSkin = ArgF("-ndNoSkin", 0f) > 0f;
            SkeletalAnimator.QuatMode = (int)ArgF("-ndQuat", 0f);   // 0=conjugate; 1=raw (debug)
            SkeletalAnimator.PrependBind = ArgF("-ndNoPrepend", 0f) <= 0f;
            SkeletalAnimator.Play = ArgF("-ndNoAnim", 0f) <= 0f;   // default ON: idle/walk .XA (frame-0 prepend bug fixed); -ndNoAnim = static bind pose
            SkinnedCharacterFactory.LogAnim = ArgF("-ndLogAnim", 0f) > 0f;   // log why NPCs/mobs fail to animate
            EntityMover.LogMove = ArgF("-ndLogMove", 0f) > 0f;               // log per-packet mob drift/snap (smoothness check)
            PlayerController.AutoAttack = ArgF("-ndAutoAttack", 0f) > 0f;     // debug: auto-attack nearest mob
            PlayerController.TestNpc = ArgF("-ndTestNpc", 0f) > 0f;           // verify: walk to nearest NPC + open its dialog
            PlayerController.TestKhinhCong = ArgF("-ndTestKhinhCong", 0f) > 0f; // verify: auto khinh công run then release (lean check)
            bool naked = ArgF("-ndNaked", 0f) > 0f;   // diagnostic: base body only, no equipment
            // worn costume/deco (ngoại trang) = the inventory item in the MASK equip slot (op121 slot 7,
            // nd-world main.cpp:1200). -ndNoDeco disables it. 0 = no costume → normal outfit.
            int maskId = (naked || ArgF("-ndNoDeco", 0f) > 0f) ? 0 : WornItemIdAtSlot(7);
            GameObject model = null;
            if (ArgF("-ndStatic", 0f) <= 0f)
                model = SkinnedCharacterFactory.BuildPlayer(_assets, _sex, _face, _hair, name, out _skeleton,
                                                            naked ? 0 : _amor, naked ? 0 : _pants,
                                                            naked ? 0 : _cap, naked ? 0 : _weapon,
                                                            naked ? 0 : _weaponModelIndex, naked ? 0 : _shoes, maskId);
            if (model == null)
                model = CharacterFactory.BuildPlayer(_assets, _sex, _face, _hair, name);
            model.name = "Player_" + name;
            _player = model.transform;

            // held weapon + enchant aura: mount the weapon mesh on the shared skeleton (hand-weapon
            // bones bo05/bo07) and light the refine glow, both OUTSIDE the body assembly (WeaponMount).
            if (!naked && _skeleton != null && ArgF("-ndStatic", 0f) <= 0f)
            {
                bool wlog = ArgF("-ndLogParts", 0f) > 0f;
                WeaponMount.SubstituteAbsent = ArgF("-ndNoWeaponSub", 0f) <= 0f;   // data-gap fallback (default on)
                WeaponMount.MountWeapon(_player, _skeleton, _assets, _sex, _weapon, _weaponModelIndex, wlog, _weaponType);
                if (_weapon > 0 && WeaponMount.FindWeaponRenderer(_player) == null)
                    WeaponMount.MountAnyPresentWeapon(_player, _skeleton, _assets, _sex);   // spawn: never bare-handed if the mesh won't skin
                int glow = (int)ArgF("-ndWeaponGlow", _weaponGlowDefault);   // debug: force an enchant grade to preview the aura
                WeaponMount.Apply(_player, _skeleton, _session != null ? _session.Me : null, _weapon, glow, wlog, _assets, _weaponType);
                // cloth refine aura (áo tinh luyện) — the real VISUALEFFECT.xes glows per worn cloth slot.
                // real cloth refine by default (0 = no forced preview); -ndClothGlow N previews level N.
                int clothGlow = (int)ArgF("-ndClothGlow", 0f);
                ClothGlow.Apply(_assets, _player, _skeleton, _session != null ? _session.Me : null, wlog, clothGlow);
            }

            // player's own floating nameplate: the client draws the local name in light grey
            // (XUser.cpp:8865 ARGB 230,230,230) with the nickname/title line above it.
            AddPlayerNameplate(_player, name, _session != null ? _session.Me.NickTitle : null);
            UpdateMarker(_player, _session.Me.X, _session.Me.Z);   // initial spot; controller owns it after
        }

        /// <summary>Item id worn in an inventory equip slot (op121 slots 0-24 are the worn equipment:
        /// armor0..cap4 / mask7 / weapon10 — nd-world main.cpp:1200). 0 when nothing is worn there or
        /// the inventory has not arrived.</summary>
        private int WornItemIdAtSlot(int slot)
        {
            var me = _session != null ? _session.Me : null;
            if (me == null) return 0;
            foreach (var it in me.Inventory)
                if (it != null && it.Slot == slot && it.Id > 0) return it.Id;
            return 0;
        }

        /// <summary>Head height above an entity root from its renderer bounds (fallback ≈ capsule).</summary>
        private static float MeasureHeadHeight(Transform root, float fallback)
        {
            float top = float.NegativeInfinity;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
                if (r.bounds.max.y > top) top = r.bounds.max.y;
            if (float.IsNegativeInfinity(top)) return fallback;
            return Mathf.Clamp(top - root.position.y + 0.35f, 1.0f, 6.5f);
        }

        /// <summary>The persistent over-the-head nameplate the real client draws (name coloured by
        /// the level gap to the player + a mini HP bar), matching XMob.cpp:1250-1304. Replaces the
        /// old plain floating TextMesh. Reads the mob's live level/vital/alive each frame.</summary>
        private void AddMobNameplate(Transform root, Nd.Session.MonsterEntity mob)
        {
            var np = root.gameObject.AddComponent<WorldNameplate>();
            float headY = MeasureHeadHeight(root, 2.2f);
            string mname = _assets != null ? _assets.MobName(mob.RawType) : "";
            if (string.IsNullOrEmpty(mname)) mname = "#" + mob.RawType;
            Texture2D back = null, fill = null;
            if (_assets != null) _assets.MobHpBarArt(out back, out fill);
            np.SetupMob(root, headY, mname,
                        () => mob.Level, () => _level,
                        () => mob.VitalRate, () => mob.Alive,
                        back, fill);
            if (_npLogged < 16)
            {
                _npLogged++;
                int d = mob.Level - _level;
                string band = d > 4 ? "red" : d > 2 ? "orange" : d > 0 ? "yellow" : d == 0 ? "white"
                            : d >= -2 ? "green" : d >= -4 ? "lightblue" : "blue";
                Debug.Log($"[NAMEPLATE] '{mname}' raw={mob.RawType} mobLv={mob.Level} playerLv={_level} diff={d} -> {band}");
            }
        }

        /// <summary>A title+name nameplate for the player / another player / an NPC (no HP bar).</summary>
        private void AddPlayerNameplate(Transform body, string name, string title)
        {
            var np = body.gameObject.AddComponent<WorldNameplate>();
            float headY = MeasureHeadHeight(body, 1.8f);
            np.SetupSimple(body, headY, name, WorldNameplate.PlayerName, title);
        }

        /// <summary>Visual check: snap the player to the densest monster cluster.</summary>
        private void RelocateToMobs()
        {
            if (_session.Monsters.Count == 0) return;
            float sx = 0, sz = 0; int n = 0;
            foreach (var kv in _session.Monsters) { sx += kv.Value.X; sz += kv.Value.Z; n++; }
            float cx = sx / n, cz = sz / n;
            var p = new Vector3(cx, 0f, cz);
            p.y = TerrainFactory.HeightAt(_terrain, cx, cz);
            _player.position = p;
            _control.enabled = false;   // stop the controller fighting the manual move
            Debug.Log($"[NdGameClient] relocated to mob cluster ({cx:0.#},{cz:0.#}) of {n} mobs");
        }

        private void UpdateMarker(Transform t, float x, float z)
        {
            float y = _terrain != null ? TerrainFactory.HeightAt(_terrain, x, z) : 0f;
            t.position = new Vector3(x, y + 1f, z);   // capsule pivot at its center
        }

        /// <summary>Place a feet-origin entity on the ground, walking smoothly toward its server
        /// DESTINATION (gotoX,gotoZ) at its own move speed. XZ interpolation + facing are owned by
        /// <see cref="EntityMover"/> (faithful to the C++ _XMob::MoveNextPostion); the entity keeps
        /// advancing between the sparse MONSTER_MOVE packets instead of snapping to each server
        /// position and stuttering. The terrain-Y grounding call is unchanged — only the XZ it is
        /// sampled at follows the interpolated position (owned here, not the terrain agent).</summary>
        private void PlaceEntity(Transform t, float x, float z, float gotoX, float gotoZ, float speed,
                                 Vector3? faceTarget = null)
        {
            var mover = t.GetComponent<EntityMover>();
            if (mover == null) mover = t.gameObject.AddComponent<EntityMover>();
            var pos = t.position;
            var r = mover.Step(pos.x, pos.z, x, z, gotoX, gotoZ, speed, Time.deltaTime);

            float y = _terrain != null ? TerrainFactory.HeightAt(_terrain, r.X, r.Z) : 0f;
            t.position = new Vector3(r.X, y, r.Z);
            // Đang ĐI: xoay theo hướng đi (EntityMover). ĐỨNG mà đang đánh nhau (faceTarget có): xoay
            // MẶT vào target (char) — native _XMob quay vào mục tiêu khi đánh; nếu không sẽ giữ hướng
            // đi cũ → cả đám mob quay cùng một phía (chủ 09/09). Mesh mob nhìn -Z nên hướng -(target-mob).
            // Đang ĐI: xoay theo hướng đi (EntityMover). ĐỨNG mà đang chiến (faceTarget có, trong
            // MobFaceHold giây): xoay MẶT vào target (player) — native _XMob quay vào mục tiêu khi đánh.
            // Mesh mob nhìn -Z nên hướng -(target-mob). ĐỨNG mà hết hold (ko chiến): giữ hướng spawn/đi
            // cũ (r.Rot) — QUAN TRỌNG để quái mới spawn giữ facing ngẫu nhiên, ko quay lockstep.
            // ⭐ Lỗi "Bandit đánh xong quay mặt 90° rồi đòn sau quay lại" (chủ 14/09): hold 3s HẾT giữa 2
            // đòn (cooldown > 3s) → snap về hướng walk-in (lệch ~90°). FIX = hold DÀI hơn (6s) + refresh
            // khi mob BỊ ĐÁNH (MonsterDamaged, ở trên) — lúc đánh nhau player bổ liên tục nên timer luôn
            // mới → mob hướng vào player suốt trận. Hold chỉ áp cho mob ĐỨNG (đi thì luôn theo hướng đi)
            // nên để dài vô hại.
            if (!r.Moving && faceTarget.HasValue)
            {
                Vector3 d = faceTarget.Value - t.position; d.y = 0f;
                if (d.sqrMagnitude > 0.0001f)
                {
                    var want = Quaternion.LookRotation(new Vector3(-d.x, 0f, -d.z), Vector3.up);
                    t.rotation = Quaternion.RotateTowards(t.rotation, want, 540f * Time.deltaTime);
                }
            }
            else t.rotation = r.Rot;
        }

        /// <summary>Mob move speed (world u/s) from the last MONSTER_MOVE: base walk/run (C++
        /// 0.0022/0.0075 u/ms) scaled by ucSpeedRate/20, mirroring the server's quantization so the
        /// client interpolation advances at the same rate the server advances the mob.</summary>
        private static float MobSpeed(Nd.Session.MonsterEntity m)
        {
            float baseUps = m.WalkMode == 0 ? EntityMover.WalkSpeed : EntityMover.RunSpeed;
            // ucSpeedRate (raw) == 20 is "normal"; 0 = never sent (MONSTER_EXIST) -> normal.
            int rate = m.SpeedRate;
            return rate > 0 ? baseUps * (rate / 20f) : baseUps;
        }

        private void SetupCameraAndControl()
        {
            var cam = Camera.main;
            if (cam == null)
                cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            cam.farClipPlane = 2000f;

            _control = _player.gameObject.AddComponent<PlayerController>();
            _control.Session = _session;
            _control.Terrain = _terrain;
            _control.Cam = cam;
            _control.TeleportToSession();
            // clicking an NPC walks up to it and opens its conversation (StartNPCScript). The dialog art
            // is a placeholder — the functional part is the pick/approach/face + the action hooks below.
            _control.OnNpcReached = npc =>
            {
                if (npc == null) return;
                // action packets (quest offer / shop open / MSG_NO_STR_ITEM_REQ warehouse / craft / refine)
                // are the next port — for now each choice flashes a notice; the pick/approach/dialog work.
                NpcDialog.Show(npc, action => SystemNotice.Show($"{npc.Name}: '{action}' đang hoàn thiện"));
                Debug.Log($"[NPC] reached npc {npc.UniqueId} '{npc.Name}' job={npc.JobClass} quest={npc.HasQuest}");
            };

            SkeletalAnimator playerAnim = null;
            if (_skeleton != null && ArgF("-ndNoAnim", 0f) <= 0f)
            {
                // real skeletal animation from .XA clips: idle = _XAN_IDLE_NONE (bre), run =
                // _XAN_MOVE_NONE_REPEAT (run). The peace/battle stance + attack/skill one-shots
                // are driven by AnimDriver below.
                string p = _sex == 0 ? "Mz" : "Wz";
                var mb = _assets.CharBiped(0); var wb = _assets.CharBiped(1);
                Debug.Log($"[BIPEDCLIP] male idle={mb?.ClipFor(CharBipedTable.IDLE_NONE)} run={mb?.ClipFor(CharBipedTable.MOVE_NONE_REPEAT)} | " +
                          $"female idle={wb?.ClipFor(CharBipedTable.IDLE_NONE)} run={wb?.ClipFor(CharBipedTable.MOVE_NONE_REPEAT)}");
                Debug.Log($"[KHINHCONG] biped count male={mb?.Count} fastNone={mb?.ClipFor(CharBipedTable.FASTMOVE_NONE_REPEAT)} " +
                          $"fastSwordPeace={mb?.ClipFor(CharBipedTable.FASTMOVE_SWORD_REPEAT_PEACE)} " +
                          $"fastSwordBattle={mb?.ClipFor(CharBipedTable.FASTMOVE_SWORD_REPEAT_BATTLE)} " +
                          $"fastLong={mb?.ClipFor(CharBipedTable.FASTMOVE_LONGWEAPON_REPEAT)}");
                var sa = _player.gameObject.AddComponent<SkeletalAnimator>();
                sa.Skel = _skeleton;
                sa.Controller = _control;
                // clip names from the biped table (authoritative), NOT a hardcoded "{p}_a_x_bre_a" guess —
                // the female idle/run filenames don't follow the male pattern, so the guess fell through to
                // the run clip and the character stood in a forward-leaning run pose.
                var bip = _assets.CharBiped(_sex);
                sa.SetClips(_assets, bip?.ClipFor(CharBipedTable.IDLE_NONE) ?? $"{p}_a_x_bre_a.xa",
                            bip?.ClipFor(CharBipedTable.MOVE_NONE_REPEAT) ?? $"{p}_a_x_run_a.xa");
                playerAnim = sa;
            }
            else
            {
                var visual = _player.Find("visual");
                if (visual != null)
                {
                    var anim = visual.gameObject.AddComponent<CharacterAnimator>();
                    anim.Controller = _control;
                }
            }

            var tps = cam.GetComponent<TpsCamera>() ?? cam.gameObject.AddComponent<TpsCamera>();
            tps.target = _player;
            tps.terrain = _terrain;
            if (ArgF("-ndNoPostFx", 0f) <= 0f) NdPostFx.Attach(cam);   // bloom + ACES tone-map + vignette (gameplay)
            float camDist = ArgF("-ndCamDist", 0f);   // debug: force the third-person zoom distance
            if (camDist > 0f) tps.distance = camDist;

            EnvironmentFactory.BuildSky(cam);
            WeatherFactory.ApplyMood(_weather, cam);

            var hud = new GameObject("HUD").AddComponent<GameHud>();
            _hud = hud;
            hud.Session = _session;
            hud.Assets = _assets;
            // tên HIỂN THỊ = tên đẹp theo zone id (vd "Bamboo Village"), fallback tên map thô nếu thiếu.
            // Giữ _mapName thô cho texture (us_Minimap_<base>) + BGM.
            string zoneNice = _assets.GetZoneName(_session.Me.Zone);
            hud.MapName = string.IsNullOrWhiteSpace(zoneNice) ? _mapName : zoneNice;
            hud.Level = _level;
            hud.MapWorldSize = _terrain != null ? _terrain.Width : 512f;
            // real minimap art: us_Minimap_<base>.tga  (map name without the "map_" prefix)
            string mmBase = _mapName.StartsWith("map_", StringComparison.OrdinalIgnoreCase)
                ? _mapName.Substring(4) : _mapName;
            var mm = _assets.GetTexture($"us_Minimap_{mmBase}.tga");
            if (mm != null && mm.width > 4) hud.MinimapTex = mm;

            // event-driven SFX (combat hit / death / level-up / footsteps) through AudioService
            SfxDriver.Attach(_session, _player, _assets);

            // event-driven skeletal animations: mob death/attack/blow, player stance (TAB / arrow),
            // player attack + skill one-shots. Mirrors the client's SetAnimation event wiring.
            AnimDriver.LogEvents = ArgF("-ndLogAnim", 0f) > 0f;
            AnimDriver.AutoStance = ArgF("-ndAutoStance", 0f) > 0f;   // headless: cycle peace/battle idle
            AnimDriver.ForceBattle = ArgF("-ndForceBattle", 0f) > 0f; // headless: enter+stay battle stance
            AnimDriver.CorpseFade = ArgF("-ndNoCorpseFade", 0f) <= 0f;   // diagnostic: disable corpse despawn
            AnimDriver.TestEnter = ArgF("-ndTestEnter", 0f) > 0f;        // verify: fire mob spawn (ENTERING) clip
            AnimDriver.AutoSkill = ArgF("-ndAutoSkill", 0f) > 0f;        // verify: slot+fire a combat skill motion
            AnimDriver.FxOnCast = (int)ArgF("-ndFxOnCast", 0f);          // live VFX demo: force this template on cast
            AnimDriver.Attach(_session, _player, playerAnim, _sex, _assets);
        }

        /// <summary>Vé di chuyển to a DIFFERENT map: tear down the current world and rebuild it at the new
        /// zone (Me.Zone), reusing the SAME connection + player model + camera/controller (seamless, no
        /// re-login) — the Unity counterpart of the client's proc_loading reload on a resent CHARAC_INFO.</summary>
        private void RebuildForZone()
        {
            var me = _session.Me;
            var zones = _assets.LoadZoneTable();
            string mapName = zones.MapNameFor(me.Zone);
            if (string.IsNullOrEmpty(mapName)) { Debug.LogWarning($"[VILLAGE] teleport: no map for zone {me.Zone}"); _builtZone = me.Zone; return; }
            _villagePicker = false;
            Debug.Log($"[VILLAGE] teleport reload -> zone {me.Zone} map {mapName} @({me.X:0.#},{me.Z:0.#})");

            // ---- build the NEW map FIRST; if it fails, keep the old world intact (never strand the player) ----
            MapBuilder.Result result;
            try { result = MapBuilder.Build(_assets, mapName, terrainColliders: true); }
            catch (Exception e) { Debug.LogError($"[VILLAGE] build '{mapName}' failed, staying put: {e.Message}"); _builtZone = me.Zone; return; }
            _mapName = mapName;

            // ---- tear down the old world (keep the player model, camera, controller, HUD) ----
            var oldMapRoot = _mapRoot;
            if (_mobRoot != null) Destroy(_mobRoot.gameObject);
            _mobs.Clear(); _mobAnims.Clear(); _players.Clear(); _mobFaceUntil.Clear(); _playerModeled.Clear();
            _session.World.Monsters.Clear(); _session.World.Players.Clear();   // old-zone entities are gone

            _terrain = result.Terrain;
            _mapRoot = result.Root;
            _weather = result.Weather;
            WorldNameplate.OccluderTerrain = result.Terrain;
            if (oldMapRoot != null) Destroy(oldMapRoot);

            // re-point the kept controller / camera at the new terrain, then snap the player to the spawn
            var cam = Camera.main;
            if (_control != null) { _control.Terrain = _terrain; _control.TeleportToSession(); }
            var tps = cam != null ? cam.GetComponent<TpsCamera>() : null;
            if (tps != null) tps.terrain = _terrain;
            if (cam != null) WeatherFactory.ApplyMood(_weather, cam);
            AudioService.PlayForMap(_assets, mapName);

            // new map is loaded → tell nd-world to spawn this zone's monsters (GAMESTARTSIGNAL), then
            // drain the initial MONSTER_EXIST burst and rebuild the live entities.
            _session.SendGameStart();
            _session.Pump(800);
            _mobRoot = new GameObject("LiveEntities").transform;
            SyncEntities();

            // minimap + map name for the new zone
            if (_hud != null)
            {
                string zoneNice = _assets.GetZoneName(me.Zone);
                string mmBase = mapName.StartsWith("map_", StringComparison.OrdinalIgnoreCase) ? mapName.Substring(4) : mapName;
                var mm = _assets.GetTexture($"us_Minimap_{mmBase}.tga");
                _hud.SetMap(string.IsNullOrWhiteSpace(zoneNice) ? mapName : zoneNice,
                            _terrain != null ? _terrain.Width : 512f,
                            (mm != null && mm.width > 4) ? mm : null);
            }

            _builtZone = me.Zone;
            _status = $"{me.Name} in {mapName} — {_session.Monsters.Count} mobs streamed";
        }

        // When the player equips/unequips a weapon (me.Weapon changes via INVEN_MOVE), swap the held mesh on
        // the 3D character LIVE so it actually holds the equipped weapon (chủ: "trang bị vũ khí thì nhân vật
        // phải cầm cây vũ khí đó"). MountWeapon no-ops when a weapon is already mounted, so drop the old one first.
        private void RefreshLocalWeapon()
        {
            if (_session == null || _player == null || _skeleton == null || _assets == null) return;
            var me = _session.Me; if (me == null) return;
            if (me.Weapon == _weapon && (int)me.WeaponType == _weaponType) return;   // unchanged
            _weapon = me.Weapon; _weaponType = (int)me.WeaponType; _weaponModelIndex = 0;
            // Drop the old held weapon. Destroy() is DEFERRED to end-of-frame, and MountWeapon no-ops when it
            // still finds a renderer named "item_wea*", so RENAME the old one first → MountWeapon can't find it
            // and mounts the new weapon this frame (no duplicate, no bare-hand).
            var oldW = WeaponMount.FindWeaponRenderer(_player);
            if (oldW != null) { oldW.gameObject.name = "_old_weapon_removing"; Destroy(oldW.gameObject); }
            if (_weapon > 0)
            {
                WeaponMount.MountWeapon(_player, _skeleton, _assets, _sex, _weapon, _weaponModelIndex, true, _weaponType);
                // if the exact + class-substitute meshes both failed to skin, mount ANY present weapon so the
                // character never ends up empty-handed after an equip (chủ: "phải cầm vũ khí").
                if (WeaponMount.FindWeaponRenderer(_player) == null)
                    WeaponMount.MountAnyPresentWeapon(_player, _skeleton, _assets, _sex);
            }
            // NOTE: no WeaponMount.Apply here — re-applying the enchant aura on every equip piled up a dark
            // "smoke" effect (chủ). The glow is applied once at spawn; live equip just swaps the mesh.
        }

        private void Update()
        {
            // offline demo: no world pump — just fire the one verification screenshot
            if (_demoMode)
            {
                // re-play the effect only under -ndFxLoop (default: the single spawn from BuildCharPreview,
                // so the screenshot shows ONE true instance, not an accumulated pile-up).
                if (_fxTestId != 0 && _player != null && ArgF("-ndFxLoop", 0f) > 0f && Time.time >= _fxNext)
                {
                    EffectFactory.Play(_assets, _fxTestId, _skeleton, _player);
                    _fxNext = Time.time + 3.0f;
                }
                if (_shotAt > 0f && Time.time >= _shotAt)
                {
                    _shotAt = (ArgF("-ndShotLoop", 0f) > 0f) ? Time.time + 0.6f : -1f;
                    string d = Directory.GetParent(Application.dataPath).FullName;
                    ScreenCapture.CaptureScreenshot(Path.Combine(d, _shotName));
                    Debug.Log("[NdGameClient] demo screenshot -> " + Path.Combine(d, _shotName));
                }
                return;
            }

            if (_session == null || !_session.InWorld) return;

            _session.Pump(1);

            // vé di chuyển (Move Village) — the server answered a teleport request:
            //  • different map (CHARAC_INFO changed the zone) → rebuild the world at the new map;
            //  • same map (ZONE_RESTART) → just snap the player to the new spot.
            if (_session.PendingZoneReload) { _session.PendingZoneReload = false; RebuildForZone(); return; }
            if (_session.PendingTeleport)   { _session.PendingTeleport = false; _control?.TeleportToSession(); }
            // key E toggles the Move Village destination list (needs a vé, id 602, in the bag)
            if (Input.GetKeyDown(KeyCode.E)) _villagePicker = !_villagePicker;

            // player position is owned by PlayerController now (client-predicted + reconciled)
            SyncEntities();
            RefreshLocalWeapon();   // re-mount the held weapon live when the player equips/unequips (right-click)

            if (_shotAt > 0f && Time.time >= _shotAt)
            {
                _shotAt = (ArgF("-ndShotLoop", 0f) > 0f) ? Time.time + 0.4f : -1f;   // -ndShotLoop: keep capturing (debug/verify)
                string dir = Directory.GetParent(Application.dataPath).FullName;
                string file = Path.Combine(dir, "nd_screenshot.png");
                ScreenCapture.CaptureScreenshot(file);
                Debug.Log("[NdGameClient] screenshot -> " + file);
                if (SkinnedCharacterFactory.LogParts)
                {
                    Debug.Log($"[SKINQ] level={QualitySettings.GetQualityLevel()} skinWeights={QualitySettings.skinWeights}");
                    // front-view ASCII silhouette of the ACTUAL baked (rendered) verts, so we can
                    // see exactly where each part lands: is the arm extended or collapsed?
                    const int gW = 60, gH = 44;
                    const float gx0 = -0.75f, gx1 = 0.75f, gyH = 1.45f;
                    var grid = new char[gH, gW];
                    for (int a = 0; a < gH; a++) for (int b = 0; b < gW; b++) grid[a, b] = '.';
                    foreach (var r in _player.GetComponentsInChildren<SkinnedMeshRenderer>())
                    {
                        var baked = new Mesh();
                        r.BakeMesh(baked, true);   // actual skinned verts in renderer-local space
                        var bv = baked.vertices;
                        float mn = 1e9f, mx = -1e9f, xmn = 1e9f, xmx = -1e9f;
                        var c = _player.position;
                        string pn = r.gameObject.name;
                        char sym = pn.Contains("_b.") ? 'B' : pn.Contains("_c1.") || pn.Contains("_c2.") ? 'h'
                                 : pn.Contains("_d.") ? 'L' : pn.Contains("_e1.") || pn.Contains("_e2.") ? 'f'
                                 : pn.Contains("_a.") ? 'a' : pn.Contains("_h") ? '^' : '#';
                        foreach (var v in bv)
                        {
                            var w = r.transform.TransformPoint(v);
                            if (w.y < mn) mn = w.y; if (w.y > mx) mx = w.y;
                            float lx = w.x - c.x; if (lx < xmn) xmn = lx; if (lx > xmx) xmx = lx;
                            int gx = (int)((lx - gx0) / (gx1 - gx0) * gW);
                            int gy = (int)((w.y - c.y) / gyH * gH);
                            if (gx >= 0 && gx < gW && gy >= 0 && gy < gH) grid[gH - 1 - gy, gx] = sym;
                        }
                        Debug.Log($"[BAKE] {r.gameObject.name} worldY=[{mn:0.00}..{mx:0.00}] localX=[{xmn:0.00}..{xmx:0.00}]");
                        // verify the skin matrix for bone 14 (L Forearm): bones[14].LtW * bindposes[14]
                        // should be a pure translation (the char placement); rotation/scale here = bug.
                        if (r.gameObject.name.Contains("_b.") && r.bones != null && r.bones.Length > 33)
                            foreach (var bi in new[] { 12, 13, 14 })
                            {
                                var sk = r.bones[bi].localToWorldMatrix * r.sharedMesh.bindposes[bi];
                                var c0 = sk.GetColumn(0); var c1 = sk.GetColumn(1);
                                Debug.Log($"[SKINM{bi}] |c0|={c0.magnitude:0.00} |c1|={c1.magnitude:0.00} c0.c1={Vector3.Dot(c0,c1):0.00} col0={c0}");
                            }
                        if (r.gameObject.name.Contains("_d.") && r.bones != null && r.bones.Length > 33)
                            foreach (var bi in new[] { 32, 33 })
                            {
                                var sk = r.bones[bi].localToWorldMatrix * r.sharedMesh.bindposes[bi];
                                var c0 = sk.GetColumn(0); var c1 = sk.GetColumn(1);
                                Debug.Log($"[SKINM{bi}] |c0|={c0.magnitude:0.00} |c1|={c1.magnitude:0.00} c0.c1={Vector3.Dot(c0,c1):0.00}");
                            }
                    }
                    Debug.Log("[SIL] front view: cols x[-0.75..0.75], rows y[1.45..0]; B=body h=hand L=legs f=feet a=face ^=hat");
                    for (int a = 0; a < grid.GetLength(0); a++)
                    {
                        var row = new char[grid.GetLength(1)];
                        for (int b = 0; b < grid.GetLength(1); b++) row[b] = grid[a, b];
                        Debug.Log("[SIL] " + new string(row));
                    }
                    if (_skeleton != null)
                    {
                        var c = _player.position;
                        foreach (var id in new[] { 12, 13, 14, 15, 32, 33, 34, 35 })
                            if (id < _skeleton.Bones.Length)
                            {
                                var bp = _skeleton.Bones[id].position - c;   // relative to char root
                                Debug.Log($"[BONEW] {id} {_skeleton.Bones[id].name.Replace("Bip01 ","")} local=({bp.x:0.00},{bp.y:0.00},{bp.z:0.00})");
                            }
                    }
                }
                var pp = _player.position;
                float th = TerrainFactory.HeightAt(_terrain, pp.x, pp.z);
                var cp = Camera.main.transform.position;
                Debug.Log($"[DIAG] player=({pp.x:0.##},{pp.y:0.##},{pp.z:0.##}) terrainH={th:0.##} " +
                          $"camera=({cp.x:0.##},{cp.y:0.##},{cp.z:0.##}) camDist={Vector3.Distance(cp, pp):0.##}");
                if (autoWalk > 0f)
                {
                    var moved = _player.position - _startPos;
                    Debug.Log($"[NdGameClient] autoWalk moved dx={moved.x:0.##} dz={moved.z:0.##} " +
                              $"(server Me=({_session.Me.X:0.##},{_session.Me.Z:0.##}))");
                }
            }
        }

        private void SyncEntities()
        {
            foreach (var kv in _session.Monsters)
            {
                if (!_mobs.TryGetValue(kv.Key, out var t))
                {
                    var root = new GameObject($"mob_{kv.Key}_{kv.Value.RawType}");
                    root.transform.SetParent(_mobRoot, false);
                    // click-targeting: a capsule collider on the MOB layer + the mob id, so the
                    // player can pick this monster to attack (the ground move-ray ignores this layer).
                    root.layer = EntityId.MobLayer;
                    root.AddComponent<EntityId>().Id = kv.Key;
                    var col = root.AddComponent<CapsuleCollider>();
                    col.center = new Vector3(0f, 1f, 0f); col.height = 2.2f; col.radius = 0.8f;
                    // native gives each spawned mob a random facing (XMob.cpp:3000); add the mover now and
                    // flag it so the spawn group doesn't all point one way. PlaceEntity reuses this mover.
                    root.AddComponent<EntityMover>().RandomSpawnFacing = true;
                    var animated = _assets.BuildAnimatedMob(kv.Value.RawType, kv.Key.ToString());
                    var model = animated == null ? _assets.GetMobModel(kv.Value.RawType) : null;
                    if (SkinnedCharacterFactory.LogAnim)
                        Debug.Log($"[MOBBUILD] key={kv.Key} rawType={kv.Value.RawType} animated={(animated != null)} staticModel={(model != null && model.Mesh.vertexCount > 0)}");
                    if (animated != null)
                    {
                        animated.transform.SetParent(root.transform, false);
                        // cache the animator so we can feed it the per-move walk/run mode each frame;
                        // pin the mob's root upright so a jittery run-mode mob never leans/inverts.
                        var msa = animated.GetComponent<SkeletalAnimator>();
                        // LockRootRotAlways = mob (root plays raw like native; only inversion guarded).
                        // RandomPhase = stagger the idle/walk cycle so a spawn group doesn't animate in
                        // lockstep ("cả đám hành động giống nhau") — matches native's staggered-creation desync.
                        if (msa != null) { msa.LockRootRotAlways = true; msa.RandomPhase = true; _mobAnims[kv.Key] = msa; }
                    }
                    else if (model != null && model.Mesh.vertexCount > 0)
                    {
                        var m = new GameObject("mesh");
                        m.transform.SetParent(root.transform, false);
                        m.AddComponent<MeshFilter>().sharedMesh = model.Mesh;
                        var mats = new Material[model.SubmeshTextures.Length];
                        for (int i = 0; i < mats.Length; i++) mats[i] = _assets.GetObjectMaterial(model.SubmeshTextures[i]);
                        m.AddComponent<MeshRenderer>().sharedMaterials = mats;
                    }
                    // no visible placeholder for a mob whose model failed to load — an ugly cube
                    // clutters the scene; the entity is still tracked (position/targeting) invisibly.
                    // persistent nameplate: localized name (level-coloured) + mini HP bar above the head.
                    AddMobNameplate(root.transform, kv.Value);
                    _mobs[kv.Key] = t = root.transform;
                }
                // đang đánh nhau với char (vừa đánh/skill trong MobFaceHold giây) → đứng thì quay mặt vào char
                Vector3? face = null;
                if (_player != null && _mobFaceUntil.TryGetValue(kv.Key, out var until) && Time.time < until)
                    face = _player.position;
                PlaceEntity(t, kv.Value.X, kv.Value.Z, kv.Value.GotoX, kv.Value.GotoZ, MobSpeed(kv.Value), face);
                // walk vs run animation: feed the server move mode; and hand the animator the EntityMover
                // (added by PlaceEntity) so it uses the AUTHORITATIVE "arrived / not moving" flag — a mob
                // that has stopped shows idle (upright) instead of freezing in the run clip's forward lean
                // ("quái đứng nghiêng"). Locking the root alone did not fix it: the lean lives in the spine
                // of the run clip, so the real fix is to stop playing the run clip when the mob is idle.
                if (_mobAnims.TryGetValue(kv.Key, out var mAnim) && mAnim != null)
                {
                    mAnim.RunMode = kv.Value.WalkMode != 0;
                    if (mAnim.Mover == null) mAnim.Mover = t.GetComponent<EntityMover>();
                }
            }
            foreach (var kv in _session.Players)
            {
                var pe = kv.Value;
                if (!_players.TryGetValue(kv.Key, out var t))
                {
                    var root = new GameObject($"player_{kv.Key}");
                    root.transform.SetParent(_mobRoot, false);
                    _players[kv.Key] = t = root.transform;
                }

                // Appearance (op27, ZONE_CHARAC_INFO) may not arrive with the first ZONE_MOVE, so a PC
                // starts as a marker. Mirror the client: request the appearance (retry until it lands),
                // and the moment it does, replace the marker with the real skinned character in place —
                // otherwise the player would stay a yellow capsule forever (XNetwork.cpp:15624-15638).
                if (pe.HasAppearance && !_playerModeled.Contains(kv.Key))
                {
                    // DIAGNOSTIC: dump the appearance the server actually sent in op27, so a "random
                    // model" can be pinned to the wire values (server) vs the client resolution.
                    Debug.Log($"[OP27] uid={kv.Key} name='{pe.Name}' sex={pe.Sex} face={pe.Face} hair={pe.Hair} " +
                              $"amor={pe.Amor} pants={pe.Pants} cap={pe.Cap} weapon={pe.Weapon} " +
                              $"faceMesh='{_assets.FaceClusterMesh(pe.Sex, pe.Face) ?? "(null)"}' " +
                              $"hairMesh='{_assets.HairClusterMesh(pe.Sex, pe.Hair) ?? "(null)"}'");
                    for (int i = t.childCount - 1; i >= 0; i--) Destroy(t.GetChild(i).gameObject);   // drop the marker
                    // TEMP TEST: force the face so it can be compared live against the C++ client
                    // (0 = use the real op27 face). Set back to 0 once the correct value is confirmed.
                    int useFace = ForceOtherFace > 0 ? ForceOtherFace : pe.Face;
                    var body = SkinnedCharacterFactory.BuildPlayer(_assets, pe.Sex, useFace, pe.Hair,
                        string.IsNullOrEmpty(pe.Name) ? kv.Key.ToString() : pe.Name,
                        out var pskel, pe.Amor, pe.Pants, pe.Cap, pe.Weapon);
                    if (body != null)
                    {
                        body.transform.SetParent(t, false);
                        // other players hold their weapon too (op27 gives their Weapon id); no aura
                        // (their refine is not on the wire) — WeaponMount mounts the mesh only.
                        if (pskel != null && pe.Weapon > 0)
                            WeaponMount.MountWeapon(body.transform, pskel, _assets, pe.Sex, pe.Weapon);
                        AddPlayerNameplate(body.transform, pe.Name, null);
                        // idle/walk animation so other players BREATHE and STEP instead of standing in the
                        // stiff bind (A) pose. No Controller (they aren't click-driven): the SkeletalAnimator
                        // auto-switches idle<->walk from the world-position delta EntityMover applies, exactly
                        // like NPCs/mobs. Idle = {p}_a_x_bre_a.xa, run = {p}_a_x_run_a.xa (M/W by gender).
                        if (pskel != null && ArgF("-ndNoAnim", 0f) <= 0f)
                        {
                            string pfx = pe.Sex == 0 ? "Mz" : "Wz";
                            var sa = body.AddComponent<SkeletalAnimator>();
                            sa.Skel = pskel;
                            // idle/run clip names from the biped table (authoritative) — the female
                            // filenames don't follow the male "{p}_a_x_bre_a" pattern, so the old hardcoded
                            // guess fell through to the run clip and the character leaned forward. A worn
                            // weapon picks the weapon-class peace idle so a blade hangs naturally.
                            var biped = _assets.CharBiped(pe.Sex);
                            int idleMotion = CharBipedTable.IDLE_NONE, runMotion = CharBipedTable.MOVE_NONE_REPEAT;
                            if (pe.Weapon > 0 && _assets.Items != null &&
                                _assets.Items.TryGetWeapon(pe.Weapon, out var pw))
                            {
                                idleMotion = CharBipedTable.WeaponPeaceIdle(pw.SecondType, pw.ThirdType);
                                runMotion = CharBipedTable.WeaponRun(pw.SecondType, pw.ThirdType, false);   // peace run
                            }
                            string idleClip = biped?.ClipFor(idleMotion) ?? $"{pfx}_a_x_bre_a.xa";
                            string runClip = biped?.ClipFor(runMotion) ?? $"{pfx}_a_x_run_a.xa";
                            sa.SetClips(_assets, idleClip, runClip);   // glTF overlay (gltf_anim) wins over .XA
                            // use the interpolator's authoritative moving flag (not a jitter-prone
                            // position-delta guess), so an idle player stays in the idle pose.
                            sa.Mover = t.GetComponent<EntityMover>() ?? t.gameObject.AddComponent<EntityMover>();
                        }
                        _playerModeled.Add(kv.Key);   // built once; don't rebuild every frame
                    }
                    else if (t.childCount == 0) BuildPlayerMarker(t);   // build failed → keep a marker, retry later
                }
                else if (!pe.HasAppearance)
                {
                    if (t.childCount == 0) BuildPlayerMarker(t);
                    // (re)request the appearance from the main thread until it arrives
                    if (!_apprReqAt.TryGetValue(kv.Key, out var next) || Time.time >= next)
                    { _session.SendReqCharacInfo(kv.Key); _apprReqAt[kv.Key] = Time.time + 3f; }
                }

                // other players run at the character run speed (ZONE_MOVE carries no per-move speed)
                PlaceEntity(t, pe.X, pe.Z, pe.GotoX, pe.GotoZ, 4.2f);
            }
        }

        // A temporary marker for a PC whose appearance (op27) hasn't arrived yet — replaced by the real
        // skinned character as soon as ZONE_CHARAC_INFO fills the PlayerEntity (see SyncEntities).
        private void BuildPlayerMarker(Transform parent)
        {
            var cap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            cap.GetComponent<Collider>().enabled = false;   // must not block click-to-move raycasts
            cap.transform.SetParent(parent, false);
            cap.transform.localPosition = Vector3.up;
            cap.GetComponent<Renderer>().sharedMaterial = MarkerMaterial(new Color(0.9f, 0.85f, 0.2f));
        }

        private void OnGUI()
        {
            // status only until the world is up; the HUD takes over after that
            if (!_demoMode && (_session == null || !_session.InWorld))
                GUI.Label(new Rect(10, 10, 900, 60), _status);
            if (_villagePicker && !_demoMode && _session != null && _session.InWorld)
                DrawVillagePicker();
        }

        /// <summary>Key-E Move Village list: pick a destination → SendMoveVillage. The server checks the
        /// ticket (id 602) and teleports (ZONE_RESTART same map / CHARAC_INFO different map).</summary>
        private void DrawVillagePicker()
        {
            var vills = _assets.MoveVillages?.Villages;
            if (vills == null || vills.Count == 0) { _villagePicker = false; return; }
            const float w = 360f, h = 470f;
            float x = (Screen.width - w) * 0.5f, y = (Screen.height - h) * 0.5f;
            GUI.Box(new Rect(x, y, w, h), "Vé di chuyển — chọn nơi đến  (E: đóng)");
            _villageScroll = GUI.BeginScrollView(new Rect(x + 8, y + 26, w - 16, h - 36),
                                                 _villageScroll, new Rect(0, 0, w - 40, vills.Count * 26f));
            for (int i = 0; i < vills.Count; i++)
            {
                var v = vills[i];
                string name = _assets.GetInterfaceText(v.VillageNameId);
                if (string.IsNullOrWhiteSpace(name)) name = _assets.GetZoneName(v.MapId);
                if (string.IsNullOrWhiteSpace(name)) name = $"Map {v.MapId}";
                string label = v.MaxLv > 0 ? $"{name}   (Lv {v.MinLv}-{v.MaxLv})" : name;
                if (GUI.Button(new Rect(0, i * 26f, w - 40f, 24f), label))
                {
                    _session.SendMoveVillage(v.SendId);
                    Debug.Log($"[VILLAGE] request -> '{name}' sendId={v.SendId} map={v.MapId} area={v.AreaId}");
                    _villagePicker = false;
                }
            }
            GUI.EndScrollView();
        }

        private void OnDestroy()
        {
            _session?.Dispose();
            _assets?.Dispose();
        }
    }
}
