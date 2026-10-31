using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;
using NineDragons.Assets;
using NumQuat = System.Numerics.Quaternion;

namespace NineDragons.Client
{
    /// <summary>
    /// Plays a 9Dragons .XA motion onto a <see cref="Skeleton"/>, reproducing the client's
    /// XBipedController::FrameAnimation: each bone's local transform is the interpolated
    /// rotation (accumulated axis-angle-delta quaternions) + position keys; bones without keys
    /// keep their bind-local pose. Supports blending between an idle and a walk clip.
    /// </summary>
    public sealed class SkeletalAnimator : MonoBehaviour
    {
        public Skeleton Skel;
        public PlayerController Controller;   // optional: switches idle/walk (local player)
        public EntityMover Mover;             // optional: authoritative moving flag (other players/mobs)
        public float fps = 30f;

        private Clip _idle, _walk, _run, _current, _action;
        private float _frame; private float _actionTimer; private bool _playingAction;
        private Vector3 _lastWorldPos; private bool _posInit; private float _moveTimer;

        /// <summary>Mob move mode from the server (ucMobWalkMode: false = WALK, true = RUN). The moving
        /// clip is chosen from this exactly like the client (_XMob::SetAnimation _XMOBACTION_MOVE,
        /// XMob.cpp:1737-1771): RUN → run clip (walk fallback), WALK → walk clip (run fallback). Set
        /// per frame by NdGameClient from MonsterEntity.WalkMode; stays false for NPCs/other players.</summary>
        public bool RunMode;

        /// <summary>Give this instance a RANDOM initial animation phase (set for mobs). The native client
        /// creates each mob object as its MONSTER_EXIST packet arrives — staggered by a frame or more — so
        /// every mob sets m_AniFrame=StartFrame at a DIFFERENT instant and the crowd animates out of phase.
        /// Our client builds a whole group in ONE SyncEntities pass, so without this every same-type mob
        /// sits on the IDENTICAL frame and the bunch breathes/steps in lockstep ("cả đám hành động giống
        /// nhau", confirmed in Player.log: Anim_1201..1211 all frame=17.5). Offsetting the start frame once
        /// reproduces native's staggered-creation desync; independent per-mob clip switches keep it desynced.
        /// Players/NPCs leave this false (deterministic).</summary>
        public bool RandomPhase;
        private bool _phaseApplied;

        // One-shot overlay (attack / skill fire / mob attack / death). Mirrors the client's
        // SetCurMotion(motion, TRUE) for a single-fire motion: it plays once over idle/walk, then
        // the client returns to _XACTION_IDLE (SetStopAction). DEATH holds the last frame (the
        // client keeps the corpse on pMotion->EndFrame-1 until it fades, XMob.cpp:967).
        private Clip _oneShot; private bool _playingOneShot; private bool _oneShotHold;

