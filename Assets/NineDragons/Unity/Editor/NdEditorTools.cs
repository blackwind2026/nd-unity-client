using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NineDragons.Client;
using Debug = UnityEngine.Debug;

namespace NineDragons.Client.Editor
{
    /// <summary>
    /// Editor entry points for M6-render.
    ///  - Menu "NineDragons/Build Map Scene": creates a scene with an NdMapViewer, ready to Play.
    ///  - <see cref="Smoke"/>: batchmode smoke test —
    ///      Unity.exe -batchmode -nographics -projectPath ... -executeMethod
    ///      NineDragons.Client.Editor.NdEditorTools.Smoke [-ndMap map_hapbi]
    ///    builds the map headless (terrain + objects + textures decode on the real .XP data)
    ///    and exits 0/1, so the whole render path is verifiable without a GUI.
    /// </summary>
    public static class NdEditorTools
    {
        [MenuItem("NineDragons/Build Map Scene")]
        public static void BuildMapScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var go = new GameObject("NdMapViewer");
            go.AddComponent<NdMapViewer>();
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/MapViewer.unity");
            Selection.activeGameObject = go;
            Debug.Log("Saved Assets/Scenes/MapViewer.unity — set the map name on NdMapViewer and press Play.");
        }

        [MenuItem("NineDragons/Build Game Scene (login)")]
        public static void BuildGameSceneMenu() { CreateGameScene(); }

        /// <summary>Batchmode: extract the character-window interface .tga atlases from
        /// TR_MAININTERFACE.XP to PNG so their real pixel sizes / sub-rects can be measured.
        ///   Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   NineDragons.Client.Editor.NdEditorTools.DumpInterfaceTex [-ndOut C:\dir]</summary>
        public static void DumpInterfaceTex()
        {
            try
            {
                string outDir = GetArg("-ndOut") ?? Path.Combine(Path.GetTempPath(), "nd_iface");
                Directory.CreateDirectory(outDir);
                ClientData.Root = ClientData.DefaultRoot;
                var a = new AssetService();
                a.Open();
                string[] names =
                {
                    "MI_Characterwin.tga", "MI_BorderTile.tga", "MI_main_back01.tga",
                    "MI_GMWindow.tga", "MI_quest2.tga", "MI_quest1.tga",
                    "mi_skillwin.tga", "MI_NPCQuestwin_01.tga", "MI_NPCQuestwin_03.tga",
                    "MI_chat_quick.tga", "MI_cooltime.tga", "MI_System.tga",
                    "mi_party.tga", "MI_Trade.tga", "MI_tra_base.tga",
                    "Mastery_Icon_00.tga", "icon_skilllist00.tga",
                    "MI_Alience1.tga", "MI_Alience2.tga", "MI_Messenger1.tga",
                    "Item_Wea_13.tga", "Item_Clo_00a.tga", "Item_Pot_w3_003.tga", "item_mon.tga",
                    "Item_Wea_14.tga", "Item_Clo_00b.tga", "Item_Clo_00e.tga",
                };
                foreach (var n in names)
                {
                    var tex = a.GetInterfaceTexture(n);
                    if (tex == null) { Debug.LogWarning("[DumpTex] missing " + n); continue; }
                    var png = tex.EncodeToPNG();
                    File.WriteAllBytes(Path.Combine(outDir, n + ".png"), png);
                    Debug.Log($"[DumpTex] {n} -> {tex.width}x{tex.height} ({png.Length} bytes)");
                }
                Debug.Log("[DumpTex] done -> " + outDir);
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogError("[DumpTex] " + ex); EditorApplication.Exit(1); }
        }

