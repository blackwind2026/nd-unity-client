using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NineDragons.Client
{
    /// <summary>
    /// Drop-in map viewer: put this on an empty GameObject (or use the
    /// "NineDragons/Build Map Scene" editor menu), press Play, fly around with WASD + mouse.
    ///
    /// Also doubles as a server-free BGM harness: it plays the map's looping background music via
    /// <see cref="AudioService"/>. Command-line overrides (built player): -ndMap &lt;name&gt; picks the
    /// map, -ndMap2 &lt;name&gt; + -ndBgmSwitch &lt;sec&gt; crossfades to a second map's track after a delay
    /// (to verify map-change crossfade), and -ndQuitAfter &lt;sec&gt; exits so a headless run ends cleanly.
    /// </summary>
    public sealed class NdMapViewer : MonoBehaviour
    {
        [Tooltip("Map name without extension, e.g. map_hapbi / MAP_NAKYANG / Map_Bookhae")]
        public string mapName = "map_hapbi";

        [Tooltip("Client Data folder holding Model/Texture/Environment/*.XP")]
        public string dataPath = ClientData.DefaultRoot;

        public bool terrainColliders = false;

        private AssetService _assets;

        private void Start()
        {
            var sw = Stopwatch.StartNew();
            mapName = Arg("-ndMap") ?? mapName;
            ClientData.Root = Arg("-ndData") ?? dataPath;
            AssetService.LogLoose = Arg("-ndLogLoose") != null;   // log loose .x3d hits + [GLTF] .glb hits
            _assets = new AssetService();
            _assets.Open();

            var result = MapBuilder.Build(_assets, mapName, terrainColliders);
            Debug.Log($"[NdMapViewer] {mapName}: {MapBuilder.Describe(result.Terrain)}");
            Debug.Log($"[NdMapViewer] built in {sw.ElapsedMilliseconds} ms — {result}");

            // per-map background music (looping, crossfaded on change) — same path the game uses
            AudioService.PlayForMap(_assets, mapName);

            PlaceCamera(result);

            // headless BGM parity checks (all optional, for the server-free verification player):
            if (ArgF("-ndAltSeconds", 0f) > 0f) AudioService.AlternateSeconds = ArgF("-ndAltSeconds", 180f);

            string map2 = Arg("-ndMap2");
            float switchAt = ArgF("-ndBgmSwitch", 0f);
            if (!string.IsNullOrEmpty(map2) && switchAt > 0f)
                StartCoroutine(SwitchBgm(map2, switchAt));

            float toggleAt = ArgF("-ndBgmToggleAt", 0f);   // mute then (re)enable to test BGMENABLE
            if (toggleAt > 0f) StartCoroutine(ToggleBgm(toggleAt));

            string special = Arg("-ndSpecial");             // event/boss override track (e.g. battle01.ogg)
            float specialAt = ArgF("-ndSpecialAt", 0f);
            if (!string.IsNullOrEmpty(special) && specialAt > 0f)
                StartCoroutine(SpecialBgm(special, specialAt, ArgF("-ndClearSpecialAt", 0f)));

            if (ArgF("-ndSfxTest", 0f) > 0f) StartCoroutine(SfxSelfTest());

            float quitAfter = ArgF("-ndQuitAfter", 0f);
            if (quitAfter > 0f)
                StartCoroutine(QuitAfter(quitAfter));

            string shot = Arg("-ndShot");                       // capture one screenshot then quit
            if (!string.IsNullOrEmpty(shot))
                StartCoroutine(Shot(shot, ArgF("-ndShotAt", 2.5f)));
        }

        private IEnumerator Shot(string path, float delay)
        {
            yield return new WaitForSeconds(delay);
            // frame the whole placed scene from a 3/4 aerial view unless the user pinned a camera
            if (Arg("-ndNoFrame") == null) FrameScene();
            yield return null;
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log($"[NdMapViewer] screenshot -> {path}");
            yield return new WaitForSeconds(1.5f);
            Application.Quit();
        }

        /// <summary>Aim Camera.main at the centre of all renderers from a 3/4 aerial angle so a
        /// verification screenshot actually frames the map's objects (not whatever the default view hit).</summary>
        private void FrameScene()
        {
            var cam = Camera.main; if (cam == null) return;
            bool any = false; var b = new Bounds();
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (!any) return;
            float ext = b.size.magnitude * 0.5f;
            Vector3 dir = new Vector3(0.55f, 0.6f, -0.55f).normalized;   // from front-up-right
            cam.transform.position = b.center + dir * ext * 1.7f;
            cam.transform.LookAt(b.center);
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, ext * 6f);
        }

        // Fire each core SFX once so the WAV decode + 2D/3D playback path is verifiable headless.
        private IEnumerator SfxSelfTest()
        {
            AudioService.SetAssets(_assets);
            var cam = Camera.main;
            Vector3 near = cam != null ? cam.transform.position + cam.transform.forward * 8f : Vector3.zero;
            yield return new WaitForSeconds(1f);
            Debug.Log("[NdMapViewer] SFX test: combat hit (3D)");
            AudioService.PlaySfx("MCh01_Scream01.wav", near);
            yield return new WaitForSeconds(1f);
            Debug.Log("[NdMapViewer] SFX test: footstep (3D)");
            AudioService.PlaySfx("NormalStepLeft01.wav", near, 0.7f);
            yield return new WaitForSeconds(1f);
            Debug.Log("[NdMapViewer] SFX test: monster die (3D)");
            AudioService.PlaySfx("MCh01_Die01.wav", near);
            yield return new WaitForSeconds(1f);
            Debug.Log("[NdMapViewer] SFX test: level up (2D)");
            AudioService.PlaySfx("levelup01.wav");
            yield return new WaitForSeconds(1f);
            Debug.Log("[NdMapViewer] SFX test: UI click (2D)");
            AudioService.PlaySfx("SmallButtonClick.wav");
            yield return new WaitForSeconds(1f);
            Debug.Log("[NdMapViewer] SFX test: melee swing (3D)");
            AudioService.PlaySfx("ONSLAUGHT_SWORD_ATT_01.wav", near);
            yield return new WaitForSeconds(1f);
            Debug.Log("[NdMapViewer] SFX test: skill cast (3D)");
            AudioService.PlaySfx("skill_charge001.wav", near);
            yield return new WaitForSeconds(1f);
            Debug.Log("[NdMapViewer] SFX test: item pickup (2D)");
            AudioService.PlaySfx("PICK_ITEM_001.wav");
            yield return new WaitForSeconds(1f);
            Debug.Log("[NdMapViewer] SFX test: money (2D)");
            AudioService.PlaySfx("money_01.wav");
            yield return new WaitForSeconds(1f);
            Debug.Log("[NdMapViewer] SFX test: per-weapon swing AX + LANCE (3D)");
            AudioService.PlaySfx("ONSLAUGHT_AX_ATT_01.wav", near);
            yield return new WaitForSeconds(0.6f);
            AudioService.PlaySfx("ONSLAUGHT_LANCE_ATT_01.wav", near);
        }

        private IEnumerator SwitchBgm(string map2, float delay)
        {
            yield return new WaitForSeconds(delay);
            Debug.Log($"[NdMapViewer] map change -> {map2} (BGM crossfade)");
            AudioService.PlayForMap(_assets, map2);
        }

        private IEnumerator ToggleBgm(float delay)
        {
            yield return new WaitForSeconds(delay);
            Debug.Log("[NdMapViewer] BGMENABLE off");
            AudioService.SetEnabled(false);
            yield return new WaitForSeconds(3f);
            Debug.Log("[NdMapViewer] BGMENABLE on (resume)");
            AudioService.SetEnabled(true);
        }

        private IEnumerator SpecialBgm(string track, float atSec, float clearSec)
        {
            yield return new WaitForSeconds(atSec);
            Debug.Log($"[NdMapViewer] special BGM -> {track}");
            AudioService.PlaySpecial(_assets, track);
            if (clearSec > atSec)
            {
                yield return new WaitForSeconds(clearSec - atSec);
                Debug.Log("[NdMapViewer] clear special BGM (restore map)");
                AudioService.ClearSpecial();
            }
        }

        private IEnumerator QuitAfter(float delay)
        {
            yield return new WaitForSeconds(delay);
            Debug.Log("[NdMapViewer] quit");
            Application.Quit(0);
        }

        private void PlaceCamera(MapBuilder.Result result)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
            }
            cam.farClipPlane = 2000f;
            if (cam.GetComponent<FlyCamera>() == null)
                cam.gameObject.AddComponent<FlyCamera>();
            if (Arg("-ndNoPostFx") == null) NdPostFx.Attach(cam);   // bloom + tone-map + vignette

            // start above the middle of the map looking down the +Z axis
            float y = TerrainFactory.HeightAt(result.Terrain, 0f, 0f);
            cam.transform.position = new Vector3(0f, y + 30f, -40f);
            cam.transform.rotation = Quaternion.Euler(25f, 0f, 0f);
        }

        private static string Arg(string name)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
                if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
            return null;
        }

        private static float ArgF(string name, float def)
        {
            var s = Arg(name);
            return s != null && float.TryParse(s, out var f) ? f : def;
        }

        private void OnDestroy() => _assets?.Dispose();
    }
}