        // Root-motion lock: some clips (attack lunges especially) translate the root bone Bip01 forward,
        // which slid the character across the ground ("lướt về trước") and into neighbouring mobs. The
        // character's WORLD position is driven by PlayerController/EntityMover, so every clip must play
        // IN PLACE — we keep the root's bind X/Z and let only its Y (crouch/bob) through.
        private int _rootId = -2; private Vector3 _rootBind; private Quaternion _rootBindRot;
        // Lock the root bone (Bip01) ROTATION to its bind pose, but ONLY while the IDLE clip plays. The
        // battle/peace idle stances bake a ~40° whole-body forward pitch into the Bip01 rotation, which
        // pitched a STOPPED character over ("ngừng chạy vẫn nghiêng người"). Locking it during idle keeps
        // a standing character upright. The WALK/RUN clips are NOT locked — their forward lean is the
        // natural running posture, and locking it flattened the run into a stiff, broken-looking stride
        // ("di chuyển/chạy lỗi"). So: upright when standing, natural lean when moving. Default ON.
        public static bool LockRootRot = true;
        /// <summary>This animator drives a MOB. Mob .xbp skeletons come out of the Unity parse with a
        /// spurious forward pitch baked into the bind pose (the client's row-normalised matrices keep a
        /// reflection that Unity's quaternion extraction can't), so a bandit stands leaning ~40° and some
        /// mobs invert (the "Fiend" handstand) — the native client stands them upright. We correct it by
        /// measuring the bind spine tilt and counter-rotating the whole model upright (see MeasureUpright /
        /// LateUpdate). Players keep this false (their skeleton parses correctly).</summary>
        public bool LockRootRotAlways;
        private int _pelvisId = -2, _headId = -2;   // humanoid-mob spine endpoints (cached; -1 = not humanoid)
        private int RootId()
        {
            if (_rootId == -2)
            {
                _rootId = -1;
                int id = -1;
                if (Skel != null && Skel.ByName != null && Skel.ByName.TryGetValue("Bip01", out var bid) &&
                    bid >= 0 && bid < Skel.Bones.Length)
                    id = bid;
                else if (Skel != null && Skel.Bones != null && Skel.BipRoot != null)
                    id = System.Array.IndexOf(Skel.Bones, Skel.BipRoot);   // non-humanoid mob: the actual root bone
                if (id >= 0 && id < Skel.Bones.Length)
                {
                    _rootId = id;
                    // Use the AUTHORITATIVE bind pose (captured at Skeleton.Build), NOT the live bone's
                    // current localRotation — in-game the bone may already hold a WALK frame (leaning) by
                    // the time this first runs, and locking to that made the idle lean. (The offline
                    // preview happened to capture it at bind, which is why only the live client leaned.)
                    _rootBind = Skel.BindLocalPos(id);
                    _rootBindRot = Skel.BindLocalRot(id);
                }
            }
            return _rootId;
        }
        /// <summary>A humanoid mob (Bip01 Pelvis + Head + Spine) — the ones whose Unity-parsed skeleton
        /// leans/inverts and must be straightened. Quadrupeds (Bip02, no Bip01 spine) return false and are
        /// left alone. Cached on first use.</summary>
        private bool IsHumanoidMob()
        {
            if (_pelvisId == -2)
            {
                _pelvisId = _headId = -1;
                if (LockRootRotAlways && Skel?.ByName != null && Skel.ByName.ContainsKey("Bip01 Spine") &&
                    Skel.ByName.TryGetValue("Bip01 Pelvis", out int p) && Skel.ByName.TryGetValue("Bip01 Head", out int h))
                { _pelvisId = p; _headId = h; }
            }
            return _pelvisId >= 0 && _headId >= 0;
        }

        private Quaternion _uprightFix = Quaternion.identity;   // last applied correction (for the diagnostic)

        /// <summary>Straighten a standing humanoid mob AFTER its pose is applied. The mob .xbp comes out of
        /// the Unity parse with a spurious forward pitch in the IDLE pose (native stays upright), so while
        /// the mob is idle we measure the animated spine tilt (pelvis→head) and counter-rotate the whole
        /// model so the spine is vertical. Run/walk clips keep their natural lean (no correction while
        /// moving). Measured every frame off the UN-corrected pose (localRotation cleared first) so it
        /// tracks any idle sway and never accumulates.</summary>
        private void LateUpdate()
        {
            if (Skel == null || !Play || !LockRootRotAlways) return;
            // A mob now plays its pose EXACTLY like the native client (the root-rotation lock above is
            // player-only), so the model transform carries NO pitch correction — a bandit leans just like
            // the real client, a fox stands like the real client, etc. Keep this transform at identity so
            // no stale correction from an earlier build lingers. The ONE thing we still catch is a GROSS
            // inversion: a mis-parsed skeleton that stands a mob on its head (head below the feet). A normal
            // forward/back lean (head still above the feet) is the native pose and is left untouched.
            transform.localRotation = Quaternion.identity;
            _uprightFix = Quaternion.identity;
            if (!IsHumanoidMob() || _current != _idle || _playingOneShot) return;
            Vector3 up = transform.InverseTransformDirection(Skel.Bones[_headId].position - transform.position);
            if (up.y >= 0f) return;                             // head above feet → native pose, no touch
            up.x = 0f;                                          // inverted: flip the forward/back axis back up
            if (up.sqrMagnitude < 1e-4f) return;
            _uprightFix = Quaternion.FromToRotation(up.normalized, Vector3.up);
            transform.localRotation = _uprightFix;
        }