        private static string CreateGameScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var go = new GameObject("NdGameClient");
            go.AddComponent<NdGameClient>();
            Directory.CreateDirectory("Assets/Scenes");
            const string path = "Assets/Scenes/Game.unity";
            EditorSceneManager.SaveScene(scene, path);
            Debug.Log("Saved " + path);
            return path;
        }

        /// <summary>
        /// Batchmode player build:
        ///   Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   NineDragons.Client.Editor.NdEditorTools.BuildPlayer
        /// Builds to TWO places (chủ 09/09): (1) SOURCE-local Builds\Win64 = bản build giữ cạnh source
        /// (source là gốc quan trọng nhất — client mất vẫn build lại được), (2) DEPLOY copy sang thư mục
        /// client ClientData.UnityClientDir (C:\9Dragons Unity) để click là chạy, cạnh Data\ game.
        /// Override build dir với -ndBuildOut &lt;dir&gt;; bỏ deploy với -ndNoDeploy.
        /// </summary>
        public static void BuildPlayer()
        {
            // glTFast's runtime materials use these shaders; nothing references them at build time so
            // they'd be stripped → magenta models. Force them into the player.
            EnsureShadersIncluded("glTF/PbrMetallicRoughness", "glTF/PbrSpecularGlossiness", "glTF/Unlit", "NineDragons/Character", "NineDragons/Sharpen", "NineDragons/PostFx");
            string scenePath = CreateGameScene();
            string buildDir = GetArg("-ndBuildOut") ?? "Builds/Win64";   // (1) build cạnh SOURCE
            string outExe = Path.Combine(buildDir, "NineDragonsUnity.exe");
            Directory.CreateDirectory(buildDir);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = outExe,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(options);
            bool ok = report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;
            Debug.Log($"[BuildPlayer] result={report.summary.result} size={report.summary.totalSize} " +
                      $"errors={report.summary.totalErrors} -> {outExe}");
            // (2) DEPLOY: copy payload sang thư mục client (KHÔNG đụng Data\ game + launcher native ở đó)
            if (ok && !HasArg("-ndNoDeploy"))
            {
                try { DeployPlayer(buildDir, ClientData.UnityClientDir); Debug.Log($"[BuildPlayer] deployed -> {ClientData.UnityClientDir}"); }
                catch (Exception ex) { Debug.LogError("[BuildPlayer] deploy FAILED: " + ex); ok = false; }
            }
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>Copy ĐÚNG payload Unity Windows player từ buildDir sang thư mục client, ghi đè; xoá
        /// sạch 3 thư mục payload ở đích trước (mirror, tránh file build cũ sót) NHƯNG chừa Data\ game
        /// + NineDragons.exe/NDLAUNCHER (launcher native) + mọi thứ khác trong thư mục client.</summary>
        static void DeployPlayer(string srcDir, string dstDir)
        {
            Directory.CreateDirectory(dstDir);
            foreach (var f in new[] { "NineDragonsUnity.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe" })
            {
                var s = Path.Combine(srcDir, f);
                if (File.Exists(s)) File.Copy(s, Path.Combine(dstDir, f), true);
            }
            foreach (var d in new[] { "NineDragonsUnity_Data", "MonoBleedingEdge", "D3D12" })
            {
                var s = Path.Combine(srcDir, d);
                if (!Directory.Exists(s)) continue;
                var dst = Path.Combine(dstDir, d);
                if (Directory.Exists(dst)) Directory.Delete(dst, true);   // mirror sạch (chỉ payload, ko phải Data\)
                CopyDir(s, dst);
            }
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        static bool HasArg(string name)
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Add shaders to Graphics → Always Included so a player build keeps them (runtime-only
        /// materials, e.g. glTFast, otherwise get stripped and render magenta).</summary>
        static void EnsureShadersIncluded(params string[] names)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0) { Debug.LogWarning("[BuildPlayer] no GraphicsSettings"); return; }
            var so = new SerializedObject(assets[0]);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            foreach (var n in names)
            {
                var sh = Shader.Find(n);
                if (sh == null) { Debug.LogWarning("[BuildPlayer] shader not found: " + n); continue; }
                bool present = false;
                for (int i = 0; i < arr.arraySize; i++)
                    if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) { present = true; break; }
                if (present) continue;
                arr.arraySize++;
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
                Debug.Log("[BuildPlayer] +always-included shader: " + n);
            }
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Server-free BGM verification player: builds a standalone that runs an NdMapViewer scene
        /// (no login), so the real runtime audio path (resolve map -> .ogg from SR_SOUND.XP -> decode
        /// AudioClip -> loop through AudioSource) can be exercised headless and checked in the log.
        ///   Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   NineDragons.Client.Editor.NdEditorTools.BuildMapViewerPlayer
        /// Produces Builds\Win64_MapViewer\NineDragonsUnity.exe. Run it with:
        ///   NineDragonsUnity.exe -ndMap map_hapbi -ndMap2 map_gaebang -ndBgmSwitch 6 -ndQuitAfter 12
        ///   -logFile out.log   (then grep out.log for "[BGM]").
        /// </summary>
        public static void BuildMapViewerPlayer()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            new GameObject("NdMapViewer").AddComponent<NdMapViewer>();
            Directory.CreateDirectory("Assets/Scenes");
            const string scenePath = "Assets/Scenes/MapViewer.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = "Builds/Win64_MapViewer/NineDragonsUnity.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[BuildMapViewerPlayer] result={report.summary.result} size={report.summary.totalSize} " +
                      $"errors={report.summary.totalErrors} -> {options.locationPathName}");
            EditorApplication.Exit(report.summary.result ==
                UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>
        /// WebGL (browser) player build:
        ///   Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   NineDragons.Client.Editor.NdEditorTools.BuildWebGL
        /// Produces Builds\WebGL\index.html (+ Build\ payload). The browser client
        /// reaches nd-world via the WS<->TCP bridge (Nd.Net.WebSocketTransport +
        /// NdWebSocket.jslib); raw TCP is impossible in a browser.
        /// </summary>
        public static void BuildWebGL()
        {
            string scenePath = CreateGameScene();
            // WebGL asset/plugin processing (the .jslib) applies under the WebGL
            // target, so switch to it before building.
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);

            // no server-side decompression on a plain static host / file:// preview
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;

            var options = new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = "Builds/WebGL",
                target = BuildTarget.WebGL,
                targetGroup = BuildTargetGroup.WebGL,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[BuildWebGL] result={report.summary.result} size={report.summary.totalSize} " +
                      $"errors={report.summary.totalErrors} -> {options.locationPathName}");
            EditorApplication.Exit(report.summary.result ==
                UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>
        /// Android (APK) player build:
        ///   Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   NineDragons.Client.Editor.NdEditorTools.BuildAndroid
        /// Produces Builds\Android\NineDragonsUnity.apk. Uses raw TCP (TcpTransport)
        /// like desktop - mobile can open sockets - so it talks to nd-world directly
        /// (or to the WS bridge if ForceWebSocket is set). Needs the Android module
        /// (SDK/NDK/JDK) installed in the editor.
        /// </summary>
        public static void BuildAndroid()
        {
            string scenePath = CreateGameScene();
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

            // debug keystore so an unsigned CI build still installs on a device
            PlayerSettings.Android.useCustomKeystore = false;

            Directory.CreateDirectory("Builds/Android");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = "Builds/Android/NineDragonsUnity.apk",
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[BuildAndroid] result={report.summary.result} size={report.summary.totalSize} " +
                      $"errors={report.summary.totalErrors} -> {options.locationPathName}");
            EditorApplication.Exit(report.summary.result ==
                UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }

        public static void Smoke()
        {
            string map = GetArg("-ndMap") ?? "map_hapbi";
            int code = 1;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ClientData.Root = GetArg("-ndData") ?? ClientData.DefaultRoot;
                using (var assets = new AssetService())
                {
                    assets.Open();
                    var result = MapBuilder.Build(assets, map);
                    Debug.Log($"[Smoke] {map}: {MapBuilder.Describe(result.Terrain)}");
                    Debug.Log($"[Smoke] {result} in {sw.ElapsedMilliseconds} ms");

                    bool ok = result.TerrainVerts > 0 && result.ObjectsPlaced > 0;
                    // sanity: a known texture decodes to a real GPU texture
                    var tex = assets.GetTexture(result.Terrain.LayerTextureNames.Length > 0
                                                ? result.Terrain.LayerTextureNames[0] : null);
                    ok &= tex != null && tex.width > 0;
                    Debug.Log($"[Smoke] base texture: {tex.name} {tex.width}x{tex.height} fmt={tex.format}");
                    code = ok ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Smoke] FAILED: " + ex);
                code = 1;
            }
            EditorApplication.Exit(code);
        }

        /// <summary>
        /// Headless terrain-format dump for one or more maps (comma sep in -ndMap):
        ///   Unity.exe -batchmode -nographics -projectPath ... -executeMethod
        ///   NineDragons.Client.Editor.NdEditorTools.TerrainDump -ndMap map_hapbi,map_gaebang
        /// Prints layer count/names/transforms, per-tile layer usage histogram, diffuse-colour
        /// stats and splat-cache presence — the numbers needed to size the splat shader.
        /// </summary>
        public static void TerrainDump()
        {
            string maps = GetArg("-ndMap") ?? "map_hapbi";
            try
            {
                ClientData.Root = GetArg("-ndData") ?? ClientData.DefaultRoot;
                using (var assets = new AssetService())
                {
                    assets.Open();
                    foreach (var map in maps.Split(','))
                    {
                        var t = assets.LoadTerrain(map.Trim());
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine($"=== {map} v{t.Version:0.00} tiles={t.TileCount} grid={t.GridSize} world={t.Width}x{t.Height} ===");
                        sb.AppendLine($"layers={t.LayerCount} splatImg={t.SplatImageSize} splatImgs={t.SplatImages.Length}");
                        for (int i = 0; i < t.LayerCount; i++)
                        {
                            var tr = t.LayerTransforms[i];
                            var tex = assets.GetTexture(t.LayerTextureNames[i]);
                            sb.AppendLine($"  L{i}: '{t.LayerTextureNames[i]}' tex={tex.name} {tex.width}x{tex.height} " +
                                          $"trans=({tr[0]:0.###},{tr[1]:0.###}) scale=({tr[2]:0.###},{tr[3]:0.###}) rot={tr[4]:0.###}");
                        }
                        // per-tile: how many higher layers are active
                        var hist = new int[t.LayerCount + 1];
                        int maxActive = 0;
                        for (int ti = 0; ti < t.TileSplats.Length; ti++)
                        {
                            var ts = t.TileSplats[ti];
                            int n = 0;
                            for (int L = 1; L < t.LayerCount; L++) if (ts.UseLayer.Length > L && ts.UseLayer[L] != 0) n++;
                            if (n < hist.Length) hist[n]++;
                            if (n > maxActive) maxActive = n;
                        }
                        sb.Append("  tiles by #higher-layers active: ");
                        for (int n = 0; n <= maxActive; n++) sb.Append($"{n}={hist[n]} ");
                        sb.AppendLine();
                        // per-layer: how many tiles USE each higher layer L + max blend weight (0..15)
                        for (int L = 1; L < t.LayerCount; L++)
                        {
                            int used = 0, maxW = 0;
                            for (int ti = 0; ti < t.TileSplats.Length; ti++)
                            {
                                var ts = t.TileSplats[ti];
                                if (ts.UseLayer.Length <= L || ts.UseLayer[L] == 0) continue;
                                used++;
                                if (ts.Blends.TryGetValue(L, out var bl) && bl != null)
                                    for (int k = 0; k < bl.Length; k++) if (bl[k] > maxW) maxW = bl[k];
                            }
                            sb.AppendLine($"    L{L} '{t.LayerTextureNames[L]}': usedTiles={used}/{t.TileSplats.Length} maxBlend={maxW}/15");
                        }
                        // diffuse colour stats
                        long sr = 0, sg = 0, sb2 = 0; byte mn = 255, mx = 0;
                        for (int i = 0; i < t.DiffuseArgb.Length; i++)
                        {
                            uint a = t.DiffuseArgb[i];
                            byte r = (byte)(a >> 16), g = (byte)(a >> 8), b = (byte)a;
                            sr += r; sg += g; sb2 += b;
                            byte lo = System.Math.Min(r, System.Math.Min(g, b));
                            byte hi = System.Math.Max(r, System.Math.Max(g, b));
                            if (lo < mn) mn = lo; if (hi > mx) mx = hi;
                        }
                        int nc = t.DiffuseArgb.Length;
                        sb.AppendLine($"  diffuse avg=({sr / nc},{sg / nc},{sb2 / nc}) min={mn} max={mx}");
                        Debug.Log(sb.ToString());
                    }
                }
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError("[TerrainDump] FAILED: " + ex);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// Headless map render (needs graphics — run WITHOUT -nographics):
        ///   Unity.exe -batchmode -projectPath ... -executeMethod
        ///   NineDragons.Client.Editor.NdEditorTools.RenderMap -ndMap map_gaebang
        ///   -ndOut C:\path\shot.png [-ndYaw 30 -ndPitch 22 -ndDist 120 -ndHeight 40]
        /// Builds the map from real data (no server) and writes a PNG so terrain/scenery can be
        /// verified visually for any map.
        /// </summary>
        public static void RenderMap()
        {
            string map = GetArg("-ndMap") ?? "map_hapbi";
            string outPath = GetArg("-ndOut") ?? ("Builds/render_" + map + ".png");
            try
            {
                ClientData.Root = GetArg("-ndData") ?? ClientData.DefaultRoot;
                MapBuilder.AnimateEntities = false;   // static bind-pose NPCs are enough for a still
                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                using (var assets = new AssetService())
                {
                    assets.Open();
                    var res = MapBuilder.Build(assets, map);
                    Debug.Log($"[RenderMap] {map}: {MapBuilder.Describe(res.Terrain)} | trees={res.Trees}");
                    Debug.Log($"[RenderMap] placement: {res}");

                    var t = res.Terrain;
                    var camGo = new GameObject("RenderCam");
                    var cam = camGo.AddComponent<Camera>();
                    EnvironmentFactory.BuildSky(cam);
                    WeatherFactory.ApplyMood(res.Weather, cam);

                    float cx = ParseF("-ndTx", 0f), cz = ParseF("-ndTz", 0f);   // terrain centred at origin
                    float cy = TerrainFactory.HeightAt(t, cx, cz);
                    if (GetArg("-ndTop") != null)                 // orthographic top-down for path alignment
                    {
                        cam.orthographic = true;
                        cam.orthographicSize = ParseF("-ndOrtho", t.Width * 0.5f);
                        cam.transform.position = new Vector3(cx, cy + t.Width, cz);
                        cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                        cam.farClipPlane = t.Width * 3f;
                        cam.nearClipPlane = 0.5f;
                    }
                    else
                    {
                        float yaw = ParseF("-ndYaw", 35f), pitch = ParseF("-ndPitch", 24f);
                        float dist = ParseF("-ndDist", t.Width * 0.28f);
                        float height = ParseF("-ndHeight", t.Width * 0.12f);
                        var target = new Vector3(cx, cy + 6f, cz);
                        var dir = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
                        cam.transform.position = target - dir * dist + Vector3.up * height;
                        cam.transform.LookAt(target);
                        cam.fieldOfView = ParseF("-ndFov", 55f);
                        cam.farClipPlane = Mathf.Max(2000f, t.Width * 3f);
                        cam.nearClipPlane = 0.5f;
                    }

                    var sun = new GameObject("Sun").AddComponent<Light>();
                    sun.type = LightType.Directional;
                    sun.transform.rotation = Quaternion.Euler(50f, 30f, 0);
                    sun.intensity = 1.0f;
                    sun.shadows = LightShadows.None;   // terrain carries its own baked vertex shadows

                    int W = 1280, H = 720;
                    var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var img = new Texture2D(W, H, TextureFormat.RGB24, false);
                    img.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                    img.Apply();
                    RenderTexture.active = null;
                    System.IO.File.WriteAllBytes(outPath, img.EncodeToPNG());
                    Debug.Log($"[RenderMap] wrote {outPath}");
                }
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogError("[RenderMap] " + ex); EditorApplication.Exit(1); }
        }

        private static float ParseF(string name, float def)
        {
            var s = GetArg(name);
            return s != null && float.TryParse(s, out var f) ? f : def;
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
