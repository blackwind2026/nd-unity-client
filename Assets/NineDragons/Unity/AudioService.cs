using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace NineDragons.Client
{
    /// <summary>
    /// Per-map background music, mirroring the C++ client (XProc_MainGame): each zone/map names two
    /// looping .ogg tracks in Zonelist.ini (g_ZoneInfoTable[zone].zonebgmfilename1/2), stored in
    /// SR_SOUND.XP, and the client plays them on map-enter (PlayBGM). Unity decodes OGG only from a
    /// URL, so the entry is written to a temp file, loaded with UnityWebRequestMultimedia, then looped.
    ///
    /// Parity features (all mirroring XProc_MainGame):
    ///  - persistent singleton with two AudioSources → a map change crossfades instead of cutting;
    ///  - BGMENABLE toggle (client: g_BgmPlayFlag from the BGMENABLE registry flag; here PlayerPrefs,
    ///    default on) with a Ctrl+M hotkey — off = silence, on = resume the map track;
    ///  - special/event BGM channel (client: SetSpecialBGMPlay/StopSpecialBGMPlay, XProc_MainGame.cpp
    ///    :3426-3483) that replaces the map BGM (e.g. boss/event music) and restores it on clear;
    ///  - the client's 180s (US) / 120s (JP) timer that alternates zonebgmfilename1 ↔ 2
    ///    (ProcessChangeBgm): the AudioSource loop keeps one track playing continuously (no
    ///    replay-on-end, no double-play), and every 180s we crossfade to the other variant.
    /// </summary>
    public sealed class AudioService : MonoBehaviour
    {
        /// <summary>Steady-state music volume (loud enough to hear, quiet enough not to blast).</summary>
        public const float TargetVolume = 0.4f;
        /// <summary>Crossfade length in seconds when switching tracks.</summary>
        public const float FadeSeconds = 1.2f;
        /// <summary>Client timer to alternate bgm1↔bgm2 (US 180s; JP build uses 120s).</summary>
        public static float AlternateSeconds = 180f;

        private const string PrefEnabled = "nd_bgm_enabled";

        /// <summary>Gate for the verification logs (resolved track, clip length/samples, isPlaying).</summary>
        public static bool LogBgm = true;

        private static AudioService _instance;

        private AudioSource _a, _b;   // two sources; the active one is audible, the other fades in
        private AudioSource _active;
        private string _currentTrack; // track name loaded on _active ("" = none)
        private int _loadGen;         // supersedes an in-flight load when a newer request arrives

        private AssetService _assets;     // cached for resume / alternation / special loads
        private string _currentMap;       // last requested map (for resume + alternation)
        private string _bgm1, _bgm2;      // the map's two variants
        private int _variant;             // 0 = bgm1, 1 = bgm2 (client m_CurrentBGMType)
        private float _alternateTimer;    // seconds on the current variant
        private bool _enabled = true;     // BGMENABLE
        private bool _special;            // event/boss override active (client m_SpecialBGMPlayMode)
        private string _specialTrack;

        // ---- SFX (one-shot sound effects: hit/step/UI/level-up), mirroring the client's
        // _XPlaySoundEffect (3D positional, g_SoundEffectArchive) + _XPlayInterfaceSound (2D UI).
        private const string PrefSfxEnabled = "nd_sfx_enabled";
        private const string PrefSfxVolume = "nd_sfx_volume";
        private const int Sfx3DPoolSize = 8;
        /// <summary>Gate for the SFX verification logs.</summary>
        public static bool LogSfx = true;
        private bool _sfxEnabled = true;     // SFXENABLE (mirrors BGMENABLE)
        private float _sfxVolume = 0.7f;     // master SFX volume
        private AudioSource _sfx2D;          // UI / non-positional one-shots
        private AudioSource[] _sfx3D;        // pooled positional one-shots
        private int _sfx3DNext;
        private readonly System.Collections.Generic.Dictionary<string, AudioClip> _sfxClips =
            new System.Collections.Generic.Dictionary<string, AudioClip>(System.StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.HashSet<string> _sfxMissing =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>Is BGM currently enabled (BGMENABLE)? Persisted in PlayerPrefs, default on.</summary>
        public static bool Enabled =>
            _instance != null ? _instance._enabled : PlayerPrefs.GetInt(PrefEnabled, 1) != 0;

        // ------------------------------------------------------------------ map BGM

        /// <summary>Resolve and play the looping BGM for a map name (e.g. "map_hapbi"). Crossfades from
        /// whatever is playing; a no-op if that map's track is already playing. Safe in edit mode
        /// (does nothing) and when the map has no BGM column. Respects the BGMENABLE toggle and yields
        /// to an active special/event track.</summary>
        public static void PlayForMap(AssetService assets, string mapName)
        {
            if (!Application.isPlaying || assets == null) return;
            EnsureInstance().DoPlayForMap(assets, mapName);
        }

        private void DoPlayForMap(AssetService assets, string mapName)
        {
            _assets = assets;
            _currentMap = mapName;
            _bgm1 = assets.MapBgm(mapName);
            _bgm2 = assets.MapBgm2(mapName);
            // start on bgm1; if bgm1's .ogg is missing from this client build but bgm2 is present,
            // start on bgm2 so the map isn't silent (e.g. map_yodong_Boss: seungchun.ogg absent -> yodong.ogg)
            _variant = (!string.IsNullOrEmpty(_bgm1) && !assets.HasSound(_bgm1)
                        && !string.IsNullOrEmpty(_bgm2) && assets.HasSound(_bgm2)) ? 1 : 0;
            _alternateTimer = 0f;

            if (_special) return;                       // event track overrides; ClearSpecial resumes this map
            if (!_enabled)
            {
                if (LogBgm) Debug.Log($"[BGM] disabled (BGMENABLE off) — map '{mapName}' track '{_bgm1}' not started");
                return;
            }
            string track = _variant == 0 ? _bgm1 : _bgm2;
            if (string.IsNullOrEmpty(track))
            {
                if (LogBgm) Debug.Log($"[BGM] map '{mapName}' has no background-music track");
                StopPlayback();
                return;
            }
            PlayTrack(track, $"map '{mapName}'");
        }

        // ------------------------------------------------------------------ enable toggle

        /// <summary>Turn BGM on/off (BGMENABLE). Persists to PlayerPrefs. On → resume the current map
        /// track; off → fade to silence. Mirrors the client's Option→Sound "Use BGM" checkbox.</summary>
        public static void SetEnabled(bool on)
        {
            PlayerPrefs.SetInt(PrefEnabled, on ? 1 : 0);
            PlayerPrefs.Save();
            if (Application.isPlaying) EnsureInstance().DoSetEnabled(on);
        }

        /// <summary>Flip the BGMENABLE toggle.</summary>
        public static void ToggleEnabled() => SetEnabled(!Enabled);

        private void DoSetEnabled(bool on)
        {
            _enabled = on;
            if (LogBgm) Debug.Log($"[BGM] BGMENABLE -> {(on ? "ON" : "OFF")}");
            if (!on) { StopPlayback(); return; }
            if (_special) return;                       // special keeps playing; leave it
            if (!string.IsNullOrEmpty(_currentMap) && !string.IsNullOrEmpty(_bgm1))
            {
                _alternateTimer = 0f;
                PlayTrack(_variant == 0 ? _bgm1 : _bgm2, $"map '{_currentMap}'");
            }
        }

        // ------------------------------------------------------------------ special / event BGM

        /// <summary>Play an override track (boss/event music) that replaces the map BGM, looping. The
        /// map track resumes on <see cref="ClearSpecial"/>. Mirrors SetSpecialBGMPlay — it plays even
        /// when the map track is muted/absent, and takes over the normal-BGM slot until cleared.</summary>
        public static void PlaySpecial(AssetService assets, string track)
        {
            if (!Application.isPlaying || string.IsNullOrEmpty(track)) return;
            var inst = EnsureInstance();
            if (assets != null) inst._assets = assets;
            inst.DoPlaySpecial(track);
        }

        private void DoPlaySpecial(string track)
        {
            if (_assets == null) { Debug.LogWarning("[BGM] PlaySpecial before any assets available"); return; }
            _special = true;
            _specialTrack = track;
            _alternateTimer = 0f;
            PlayTrack(track, $"special '{track}'");
        }

        /// <summary>End the override and restore the map BGM (or silence if BGM is disabled). Mirrors
        /// StopSpecialBGMPlay + the flag restore in ProcessChangeBgm.</summary>
        public static void ClearSpecial()
        {
            if (_instance == null) return;
            _instance.DoClearSpecial();
        }

        private void DoClearSpecial()
        {
            if (!_special) return;
            _special = false;
            _specialTrack = null;
            if (LogBgm) Debug.Log($"[BGM] special cleared -> restore map '{_currentMap}'");
            if (!_enabled) { StopPlayback(); return; }
            if (!string.IsNullOrEmpty(_currentMap) && !string.IsNullOrEmpty(_bgm1))
            {
                _alternateTimer = 0f;
                PlayTrack(_variant == 0 ? _bgm1 : _bgm2, $"map '{_currentMap}'");
            }
            else StopPlayback();
        }

        /// <summary>Stop the music (fades out). Used on logout / return to title.</summary>
        public static void Stop()
        {
            if (_instance == null) return;
            _instance._currentMap = null;
            _instance._special = false;
            _instance.StopPlayback();
        }

        // ------------------------------------------------------------------ lifecycle

        private static AudioService EnsureInstance()
        {
            if (_instance != null) return _instance;
            var go = new GameObject("BGM");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<AudioService>();
            return _instance;
        }

        private void Awake()
        {
            if (_instance == null) _instance = this;
            _enabled = PlayerPrefs.GetInt(PrefEnabled, 1) != 0;
            _a = MakeSource();
            _b = MakeSource();
            _active = _a;
            _currentTrack = "";

            // SFX: one 2D source for UI + a small pool of 3D positional sources for world one-shots
            _sfxEnabled = PlayerPrefs.GetInt(PrefSfxEnabled, 1) != 0;
            _sfxVolume = PlayerPrefs.GetFloat(PrefSfxVolume, 0.7f);
            _sfx2D = gameObject.AddComponent<AudioSource>();
            _sfx2D.playOnAwake = false; _sfx2D.loop = false; _sfx2D.spatialBlend = 0f;
            _sfx3D = new AudioSource[Sfx3DPoolSize];
            for (int i = 0; i < Sfx3DPoolSize; i++)
            {
                var go = new GameObject("sfx3d" + i);
                go.transform.SetParent(transform, false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false; s.loop = false;
                s.spatialBlend = 1f;                 // fully 3D
                s.rolloffMode = AudioRolloffMode.Linear;
                s.minDistance = 4f; s.maxDistance = 40f;
                _sfx3D[i] = s;
            }
        }

        private AudioSource MakeSource()
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.loop = true;
            s.playOnAwake = false;
            s.spatialBlend = 0f;   // 2D — same volume everywhere
            s.volume = 0f;
            return s;
        }

        private void Update()
        {
            // Ctrl+M toggles BGM, Ctrl+N toggles SFX (client uses Option→Sound checkboxes)
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.M)) ToggleEnabled();
            if (ctrl && Input.GetKeyDown(KeyCode.N)) ToggleSfxEnabled();

            // 180s bgm1↔bgm2 alternation (client ProcessBgmTimer + ProcessChangeBgm). The active
            // source already loops, so this only crossfades to the OTHER variant — never a second
            // copy of the same track. Skip when muted / special / one-track maps.
            if (!_enabled || _special || string.IsNullOrEmpty(_bgm1) || string.IsNullOrEmpty(_bgm2)) return;
            if (string.Equals(_bgm1, _bgm2, System.StringComparison.OrdinalIgnoreCase)) return;
            if (_assets == null || !_assets.HasSound(_bgm1) || !_assets.HasSound(_bgm2)) return;   // only alternate between present tracks
            if (_active == null || !_active.isPlaying) return;
            _alternateTimer += Time.unscaledDeltaTime;
            if (_alternateTimer >= AlternateSeconds)
            {
                _alternateTimer = 0f;
                _variant ^= 1;
                PlayTrack(_variant == 0 ? _bgm1 : _bgm2, $"map '{_currentMap}' (alt bgm{_variant + 1})");
            }
        }

        // ------------------------------------------------------------------ playback core

        private void PlayTrack(string track, string label)
        {
            if (string.IsNullOrEmpty(track)) return;
            if (track == _currentTrack && _active != null && _active.isPlaying) return;   // already playing
            _currentTrack = track;
            int gen = ++_loadGen;
            StartCoroutine(LoadAndCrossfade(track, label, gen));
        }

        private void StopPlayback()
        {
            _loadGen++;                 // cancel any in-flight load
            _currentTrack = "";
            StartCoroutine(FadeOutAll());
        }

        private IEnumerator LoadAndCrossfade(string track, string label, int gen)
        {
            byte[] ogg = _assets != null ? _assets.GetSound(track) : null;
            if (ogg == null || ogg.Length == 0)
            {
                Debug.LogWarning($"[BGM] '{track}' for {label} not found in SR_SOUND.XP");
                yield break;
            }

            string path = Path.Combine(Application.temporaryCachePath, "bgm_" + track);
            try { File.WriteAllBytes(path, ogg); }
            catch (System.Exception ex) { Debug.LogWarning($"[BGM] temp write '{track}': {ex.Message}"); yield break; }

            string url = "file://" + path.Replace("\\", "/");
            using var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS);
            ((DownloadHandlerAudioClip)req.downloadHandler).streamAudio = false;   // fully decode so length/samples are known
            yield return req.SendWebRequest();

            if (gen != _loadGen) yield break;   // a newer request superseded this load
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[BGM] decode '{track}' failed: {req.error}");
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(req);
            clip.name = track;

            var next = (_active == _a) ? _b : _a;
            next.clip = clip;
            next.loop = true;
            next.volume = 0f;
            next.Play();

            if (LogBgm)
                Debug.Log($"[BGM] {label} -> {track}: loaded {clip.length:0.#}s " +
                          $"({clip.samples} samples, {clip.frequency}Hz, {clip.channels}ch), " +
                          $"isPlaying={next.isPlaying}, enabled={_enabled}, special={_special}");

            var prev = _active;
            _active = next;

            // crossfade: new source up to TargetVolume, old source down to silence
            float t = 0f;
            float startPrev = prev != null ? prev.volume : 0f;
            while (t < FadeSeconds)
            {
                if (gen != _loadGen) yield break;   // superseded mid-fade — leave the newer load in charge
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / FadeSeconds);
                next.volume = TargetVolume * k;
                if (prev != null && prev != next) prev.volume = startPrev * (1f - k);
                yield return null;
            }
            next.volume = TargetVolume;
            if (prev != null && prev != next) { prev.Stop(); prev.clip = null; prev.volume = 0f; }
        }

        private IEnumerator FadeOutAll()
        {
            var src = _active;
            if (src == null) yield break;
            float start = src.volume, t = 0f;
            while (t < FadeSeconds)
            {
                t += Time.unscaledDeltaTime;
                src.volume = start * (1f - Mathf.Clamp01(t / FadeSeconds));
                yield return null;
            }
            if (_a != null) { _a.Stop(); _a.clip = null; _a.volume = 0f; }
            if (_b != null) { _b.Stop(); _b.clip = null; _b.volume = 0f; }
        }

        // ================================================================== SFX

        /// <summary>True if SFX are enabled (SFXENABLE); persisted, default on.</summary>
        public static bool SfxEnabled =>
            _instance != null ? _instance._sfxEnabled : PlayerPrefs.GetInt(PrefSfxEnabled, 1) != 0;

        /// <summary>Master SFX volume 0..1; persisted, default 0.7.</summary>
        public static float SfxVolume =>
            _instance != null ? _instance._sfxVolume : PlayerPrefs.GetFloat(PrefSfxVolume, 0.7f);

        /// <summary>Make the SFX engine's decoder available (SR_SOUND.XP reader). PlayForMap already
        /// sets this at map load; the SFX driver calls it too so UI clicks work before the first hit.</summary>
        public static void SetAssets(AssetService assets)
        {
            if (assets == null || !Application.isPlaying) return;
            EnsureInstance()._assets = assets;
        }

        public static void SetSfxEnabled(bool on)
        {
            PlayerPrefs.SetInt(PrefSfxEnabled, on ? 1 : 0);
            PlayerPrefs.Save();
            if (Application.isPlaying) EnsureInstance()._sfxEnabled = on;
            if (LogSfx) Debug.Log($"[SFX] SFXENABLE -> {(on ? "ON" : "OFF")}");
        }

        public static void ToggleSfxEnabled() => SetSfxEnabled(!SfxEnabled);

        public static void SetSfxVolume(float v)
        {
            v = Mathf.Clamp01(v);
            PlayerPrefs.SetFloat(PrefSfxVolume, v);
            PlayerPrefs.Save();
            if (Application.isPlaying) EnsureInstance()._sfxVolume = v;
        }

        /// <summary>Play a one-shot SFX from SR_SOUND.XP. worldPos!=null → 3D positional (distance
        /// falloff); null → 2D UI. Respects SFXENABLE + master volume. Clips are decoded once and cached.</summary>
        public static void PlaySfx(string name, Vector3? worldPos = null, float volScale = 1f)
        {
            if (!Application.isPlaying || string.IsNullOrEmpty(name)) return;
            EnsureInstance().DoPlaySfx(name, worldPos, volScale);
        }

        /// <summary>UI window open/close chrome sound — the exact wavs the reference plays,
        /// WINDOWOPEN01.WAV / WINDOWCLOSE01.WAV (XProc_FirstLoad.cpp:5515, present in SR_SOUND.XP).
        /// Falls back to the small UI click only if a build's archive happens to lack them.</summary>
        public static void PlayWindowSfx(bool open)
        {
            if (!Application.isPlaying) return;
            EnsureInstance().DoPlayWindowSfx(open);
        }
        private void DoPlayWindowSfx(bool open)
        {
            string primary = open ? "WINDOWOPEN01.WAV" : "WINDOWCLOSE01.WAV";
            string name = (_assets != null && _assets.HasSound(primary)) ? primary : "SmallButtonClick.wav";
            DoPlaySfx(name, null, 1f);
        }

        private void DoPlaySfx(string name, Vector3? worldPos, float volScale)
        {
            if (!_sfxEnabled) return;
            var clip = GetSfxClip(name);
            if (clip == null) return;
            float vol = Mathf.Clamp01(_sfxVolume * volScale);
            if (worldPos.HasValue)
            {
                var s = _sfx3D[_sfx3DNext];
                _sfx3DNext = (_sfx3DNext + 1) % _sfx3D.Length;
                s.transform.position = worldPos.Value;
                s.PlayOneShot(clip, vol);
            }
            else
            {
                _sfx2D.PlayOneShot(clip, vol);
            }
            if (LogSfx)
                Debug.Log($"[SFX] {name}{(worldPos.HasValue ? " @world" : " (2D)")}: " +
                          $"{clip.length:0.##}s {clip.samples}smp {clip.frequency}Hz {clip.channels}ch vol={vol:0.##}");
        }

        private AudioClip GetSfxClip(string name)
        {
            if (_sfxClips.TryGetValue(name, out var c)) return c;
            if (_sfxMissing.Contains(name)) return null;
            byte[] wav = _assets != null ? _assets.GetSound(name) : null;
            if (wav == null || wav.Length == 0)
            {
                Debug.LogWarning($"[SFX] '{name}' not found in SR_SOUND.XP");
                _sfxMissing.Add(name);
                return null;
            }
            c = DecodeWav(wav, name);
            if (c == null) { _sfxMissing.Add(name); Debug.LogWarning($"[SFX] '{name}' decode failed (unsupported WAV)"); }
            else _sfxClips[name] = c;
            return c;
        }

        // Minimal RIFF/WAVE PCM decoder → AudioClip. The SR_SOUND.XP SFX are PCM 16-bit mono/stereo
        // at 22050/44100 Hz; 8-bit PCM and 32-bit IEEE float are also handled for safety.
        private static AudioClip DecodeWav(byte[] d, string name)
        {
            if (d == null || d.Length < 44) return null;
            if (d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F') return null;
            if (d[8] != 'W' || d[9] != 'A' || d[10] != 'V' || d[11] != 'E') return null;
            int fmt = 1, channels = 1, sampleRate = 22050, bits = 16, dataOff = -1, dataLen = 0;
            int p = 12;
            while (p + 8 <= d.Length)
            {
                int csz = d[p + 4] | (d[p + 5] << 8) | (d[p + 6] << 16) | (d[p + 7] << 24);
                if (d[p] == 'f' && d[p + 1] == 'm' && d[p + 2] == 't' && d[p + 3] == ' ')
                {
                    fmt = d[p + 8] | (d[p + 9] << 8);
                    channels = d[p + 10] | (d[p + 11] << 8);
                    sampleRate = d[p + 12] | (d[p + 13] << 8) | (d[p + 14] << 16) | (d[p + 15] << 24);
                    bits = d[p + 22] | (d[p + 23] << 8);
                }
                else if (d[p] == 'd' && d[p + 1] == 'a' && d[p + 2] == 't' && d[p + 3] == 'a')
                {
                    dataOff = p + 8; dataLen = csz;
                }
                if (csz < 0) break;
                p += 8 + csz + (csz & 1);
            }
            if (dataOff < 0 || channels < 1) return null;
            if (dataLen > d.Length - dataOff) dataLen = d.Length - dataOff;
            int bytesPer = bits / 8;
            if (bytesPer < 1) return null;
            int total = dataLen / bytesPer;          // interleaved samples across channels
            if (total < channels) return null;
            var f = new float[total];
            int o = dataOff;
            if (bits == 16)
                for (int i = 0; i < total; i++, o += 2)
                    f[i] = (short)(d[o] | (d[o + 1] << 8)) / 32768f;
            else if (bits == 8)
                for (int i = 0; i < total; i++, o += 1)
                    f[i] = (d[o] - 128) / 128f;
            else if (bits == 32 && fmt == 3)
                for (int i = 0; i < total; i++, o += 4)
                    f[i] = System.BitConverter.ToSingle(d, o);
            else
                return null;   // unsupported bit depth
            int frames = total / channels;
            var clip = AudioClip.Create(name, frames, channels, sampleRate, false);
            clip.SetData(f, 0);
            return clip;
        }
    }
}