        private void SetBonePose(Track tr)
        {
            var bone = Skel.Bones[tr.BoneId];
            if (tr.Rot.Length > 0)
            {
                var r = SampleRot(tr, _frame);
                // Lock the root pitch to bind while STANDING (idle) — but ONLY for the PLAYER/NPC, never a
                // mob. The player's battle/peace idle bakes a ~40° whole-body pitch into Bip01 that the chủ
                // wants flattened to upright; a MOB must play its root rotation exactly like the native
                // client (_XBipedController::FrameAnimation applies the clip's root rotation verbatim, no
                // bind substitution), so a bandit keeps the real client's carrying-lean instead of the
                // half-straightened "nghiêng nửa chừng" the lock used to force. LockRootRotAlways marks a mob.
                if (tr.BoneId == RootId() && LockRootRot && !LockRootRotAlways && _current == _idle && !_playingOneShot)
                    r = _rootBindRot;
                bone.localRotation = r;
            }
            if (tr.Pos.Length > 0)
            {
                var p = SamplePos(tr.Pos, _frame);
                // Pin the root's X/Z to bind for WALK/RUN (their clip translates the root forward — the
                // world position drives real movement) and for attack lunges. But NOT during pure IDLE:
                // the standing idle sways the root a little while the feet stay planted, so pinning it
                // forced the legs to push the feet forward/back → the feet slid (chủ: "đứng idle 2 bàn
                // chân trượt tới lui"). Letting the idle root sway keeps the feet planted.
                if (tr.BoneId == RootId() && !(_current == _idle && !_playingOneShot))
                    p = new Vector3(_rootBind.x, p.y, _rootBind.z);   // lock X/Z in place
                bone.localPosition = p;
            }
        }

        // Reset EVERY bone to its bind pose before applying the current clip's tracks. Without this, a bone
        // animated by the previous clip but NOT by the current one keeps the previous clip's last value —
        // so switching from the khinh công OVERRUN clip (which bends the spine forward + pumps the arms) to
        // the idle clip (which has no track for those bones) left the character leaning with fists and a
        // floating weapon. Bind arrays are cached once; the per-frame reset is a cheap transform write.
        private Quaternion[] _bindRotC; private Vector3[] _bindPosC;
        private void ResetToBind()
        {
            if (Skel == null || Skel.Bones == null) return;
            if (_bindRotC == null)
            {
                int n = Skel.Bones.Length;
                _bindRotC = new Quaternion[n]; _bindPosC = new Vector3[n];
                for (int i = 0; i < n; i++) { _bindRotC[i] = Skel.BindLocalRot(i); _bindPosC[i] = Skel.BindLocalPos(i); }
            }
            var bones = Skel.Bones;
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b != null) { b.localPosition = _bindPosC[i]; b.localRotation = _bindRotC[i]; }
            }
        }

        private sealed class Track
        {
            public int BoneId;
            public XaPosKey[] Pos;
            public XaRotKey[] Rot;
            public NumQuat[] AbsRot;   // accumulated absolute rotation per rot key (D3D)
        }

        private sealed class Clip
        {
            public float First, Last;
            public readonly List<Track> Tracks = new List<Track>();
        }

        public void SetClips(XaAnimation idle, XaAnimation walk)
        {
            _idle = Build(idle);
            _walk = Build(walk);
            _current = _idle ?? _walk;
        }

        /// <summary>Name-based clip setup with the glTF overlay: a converted Data\Model\gltf_anim\&lt;name&gt;.glb
        /// (edited in Blender) wins over the .XA — the modern animation edit path (hướng A).</summary>
        public void SetClips(AssetService assets, string idleName, string walkName)
        {
            _idle = Resolve(assets, idleName);
            _walk = Resolve(assets, walkName);
            _current = _idle ?? _walk;
        }

        /// <summary>Mob setup: idle + a SEPARATE walk and run clip (MobModel.XMS WALK/RUN tags). The
        /// moving clip is then chosen per frame from <see cref="RunMode"/>. Either move clip may be
        /// null (a mob with only walk or only run); the Update fallback plays the other one.</summary>
        public void SetClips(AssetService assets, string idleName, string walkName, string runName)
        {
            _idle = Resolve(assets, idleName);
            _walk = Resolve(assets, walkName);
            _run  = Resolve(assets, runName);
            _current = _idle ?? _walk ?? _run;
        }

        public void SetAction(AssetService assets, string name) { _action = Resolve(assets, name); _actionTimer = UnityEngine.Random.Range(3f, 8f); }

        /// <summary>Self-contained model: play the animation(s) baked INTO the model .glb itself
        /// (a full Blender build — mesh + rig + animation, no .xbp / no .XA). animations[0] is the
        /// idle loop; animations[1], if present, is the walk. Bones matched to <see cref="Skel"/> by
        /// node name.</summary>
        public void SetSelfClips(byte[] modelGlb)
        {
            _idle = BuildFromGlb(modelGlb, 0);
            _walk = BuildFromGlb(modelGlb, 1);
            _current = _idle ?? _walk;
        }

        private Clip Resolve(AssetService assets, string name)
        {
            if (assets != null && !string.IsNullOrEmpty(name))
            {
                var glb = assets.GlbAnimBytes(name);
                if (glb != null) { var c = BuildFromGlb(glb); if (c != null) { Debug.Log($"[GLTF-ANIM] '{name}' from gltf_anim glb (tracks={c.Tracks.Count})"); return c; } }
            }
            return Build(assets != null ? assets.GetAnimation(name) : null);
        }

        /// <summary>Build a Clip from a converted .glb animation (glTF channels), inverting the exporter's
        /// coordinate map: rotation abs = (x, y, -z, w) of the glTF quaternion, position = (x, y, -z),
        /// time(sec) → frame = sec * fps. Bones matched to the skeleton by node name.</summary>
        private Clip BuildFromGlb(byte[] glb, int animIndex = 0)
        {
            if (glb == null || Skel == null) return null;
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
                var anims = root["animations"] as JArray;
                if (anims == null || animIndex < 0 || animIndex >= anims.Count) return null;
                var accessors = (JArray)root["accessors"]; var views = (JArray)root["bufferViews"]; var nodes = (JArray)root["nodes"];
                (int off, int len) View(int bv) { var v = (JObject)views[bv]; return ((int?)v["byteOffset"] ?? 0, (int)v["byteLength"]); }
                float[] F(int acc, int comp) { var a = (JObject)accessors[acc]; var (vo, _) = View((int)a["bufferView"]);
                    int st = vo + ((int?)a["byteOffset"] ?? 0), c = (int)a["count"]; var r = new float[c * comp];
                    for (int i = 0; i < r.Length; i++) r[i] = BitConverter.ToSingle(bin, st + i * 4); return r; }

                var an = (JObject)anims[animIndex];
                var samplers = (JArray)an["samplers"]; var channels = (JArray)an["channels"];
                var byBone = new Dictionary<int, Track>();
                float first = float.MaxValue, last = float.MinValue;
                foreach (JObject ch in channels)
                {
                    var tgt = (JObject)ch["target"]; int node = (int)tgt["node"]; string path = (string)tgt["path"];
                    string bname = (string)nodes[node]["name"];
                    if (bname == null || !Skel.ByName.TryGetValue(bname, out int boneId)) continue;
                    var sm = (JObject)samplers[(int)ch["sampler"]];
                    var times = F((int)sm["input"], 1);
                    if (times.Length == 0) continue;
                    for (int i = 0; i < times.Length; i++) { float fr = times[i] * fps; if (fr < first) first = fr; if (fr > last) last = fr; }
                    if (!byBone.TryGetValue(boneId, out var tr)) { tr = new Track { BoneId = boneId, Pos = System.Array.Empty<XaPosKey>(), Rot = System.Array.Empty<XaRotKey>(), AbsRot = System.Array.Empty<NumQuat>() }; byBone[boneId] = tr; }
                    if (path == "rotation")
                    {
                        var q = F((int)sm["output"], 4);
                        var rot = new XaRotKey[times.Length]; var abs = new NumQuat[times.Length];
                        for (int i = 0; i < times.Length; i++)
                        { rot[i].Time = times[i] * fps; abs[i] = new NumQuat(q[i*4], q[i*4+1], -q[i*4+2], q[i*4+3]); }   // glTF -> client abs
                        tr.Rot = rot; tr.AbsRot = abs;
                    }
                    else if (path == "translation")
                    {
                        var v = F((int)sm["output"], 3);
                        var pos = new XaPosKey[times.Length];
                        for (int i = 0; i < times.Length; i++)
                        { pos[i].Time = times[i] * fps; pos[i].Value = new System.Numerics.Vector3(v[i*3], v[i*3+1], -v[i*3+2]); }
                        tr.Pos = pos;
                    }
                }
                if (byBone.Count == 0) return null;
                var clip = new Clip { First = first, Last = Mathf.Max(first + 1f, last) };
                foreach (var tr in byBone.Values) clip.Tracks.Add(tr);
                return clip;
            }
            catch (Exception e) { if (SkinnedCharacterFactory.LogAnim) Debug.Log($"[GLTF-ANIM] parse fail: {e.Message}"); return null; }
        }

        /// <summary>Optional gesture clip (NPC IDLEACTION) played once every few seconds while idle.</summary>
        public void SetAction(XaAnimation a)
        {
            _action = Build(a);
            _actionTimer = UnityEngine.Random.Range(3f, 8f);
        }

        /// <summary>Swap the idle stance clip at runtime — the peace/battle toggle (SetBattleMode →
        /// SetAnimation(_XACTION_IDLE), XUser.cpp:503). Only takes effect when not moving / not in a
        /// one-shot, so the character visibly changes its resting pose.</summary>
        public void SetIdle(XaAnimation idle)
        {
            var built = Build(idle);
            if (built == null) return;
            bool wasIdle = _current == _idle;
            _idle = built;
            if (wasIdle && !_playingOneShot) { _current = _idle; _frame = _current.First; }
        }

        /// <summary>Swap the walk/run clip at runtime — the run motion changes with weapon class + stance
        /// (sword peace vs battle, XActionTable.h). Takes effect immediately if the character is running.</summary>
        public void SetWalk(XaAnimation walk)
        {
            var built = Build(walk);
            if (built == null) return;
            bool wasWalk = _current == _walk;
            _walk = built;
            if (wasWalk && !_playingOneShot) { _current = _walk; _frame = _current.First; }
        }

        /// <summary>Play a motion once over idle/walk, then return to idle (attack swing, skill fire,
        /// mob attack). With <paramref name="holdLast"/> the last frame is held instead (death /
        /// corpse). Mirrors _XModelDescriptor::SetCurMotion(index, TRUE).</summary>
        public void PlayOneShot(XaAnimation clip, bool holdLast = false)
        {
            var built = Build(clip);
            if (built == null)
            {
                if (SkinnedCharacterFactory.LogAnim)
                    Debug.Log($"[ONESHOT] {name}: clip built NULL (no bone tracks matched) — cannot play");
                return;
            }
            _oneShot = built;
            _oneShotHold = holdLast;
            _playingOneShot = true;
            _current = _oneShot;
            _frame = _current.First;
            if (SkinnedCharacterFactory.LogAnim)
                Debug.Log($"[ONESHOT] {name}: START frames[{_oneShot.First:0}..{_oneShot.Last:0}] tracks={_oneShot.Tracks.Count} hold={holdLast}");
        }

        /// <summary>True while a one-shot (attack/skill/death) is still playing — lets a driver avoid
        /// interrupting an in-flight swing, like the client's CheckAnimationPriority.</summary>
        public bool IsPlayingOneShot => _playingOneShot;

        private Clip Build(XaAnimation a)
        {
            if (a == null || Skel == null) return null;
            var clip = new Clip { First = a.FirstFrame, Last = Mathf.Max(a.FirstFrame + 1, a.LastFrame) };
            foreach (var t in a.Tracks)
            {
                if (!Skel.ByName.TryGetValue(t.Name, out int boneId)) continue;
                var abs = t.AccumulatedRotations();
                if (SkinnedCharacterFactory.LogParts && (boneId == 12 || boneId == 13 || boneId == 14 || boneId == 32 || boneId == 33 || boneId == 34))
                    Debug.Log($"[KEYTIME] bone {boneId} {t.Name}: rotKeys={t.RotKeys.Length} firstTime={(t.RotKeys.Length > 0 ? t.RotKeys[0].Time : -1)} prepend={(t.RotKeys.Length > 0 && t.RotKeys[0].Time != 0f)}");
                // The client inserts a frame-0 key = the bone's BIND-LOCAL rotation ONLY when the
                // first real key's time != 0 (XBipedController::LoadKey). When the first key is at
                // time 0 — every player limb bone here — it does NOT: that frame-0 key already IS
                // the bone's local rotation. Prepending bindLocal in that case DOUBLE-applies the
                // rest pose and twists the whole body (the bug behind the flailing/bent poses).
                // Gate the prepend on firstTime != 0, matching the client exactly.
                if (abs.Length > 0 && t.RotKeys.Length > 0 && PrependBind && t.RotKeys[0].Time != 0f)
                {
                    var bl = Skel.BindLocalRot(boneId);
                    var blD3D = new NumQuat(-bl.x, -bl.y, -bl.z, bl.w);   // Unity → D3D (undo conjugate)
                    for (int k = 0; k < abs.Length; k++) abs[k] = QMul(blD3D, abs[k]);
                }
                clip.Tracks.Add(new Track
                {
                    BoneId = boneId,
                    Pos = t.PosKeys,
                    Rot = t.RotKeys,
                    AbsRot = abs
                });
            }
            return clip.Tracks.Count > 0 ? clip : null;
        }

        /// <summary>Global: play the skeletal .XA (idle/walk). ON by default now that the frame-0
        /// bind-prepend bug is fixed (see Build) — the idle is a natural standing pose. `-ndNoAnim`
        /// forces the static bind pose for debugging.</summary>
        public static bool Play = true;

        private void Update()
        {
            if (Skel == null || !Play) return;

            // One-shot overlay takes priority over idle/walk/gesture (the client's single-fire
            // SetCurMotion). It runs to its last frame, then either holds (death) or falls back to
            // idle/walk (attack, skill fire).
            if (_playingOneShot && _oneShot != null)
            {
                if (_current != _oneShot) { _current = _oneShot; _frame = _current.First; }
                _frame += Time.deltaTime * fps;
                if (_frame >= _current.Last)
                {
                    if (_oneShotHold)
                    {
                        _frame = _current.Last;   // corpse: hold the death pose
                        ApplyPose();
                        return;
                    }
                    // swing/skill finished → resume idle/walk next frame
                    _playingOneShot = false; _oneShot = null;
                    _current = _idle ?? _walk;
                    if (_current == null) return;
                    _frame = _current.First;
                }
                else { ApplyPose(); return; }
            }

            // ACTUAL world motion (u/s) — the single source of truth for "is this thing walking". A moving
            // flag alone (Controller.IsMoving / Mover.Moving) can stick true (an interrupted khinh công
            // run, or a server stream that keeps nudging an idle mob), freezing the character in the
            // leaning one-leg-up walk stride while it visibly stands still. Requiring real displacement
            // guarantees a standing character shows idle, never the walk pose.
            Vector3 wp = transform.position;
            if (!_posInit) { _lastWorldPos = wp; _posInit = true; }
            float csp = (wp - _lastWorldPos).magnitude / Mathf.Max(1e-4f, Time.deltaTime);
            _lastWorldPos = wp;

            bool moving;
            if (Controller != null)
            {
                // player: client-driven position, no server jitter — a low threshold is safe.
                if (csp > 0.4f) _moveTimer = 0.15f; else _moveTimer -= Time.deltaTime;
                moving = Controller.IsMoving && _moveTimer > 0f;
                if (SkinnedCharacterFactory.LogAnim && Time.time - _dbgAt > 1f)
                { _dbgAt = Time.time; Debug.Log($"[PLAYERANIM] IsMoving={Controller.IsMoving} csp={csp:0.00} moving={moving} clip={(_current == _idle ? "idle" : _current == _walk ? "walk" : _playingOneShot ? "oneshot" : "?")}"); }
            }
            else if (Mover != null)
            {
                // other players / mobs: the interpolator's flag AND real displacement. The OLD 1.0 u/s
                // floor assumed every mob walks fast (≈2.2), but each mob's move speed is its OWN MobModel
                // _XM_WALKSPEED/_XM_RUNSPEED — a fox (mob_etc_001) WALKS at 0.0003 u/ms = 0.3 u/s (RUN 2.5).
                // A fox creeping at 0.3 never cleared 1.0, so it read as idle and slid with its 4 legs frozen
                // (chủ: "con cáo toàn trượt, ko cử động 4 chân"). Mover.Moving is already authoritative (true
                // only while stepping toward the destination; false = snapped onto it, csp exactly 0), so a
                // tiny 0.05 floor is all that's needed — it clears an arrived mob's zero jitter yet lets the
                // slowest real walk (~0.1 u/s) through. The 0.25s hold bridges per-frame timing dips.
                if (csp > 0.05f) _moveTimer = 0.25f; else _moveTimer -= Time.deltaTime;
                moving = Mover.Moving && _moveTimer > 0f;
                if (LockRootRotAlways && _dbgCount < 2 && Time.time - _dbgAt > 3f)   // mob diag (default on, 2×)
                {
                    _dbgAt = Time.time; _dbgCount++;
                    string clip = _current == _idle ? "idle" : _current == _walk ? "walk" : _current == _run ? "run" : _playingOneShot ? "oneshot" : "?";
                    int rid = RootId();
                    string rn = (rid >= 0 && Skel != null && rid < Skel.Bones.Length) ? Skel.Bones[rid].name : "(none)";
                    var fx = _uprightFix.eulerAngles;
                    Debug.Log($"[MOBANIM] {name} clip={clip} frame={_frame:0.0} tracks={_current?.Tracks.Count} uprightFix=({fx.x:0},{fx.y:0},{fx.z:0})");
                }
            }
            else
            {
                // static NPCs / no interpolator: pure world-position delta.
                if (csp > 0.6f) _moveTimer = 0.2f; else _moveTimer -= Time.deltaTime;
                moving = _moveTimer > 0f;
            }
            Clip want;
            if (moving)
            {
                // walk vs run picked by the server move mode (ucMobWalkMode), exactly like the client
                // (_XMob::SetAnimation _XMOBACTION_MOVE, XMob.cpp:1737-1771): RUN → run clip (walk
                // fallback), WALK → walk clip (run fallback). Each clip is authored for its matching
                // move speed (walk 2.2 / run 7.5 u/s, MobSpeed), so the legs track the ground instead
                // of the run-speed slide the old single-move-clip caused. Players/NPCs keep RunMode
                // false and _run null → this is just _walk, unchanged.
                want = (RunMode ? (_run ?? _walk) : (_walk ?? _run)) ?? _idle;
                _playingAction = false;
            }
            else if (_playingAction) { want = _action; }
            else
            {
                want = _idle ?? _walk;
                if (_action != null)
                {
                    _actionTimer -= Time.deltaTime;
                    if (_actionTimer <= 0f) { _playingAction = true; want = _action; }
                }
            }
            if (want == null) return;
            if (want != _current) { _current = want; _frame = _current.First; }

            // Stagger the crowd: give a mob a one-time random start frame so same-type mobs built together
            // don't animate in lockstep (native desyncs naturally via staggered mob creation). Applied once,
            // on the first looping clip this mob reaches; attacks (one-shots) are handled above and untouched.
            if (RandomPhase && !_phaseApplied)
            {
                _frame = _current.First + UnityEngine.Random.Range(0f, Mathf.Max(0.01f, _current.Last - _current.First));
                _phaseApplied = true;
            }

            _frame += Time.deltaTime * fps;
            if (_frame > _current.Last)
            {
                if (_playingAction && _current == _action)
                {
                    // one-shot gesture finished → back to idle, schedule the next gesture
                    _playingAction = false;
                    _actionTimer = UnityEngine.Random.Range(5f, 12f);
                    _current = _idle ?? _walk;
                    if (_current == null) return;
                    _frame = _current.First;
                }
                else _frame = _current.First + (_frame - _current.Last);
            }

            ResetToBind();   // clear any bones the previous clip animated but this one doesn't
            foreach (var tr in _current.Tracks) SetBonePose(tr);

            if (SkinnedCharacterFactory.LogAnim && Controller == null && _dbgCount < 5 && Time.time - _dbgAt > 0.4f)
            {
                _dbgAt = Time.time; _dbgCount++;
                if (_dbgPrev == null) _dbgPrev = new Quaternion[_current.Tracks.Count];
                float maxDeg = 0f; int maxBone = -1;
                for (int i = 0; i < _current.Tracks.Count; i++)
                {
                    var tr = _current.Tracks[i];
                    if (tr.Rot.Length == 0) continue;
                    var q = SampleRot(tr, _frame);
                    if (_dbgCount > 1) { float a = Quaternion.Angle(_dbgPrev[i], q); if (a > maxDeg) { maxDeg = a; maxBone = tr.BoneId; } }
                    _dbgPrev[i] = q;
                }
                if (_dbgCount > 1) Debug.Log($"[ANIMPEAK] {name} frame={_frame:0.0}/{_current.Last:0} maxMove={maxDeg:0.0}deg @bone{maxBone} (per 0.4s)");
                // camera-independent proof: bake the skinned mesh and see if a vertex actually moves
                if (_dbgSmr == null) _dbgSmr = GetComponentInChildren<SkinnedMeshRenderer>();
                if (_dbgSmr != null)
                {
                    if (_dbgBake == null) _dbgBake = new Mesh();
                    _dbgSmr.BakeMesh(_dbgBake);
                    var vs = _dbgBake.vertices;
                    if (vs.Length > 0)
                    {
                        int k = vs.Length / 2;
                        if (_dbgCount > 1) Debug.Log($"[MESHBAKE] {name} vert{k}=({vs[k].x:0.000},{vs[k].y:0.000},{vs[k].z:0.000}) delta={(_dbgPrevVert - vs[k]).magnitude:0.0000}");
                        _dbgPrevVert = vs[k];
                    }
                }
            }
        }
        private float _dbgAt; private int _dbgCount; private Quaternion[] _dbgPrev;
        private SkinnedMeshRenderer _dbgSmr; private Mesh _dbgBake; private Vector3 _dbgPrevVert;

        // D3D quaternion → Unity local rotation. ToUnity(matrixFromQuaternion) reduces to the
        // CONJUGATE (the D3D row-vector rotation seen column-vector). This is source-accurate; the
        // earlier "collapse" was NOT the sign but the MISSING frame-0 bind-local key (see Build).
        public static int QuatMode;      // debug: 1 = raw, else conjugate
        public static bool PrependBind = true;   // compose bind-local rotation into the animation (client behaviour)
        private static Quaternion ToU(NumQuat q) => QuatMode == 1
            ? new Quaternion(q.X, q.Y, q.Z, q.W)
            : new Quaternion(-q.X, -q.Y, -q.Z, q.W);

        /// <summary>Hamilton product a ⊗ b, matching the client's _XMeshMath_QuaternionMultiply.</summary>
        private static NumQuat QMul(NumQuat a, NumQuat b) => new NumQuat(
            a.X * b.W + a.Y * b.Z - a.Z * b.Y + a.W * b.X,
            -a.X * b.Z + a.Y * b.W + a.Z * b.X + a.W * b.Y,
            a.X * b.Y - a.Y * b.X + a.Z * b.W + a.W * b.Z,
            -a.X * b.X - a.Y * b.Y - a.Z * b.Z + a.W * b.W);

        /// <summary>Write the current clip's pose at <c>_frame</c> onto the skeleton bones.</summary>
        private void ApplyPose()
        {
            if (_current == null) return;
            ResetToBind();   // clear bones the previous clip animated but this one doesn't
            foreach (var tr in _current.Tracks) SetBonePose(tr);
        }

        private Quaternion SampleRot(Track tr, float frame)
        {
            var k = tr.Rot; var abs = tr.AbsRot;
            if (frame <= k[0].Time) return ToU(abs[0]);
            for (int i = 0; i < k.Length - 1; i++)
            {
                if (frame >= k[i].Time && frame <= k[i + 1].Time)
                {
                    float u = (frame - k[i].Time) / Mathf.Max(1e-4f, k[i + 1].Time - k[i].Time);
                    return Quaternion.Slerp(ToU(abs[i]), ToU(abs[i + 1]), u);
                }
            }
            return ToU(abs[abs.Length - 1]);
        }

        private Vector3 SamplePos(XaPosKey[] p, float frame)
        {
            if (frame <= p[0].Time) return V(p[0].Value);
            for (int i = 0; i < p.Length - 1; i++)
            {
                if (frame >= p[i].Time && frame <= p[i + 1].Time)
                {
                    float u = (frame - p[i].Time) / Mathf.Max(1e-4f, p[i + 1].Time - p[i].Time);
                    return Vector3.Lerp(V(p[i].Value), V(p[i + 1].Value), u);
                }
            }
            return V(p[p.Length - 1].Value);
        }

        private static Vector3 V(System.Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
