using UnityEngine;
using UnityEngine.EventSystems;
using Nd.Session;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// Click-to-move like the Windows client: click the ground → the character walks there,
    /// facing the direction of travel, clamped to the terrain surface. Movement is
    /// client-predicted and streamed to the server as ZONE_MOVE (M5 <see cref="GameSession"/>);
    /// a server position correction (packet 25) snaps us back if we drift too far.
    ///
    /// Speed from the client: _XDEF_CHARACTERMOVESPEED_RUN = 0.0042 units/ms (XPathFinder.h),
    /// i.e. 4.2 u/s; walk 2.2 u/s. Position integrates as elapsed_ms * movespeed (XUser.cpp).
    /// </summary>
    public sealed class PlayerController : MonoBehaviour
    {
        public GameSession Session;
        public XtvTerrain Terrain;
        public Camera Cam;

        public float runSpeed = 4.2f;
        public float walkSpeed = 2.2f;
        public float turnSpeed = 720f;         // deg/sec
        public float arriveEpsilon = 0.15f;
        public float sendInterval = 0.1f;       // ZONE_MOVE cadence
        public float snapThreshold = 6f;        // server correction distance to hard-snap

        public bool Running = true;
        public bool IsMoving { get; private set; }

        // This old game LOCKS movement while acting: a swing or a draw/sheathe stance change roots the
        // character in place (no walking/sliding). Toggle off (LockMoveWhileActing=false) to allow
        // move-cancel/animation-cancel later; keep ON for authentic behaviour. "Acting" = the player's
        // SkeletalAnimator is mid one-shot (attack / draw / sheathe).
        public bool LockMoveWhileActing = true;
        private SkeletalAnimator _anim; private bool _animSearched;
        private SkeletalAnimator Anim()
        {
            if (_anim == null && !_animSearched) { _animSearched = true; _anim = GetComponent<SkeletalAnimator>(); }
            return _anim;
        }
        private bool Acting => LockMoveWhileActing && Anim() != null && Anim().IsPlayingOneShot;

        // Peace stance blocks attacks (this old game: "One cannot use Kung Fu in Peace Mode"). The
        // battle/peace stance lives on AnimDriver; found lazily (it's created when the world loads).
        private AnimDriver _animDriver; private bool _adSearched;
        private AnimDriver AnimD()
        {
            if (_animDriver == null && !_adSearched) { _adSearched = true; _animDriver = Object.FindFirstObjectByType<AnimDriver>(); }
            if (_animDriver == null) _animDriver = Object.FindFirstObjectByType<AnimDriver>();   // retry until it exists
            return _animDriver;
        }

        // Honor the .xtv walk/collision grid like the C++ client (block walls/water/cliffs). The
        // server keeps the same grid (nd_tile.h) and validates monster moves against it, so honoring
        // it keeps the player where client and server agree it can stand. Toggle off only to debug.
        public bool HonorWalkGrid = true;

        // --- combat: click a monster to target + auto-attack it ---
        public float attackRange = 2.6f;
        public float attackInterval = 0.6f;
        private ushort _targetMob;    // ATTACK target (m_AttackTarget): the mob we chase + hit
        // The target mob's RENDERED transform (where it is DRAWN). Combat aims here — NOT the stale packet
        // mob.X/mob.Z. EntityMover interpolates a mob toward its DESTINATION (GotoX,GotoZ), so between the
        // sparse MONSTER_MOVE packets the mob is drawn near its Goto while mob.X/Z still holds the LAST
        // packet's start — often on the OPPOSITE side. Aiming mob.X/Z made the player run/swing AWAY from
        // the visible mob (server still hit the right id, so it died where it was drawn — chủ 14/09
        // "click con hướng A, chạy hướng ngược, con hướng A vẫn chết").
        private Transform _targetMobT;
        private ushort _viewTarget;   // VIEW target  (m_ViewTarget):   the mob shown in the status frame
        private float _atkTimer;
        private int _atkSeq = 1;
        // Skill activation is a SINGLE STRIKE (nhất kích): run to the target, land ONE hit with the
        // armed skill, then stop — never a continuous auto-loop (chủ: "click 1 lần thì chỉ đánh 1 cái").
        // Set by EngageWithSkill; cleared the instant the one strike fires.
        private bool _castOnce;
        public bool Attacking => _targetMob != 0;
        /// <summary>The SELECTED (view) monster id (0 = none) — read by the top target-status frame
        /// (<see cref="TargetFrame"/>). Mirrors the client's <c>m_ViewTarget</c>, which is SEPARATE from
        /// the attack target: clicking the ground cancels the attack but KEEPS this selection, so the
        /// status frame stays up until you close it [x], press ESC, or the mob dies/despawns
        /// (XLocalUser.cpp:1867-1878, XProc_MainGame.cpp:1114-1156).</summary>
        public ushort CurrentTarget => _viewTarget;

        /// <summary>Drop both the view + attack target (the [x] close button and the ESC key), like the
        /// client resetting m_ViewTarget/m_AttackTarget together.</summary>
        public void ClearTarget() { _viewTarget = 0; _targetMob = 0; _targetMobT = null; _npcTarget = null; _viewNpc = null; }

        // NPC the player clicked and is walking up to. On arrival the player faces it and OnNpcReached
        // fires (opens the NPC conversation). Cleared by a ground/mob click or WASD (manual override).
        private NpcId _npcTarget;
        // NPC currently SELECTED (clicked) — shown by name in the top target-status frame, exactly like a
        // clicked mob (the client sets m_ViewTarget = the NPC; DrawNPC shows name + a full bar + glyph,
        // XWindow_MonsterStatus.cpp:1636). Persists (like the mob view target) until ESC / another pick.
        private NpcId _viewNpc;
        public NpcId ViewNpc => _viewNpc;
        public float npcTalkRange = 3.2f;   // _XDEF_DISTANCEOFSTARTNPCSCRIPT (approach distance)
        /// <summary>Raised when the player reaches a clicked NPC — the host opens its conversation.</summary>
        public System.Action<NpcId> OnNpcReached;

        // Walk up to the clicked NPC, face it, and open its conversation once in range (mirrors
        // StartNPCScript + m_bAutoNPCScriptPopUp, XProc_MainGame.cpp:9041-9047).
        private void UpdateNpcApproach()
        {
            if (_npcTarget == null) return;
            if (_npcTarget == null || _npcTarget.transform == null) { _npcTarget = null; return; }
            if (Acting) return;

            var pos = transform.position;
            var np = _npcTarget.transform.position;
            var to = new Vector3(np.x - pos.x, 0f, np.z - pos.z);
            float dist = to.magnitude;
            // face the NPC (mesh front is -forward, same convention as combat facing)
            var faceRot = to.sqrMagnitude > 0.0004f
                ? Quaternion.LookRotation(-to.normalized, Vector3.up) : transform.rotation;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, faceRot, turnSpeed * Time.deltaTime);

            if (dist > npcTalkRange)
            {
                _dest = new Vector3(np.x, GroundY(np.x, np.z), np.z);
                IsMoving = true;   // keep walking toward the NPC
            }
            else
            {
                IsMoving = false;
                transform.rotation = faceRot;      // snap to face it
                var npc = _npcTarget; _npcTarget = null;
                OnNpcReached?.Invoke(npc);          // open the conversation once
            }
        }

        private Vector3 _dest;
        private float _sendTimer;
        private float _lastSentX, _lastSentZ;

        // Khinh công (light-body / _XMOVETYPE_KYUNGGONG fast run): hold ALT while moving, WITH a learned
        // khinh công skill (stype 3), to travel at the hyper speed. The client flips movetype to KYUNGGONG
        // and runs faster (XLocalUser.cpp:5010-5013); nd-world never echoes ZONE_MOVE, so — exactly like
        // the normal 4.2 u/s run — the Unity client predicts this speed locally. No such skill learned →
        // ALT is inert. AnimDriver reads <see cref="KhinhCong"/> to swap the run loop for the FASTMOVE clip.
        public float khinhCongSpeed = 7.6f;    // ~1.8× the normal run; light-body arts roughly double travel
        private bool AltHeld => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        public static bool TestKhinhCong;      // -ndTestKhinhCong: auto-arm 3..7s then release (verify no stuck lean)
        private bool TestKcActive => TestKhinhCong && _enableTime >= 0f
                                     && (Time.time - _enableTime) >= 3f && (Time.time - _enableTime) < 7f;
        private bool KhinhCongArmed => (AltHeld && Session != null && Session.HasHyperRun) || TestKcActive;
        /// <summary>True while the player is actually fast-running (armed + moving) — drives the FASTMOVE
        /// animation. The speed boost itself uses <see cref="KhinhCongArmed"/> (applies the instant a step
        /// starts, before IsMoving latches).</summary>
        public bool KhinhCong => KhinhCongArmed && IsMoving;
        private bool _wasKhinhCong;   // ALT was held last frame (to detect the release → stop promptly)

        private float Speed => KhinhCongArmed ? khinhCongSpeed : (Running ? runSpeed : walkSpeed);

        // Khinh công movement, camera-driven (source: the client runs while ALT is held and re-derives the
        // 3P camera every frame — g_LodTerrain.m_3PCamera.UpdateViewMatrix, XLocalUser.cpp:5059). The
        // character runs FORWARD in the CAMERA's facing direction; you steer by rotating the camera with
        // the mouse ("rê chuột đổi hướng"). Camera-forward is a stable heading — unlike chasing the cursor's
        // ground point, which slides as the camera tracks the player, so the run never "arrives" and the
        // leaning run pose sticks. Hold ALT = keep running; release = stop.
        private void HandleKhinhCong()
        {
            if (!KhinhCongArmed || Acting || Cam == null) return;
            if (_keyMoving) return;   // WASD is driving a camera-relative khinh công run instead
            Vector3 fwd = Cam.transform.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) { IsMoving = false; return; }
            fwd.Normalize();
            var pos = transform.position;
            var aim = pos + fwd * 6f;   // a heading point ahead in the camera direction (re-aimed each frame)
            // blocked ahead (wall/water) → stop rather than run in place against it
            if (HonorWalkGrid && Terrain != null && !Terrain.IsWalkable(aim.x, aim.z)) { IsMoving = false; return; }
            _dest = aim; _dest.y = GroundY(aim.x, aim.z);
            IsMoving = true;
            _npcTarget = null; _targetMob = 0; _lbHoldMove = false;
        }

        /// <summary>Programmatic click-to-move target (used by the auto-walk self-test).</summary>
        public void SetDestination(Vector3 worldPoint)
        {
            _dest = worldPoint;
            _dest.y = GroundY(_dest.x, _dest.z);
            IsMoving = true;
        }

        public void TeleportToSession()
        {
            var p = new Vector3(Session.Me.X, 0f, Session.Me.Z);
            p.y = GroundY(p.x, p.z);
            transform.position = p;
            _dest = p;
            IsMoving = false;
            _lastSentX = Session.Me.X; _lastSentZ = Session.Me.Z;
        }

        public static bool TestNpc;   // -ndTestNpc: pick the nearest NPC + run the click path (headless verify)
        private float _enableTime = -1f;
        private bool _testNpcDone;
        private void TestPickNearestNpc()
        {
            NpcId best = null; float bd = float.MaxValue; var p = transform.position;
            foreach (var n in Object.FindObjectsByType<NpcId>(FindObjectsSortMode.None))
            {
                float d = (n.transform.position - p).sqrMagnitude;
                if (d < bd) { bd = d; best = n; }
            }
            if (best != null) { _npcTarget = best; _viewNpc = best; Debug.Log($"[NPCTEST] picked nearest npc {best.UniqueId} '{best.Name}' dist {Mathf.Sqrt(bd):0.0}"); }
            else Debug.Log("[NPCTEST] no NpcId component found in scene — NPCs are not clickable");
        }

        private void Update()
        {
            if (Session == null || !Session.InWorld) return;
            if (_enableTime < 0f) _enableTime = Time.time;
            if (TestNpc && !_testNpcDone && _npcTarget == null && Time.time - _enableTime > 4f)
            { _testNpcDone = true; TestPickNearestNpc(); }

            HandleKeyboardMove();
            if (KhinhCongArmed) HandleKhinhCong();   // hold ALT → run toward the cursor, steer by mouse
            else
            {
                // ALT just released → stop the khinh công run at once (clear the stale forward heading),
                // so the character never keeps the leaning run pose after "ngừng chạy". WASD keeps its move.
                if (_wasKhinhCong && !_keyMoving)
                { IsMoving = false; _dest = transform.position; _lbHoldMove = false; }
                HandleClick();
            }
            _wasKhinhCong = KhinhCongArmed;
            HandleTargetKeys();
            UpdateCombat();
            UpdateNpcApproach();
            ReconcileServer();
            StepToward();
            StreamMove();
        }

        // ESC drops the selected target (like the client), and a view target whose mob has died or
        // left the world is released automatically (CodexAutoReleaseDeadMobTarget, XProc_MainGame.cpp:1126).
        private void HandleTargetKeys()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { ClearTarget(); return; }
            if (_viewTarget != 0)
            {
                if (Session.Monsters == null ||
                    !Session.Monsters.TryGetValue(_viewTarget, out var vm) || vm == null || !vm.Alive)
                    _viewTarget = 0;
            }
        }

        // WASD / arrow-pad camera-relative movement, always available even if the ground has no
        // collider under the cursor. Holding a key walks continuously in the camera's facing.
        private bool _keyMoving;
        private void HandleKeyboardMove()
        {
            if (Acting) { if (_keyMoving) { _keyMoving = false; IsMoving = false; } return; }   // rooted while swinging/drawing
            float h = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
            float v = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
            if (Mathf.Abs(h) < 0.01f && Mathf.Abs(v) < 0.01f)
            {
                if (_keyMoving) { _keyMoving = false; IsMoving = false; SendMove(true); }
                return;
            }
            _npcTarget = null;   // manual WASD cancels an NPC approach
            var camT = Cam != null ? Cam.transform : transform;
            Vector3 fwd = camT.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 right = camT.right; right.y = 0f; right.Normalize();
            Vector3 dir = (fwd * v + right * h).normalized;
            _keyMoving = true;
            // aim a destination a short way ahead so StepToward drives it this frame
            var pos = transform.position;
            _dest = pos + dir * 2f;
            _dest.y = GroundY(_dest.x, _dest.z);
            IsMoving = true;
        }

        // HOLD-to-move: the client keeps the character running toward the cursor while the left button
        // is held, and steers when you drag — not one fixed point per click. Without this, click-move
        // fires only on mouse-DOWN, so a run stops after one short hop and dragging the mouse never
        // re-aims (which is why "khinh công chỉ chạy 1 đoạn ngắn rồi dừng, rê chuột ko đổi hướng").
        private bool _lbHoldMove;

        // Forgiving click-target: pick the ALIVE mob whose ON-SCREEN position is nearest the cursor (within a
        // radius), so a click near a mob targets it even when the narrow physics capsule is missed/offset.
        private EntityId PickMobNearCursor(Vector3 mousePos)
        {
            if (Cam == null) return null;
            EntityId best = null; float bestD = 55f * 55f;   // within ~55 px of the cursor
            foreach (var eid in FindObjectsByType<EntityId>(FindObjectsSortMode.None))
            {
                if (eid.gameObject.layer != EntityId.MobLayer) continue;
                if (Session != null && Session.Monsters != null && Session.Monsters.TryGetValue(eid.Id, out var m)
                    && (m == null || !m.Alive)) continue;
                Vector3 sp = Cam.WorldToScreenPoint(eid.transform.position + Vector3.up * 1.0f);
                if (sp.z <= 0f) continue;   // behind the camera
                float dx = sp.x - mousePos.x, dy = sp.y - mousePos.y;
                float d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = eid; }
            }
            return best;
        }

        private void HandleClick()
        {
            if (Cam == null) return;
            // button released → stop following the cursor (StepToward still finishes the last _dest).
            if (!Input.GetMouseButton(0)) { _lbHoldMove = false; return; }
            bool down = Input.GetMouseButtonDown(0);

            // A click/drag over the UI must NOT move/target (the client never paths from a HUD click):
            // skip when the pointer is over any uGUI graphic OR a reserved HUD rect OR the NPC dialog.
            if (down)
            {
                bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
                bool reserved = Hud.InReservedArea(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                Debug.Log($"[CLICKDIAG] mouse-down overUI={overUI} reserved={reserved} pos={Input.mousePosition}");
            }
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
            if (Hud.InReservedArea(Input.mousePosition.x, Screen.height - Input.mousePosition.y)) return;
            if (NpcDialog.Contains(Input.mousePosition.x, Screen.height - Input.mousePosition.y)) return;
            var ray = Cam.ScreenPointToRay(Input.mousePosition);

            if (down)
            {
                _lbHoldMove = false;   // a fresh press: hold-follow only re-arms on a ground click below
                // clicking a monster targets it (auto-attack); the ground move-ray ignores the mob layer.
                EntityId mob = null;
                if (Physics.Raycast(ray, out var mhit, 5000f, 1 << EntityId.MobLayer))
                    mob = mhit.collider.GetComponentInParent<EntityId>();
                if (mob == null) mob = PickMobNearCursor(Input.mousePosition);   // forgiving screen-space pick
                if (mob != null)
                {
                    _targetMob = mob.Id; _targetMobT = mob.transform; _viewTarget = mob.Id; _npcTarget = null; _viewNpc = null; _atkTimer = 0f;
                    AnimD()?.EnsureBattle();   // engaging a mob DRAWS the weapon (auto-battle), like the client
                    Debug.Log($"[CLICKDIAG] targeted mob {mob.Id}");
                    return;
                }
                // clicking an NPC walks up to it and opens its conversation (StartNPCScript,
                // XProc_MainGame.cpp:9032). NPCs are on their own layer so this never steals a move click.
                if (Physics.Raycast(ray, out var nhit, 5000f, 1 << NpcId.NpcLayer))
                {
                    var nid = nhit.collider.GetComponentInParent<NpcId>();
                    // select it (name shows in the top target frame) AND walk up to open its conversation.
                    if (nid != null) { _npcTarget = nid; _viewNpc = nid; _targetMob = 0; _viewTarget = 0; return; }
                }
            }
            else if (!_lbHoldMove) return;   // held, but the initial press wasn't a ground move — ignore

            if (Acting) return;   // can retarget a mob mid-swing, but cannot walk off while acting
            // ground move — on the initial press AND every held frame (follow + steer to the cursor)
            if (Physics.Raycast(ray, out var hit, 5000f, ~((1 << EntityId.MobLayer) | (1 << NpcId.NpcLayer))))
            {
                // Reject a blocked/enclosed cell (XProc_MainGame.cpp:16288): keep the previous target,
                // don't path into walls/water. While dragging, just hold the last valid point.
                if (HonorWalkGrid && Terrain != null &&
                    (!Terrain.IsWalkable(hit.point.x, hit.point.z) ||
                      Terrain.BlockedNeighborCount(hit.point.x, hit.point.z) > 7))
                    return;
                _dest = hit.point;
                _dest.y = GroundY(_dest.x, _dest.z);
                IsMoving = true;
                _npcTarget = null;       // a ground move cancels an NPC approach
                _lbHoldMove = true;      // keep following the cursor until the button is released
                if (down) _targetMob = 0;   // the initial press also cancels the ATTACK target (view target stays)
            }
        }

        /// <summary>Engage a mob when the player ACTIVATES a quickslot skill (number key or a click on the
        /// cell — they share this path): keep the current attack target if it is still a live mob, else
        /// adopt the SELECTED (view) mob, else auto-pick the nearest live mob. The combat loop then runs to
        /// it and strikes with the armed skill — "bấm số / click skill → chạy lại đối tượng → dùng chiêu".
        /// Draws the weapon (battle stance) on the way, like the client.</summary>
        public void EngageWithSkill()
        {
            if (Session == null || Session.Monsters == null) return;
            ushort pick = 0;
            // keep the current attack target if still a live mob
            if (_targetMob != 0 && Session.Monsters.TryGetValue(_targetMob, out var cur) && cur != null && cur.Alive)
                pick = _targetMob;
            // else the SELECTED (view) target if it is a live mob
            if (pick == 0 && _viewTarget != 0 && Session.Monsters.TryGetValue(_viewTarget, out var vt) && vt != null && vt.Alive)
                pick = _viewTarget;
            if (pick == 0)   // else the nearest live mob
            {
                var pos = transform.position; float best = float.MaxValue;
                foreach (var kv in Session.Monsters)
                {
                    if (kv.Value == null || !kv.Value.Alive) continue;
                    float dx = kv.Value.X - pos.x, dz = kv.Value.Z - pos.z, d2 = dx * dx + dz * dz;
                    if (d2 < best) { best = d2; pick = kv.Key; }
                }
            }
            if (pick != 0)
            {
                _targetMob = pick; _viewTarget = pick; _targetMobT = null; _npcTarget = null; _viewNpc = null;
                _atkTimer = 0f; _castOnce = true;   // ONE strike then stop (nhất kích)
                AnimD()?.EnsureBattle();
                Debug.Log($"[SKILLDIAG] engage mob {pick} via skill activation (one-shot)");
            }
            else Debug.Log("[SKILLDIAG] engage: no live mob to attack");
        }

        // Approach the targeted monster and, once in range, face it and send normal attacks on a
        // steady cadence. The server is authoritative (damage / death stream back).
        public static bool AutoAttack;   // debug (-ndAutoAttack): lock onto the nearest live mob
        private void UpdateCombat()
        {
            if (_targetMob == 0 && AutoAttack)
            {
                var pos0 = transform.position; float best = 3000f * 3000f; ushort pick = 0;
                foreach (var kv in Session.Monsters)
                {
                    if (!kv.Value.Alive) continue;
                    float dx = kv.Value.X - pos0.x, dz = kv.Value.Z - pos0.z, d2 = dx * dx + dz * dz;
                    if (d2 < best) { best = d2; pick = kv.Key; }
                }
                if (pick != 0) { _targetMob = pick; _targetMobT = null; _atkTimer = 0f; Debug.Log($"[COMBAT] auto-target mob {pick}"); }
            }
            if (_targetMob == 0) return;
            if (Session.Monsters == null || !Session.Monsters.TryGetValue(_targetMob, out var mob) || mob == null || !mob.Alive)
            { _targetMob = 0; _targetMobT = null; IsMoving = false; return; }

            // resolve the target mob's RENDERED transform once (click captures it; auto-target / a re-look
            // resolves it here) so combat aims at where the mob is DRAWN, not its stale packet mob.X/Z.
            if (_targetMobT == null)
                foreach (var e in Object.FindObjectsByType<EntityId>(FindObjectsSortMode.None))
                    if (e.Id == _targetMob) { _targetMobT = e.transform; break; }

            var pos = transform.position;
            // aim at the mob's VISIBLE position (its interpolated transform), falling back to the packet
            // coords only if the transform is somehow gone — this is the fix for "chạy/đánh ngược hướng".
            Vector3 mpos = _targetMobT != null ? _targetMobT.position : new Vector3(mob.X, 0f, mob.Z);
            var to = new Vector3(mpos.x - pos.x, 0f, mpos.z - pos.z);
            float dist = to.magnitude;
            // ALWAYS face the engaged mob — whether approaching OR striking — so a swing never lands
            // facing away (mesh front is -forward, so aim -forward along the dir to it). Running this
            // every frame (not only in-range) is why "đánh vẫn quay khác hướng" is gone.
            var faceRot = to.sqrMagnitude > 0.0004f
                ? Quaternion.LookRotation(-to.normalized, Vector3.up) : transform.rotation;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, faceRot, turnSpeed * Time.deltaTime);
            if (dist > attackRange)
            {
                _dest = new Vector3(mpos.x, 0f, mpos.z);   // walk toward where the mob is DRAWN, not mob.X/Z
                _dest.y = GroundY(_dest.x, _dest.z);
                IsMoving = true;   // walk toward the mob
            }
            else
            {
                IsMoving = false;  // in range: stop and fight
                _atkTimer -= Time.deltaTime;
                if (_atkTimer <= 0f)
                {
                    // Reaching the mob DRAWS the weapon: auto-enter battle stance if still in peace,
                    // mirroring the client auto-drawing into battle mode when you engage / arm a skill
                    // from idle (SetActiveSkill → SetBattleMode(_XBATTLE_BATTLE), XLocalUser.cpp:14016).
                    // Keep the target (never wipe it) and strike on the NEXT tick so the draw plays first.
                    var ad = AnimD();
                    if (ad != null && !ad.Battle)
                    {
                        ad.EnsureBattle();
                        _atkTimer = attackInterval;   // let the draw settle; swing on the following tick
                        return;
                    }
                    // SNAP exactly onto the mob at the instant of the swing, so an attack always lands
                    // facing the target (never "đánh mà không hướng về đối phương") even if it was
                    // still mid-turn or the mob just moved.
                    transform.rotation = faceRot;
                    // If a martial technique is slotted (via the Skill panel, K), fire it instead of
                    // the plain attack — server-authoritative like the normal attack. Falls back to
                    // the plain attack if the slot turned out empty / not a combat technique.
                    // fire the armed skill ONLY on a deliberate one-shot activation (nhất kích); a plain
                    // click-a-mob engagement uses normal attacks, so an armed skill never auto-spams.
                    bool firedSkill = _castOnce && Session.SelectedSkillSlot >= 0 &&
                                      Session.SendSkill(Session.SelectedSkillSlot, _targetMob);
                    Debug.Log($"[SKILLDIAG] swing target={_targetMob} sel={Session.SelectedSkillSlot} firedSkill={firedSkill} battle={(AnimD()?.Battle)} castOnce={_castOnce}");
                    if (!firedSkill) Session.SendAttack(_targetMob, _atkSeq);
                    if (AutoAttack) Debug.Log($"[COMBAT] fire mob {_targetMob} seq {_atkSeq} skill={firedSkill} dist {dist:0.0} vital {mob.VitalRate} serverResults={Session.CombatResults}");
                    _atkSeq = _atkSeq % 3 + 1;
                    _atkTimer = attackInterval;
                    // nhất kích: one strike per activation — stop after the single hit, don't auto-loop.
                    if (_castOnce) { _castOnce = false; _targetMob = 0; _targetMobT = null; IsMoving = false; }
                }
            }
        }

        private void StepToward()
        {
            if (Acting) { IsMoving = false; return; }   // no sliding while swinging / drawing / sheathing
            if (!IsMoving) return;
            var pos = transform.position;
            var to = new Vector3(_dest.x - pos.x, 0f, _dest.z - pos.z);
            float dist = to.magnitude;
            if (dist <= arriveEpsilon)
            {
                IsMoving = false;
                SendMove(true);
                return;
            }
            var dir = to / dist;
            // The character mesh faces LOCAL -Z, so its front is -transform.forward. To face the
            // destination (not moonwalk), aim transform.forward along -dir; then the mesh front
            // (-forward) points at the target.
            var look = Quaternion.LookRotation(-dir, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnSpeed * Time.deltaTime);
            // move at a speed scaled by how much the MESH FRONT (-forward) already faces the target:
            // a sharp turn slows to a near-pivot instead of sliding sideways, then accelerates out.
            float facing = Mathf.Clamp01(Vector3.Dot(-transform.forward, dir));
            // keep a higher floor so a click that needs a turn starts moving promptly (less "stall
            // then lurch"); still eases sideways slide during the pivot.
            float step = Mathf.Min(Speed * Mathf.Lerp(0.55f, 1f, facing) * Time.deltaTime, dist);
            var next = pos + dir * step;
            // Client walk-grid block (XUser.cpp:7659): never step into a blocked cell — stay put and
            // stop, exactly as the real client returns FALSE from its move step. Anti-trap guard: only
            // block when we currently stand on a walkable cell, so a player who spawns just off a
            // walkable cell can still walk out (the Unity spawn need not land exactly on the grid).
            if (HonorWalkGrid && Terrain != null &&
                !Terrain.IsWalkable(next.x, next.z) && Terrain.IsWalkable(pos.x, pos.z))
            {
                IsMoving = false;   // position unchanged → nothing new to stream to the server
                return;
            }
            next.y = GroundYUnderPlayer(next.x, next.z);
            transform.position = next;
        }

        private void StreamMove()
        {
            if (!IsMoving) return;
            _sendTimer += Time.deltaTime;
            if (_sendTimer >= sendInterval) { _sendTimer = 0f; SendMove(false); }
        }

        private void SendMove(bool final)
        {
            var p = transform.position;
            Session.SendZoneMove(p.x, p.z, _dest.x, _dest.z);
            _lastSentX = p.x; _lastSentZ = p.z;
        }

        // If the server moved us independently of our own last send, snap (warp, knockback, correction).
        private void ReconcileServer()
        {
            float dx = Session.Me.X - _lastSentX, dz = Session.Me.Z - _lastSentZ;
            if (dx * dx + dz * dz > snapThreshold * snapThreshold)
            {
                var p = new Vector3(Session.Me.X, 0f, Session.Me.Z);
                p.y = GroundY(p.x, p.z);
                transform.position = p;
                _dest = p;
                IsMoving = false;
                _lastSentX = Session.Me.X; _lastSentZ = Session.Me.Z;
            }
        }

        private float GroundY(float x, float z)
        {
            // The character walks on the TERRAIN surface: use the smooth height grid (bilinear) so
            // it glides over slopes without hopping onto decoration colliders (poles, banners,
            // lanterns) that a downward ray would otherwise catch and jerk it upward. Fall back to a
            // ground ray only when the terrain grid is unavailable.
            if (Terrain != null) return TerrainFactory.HeightAt(Terrain, x, z);
            var ray = new Ray(new Vector3(x, 500f, z), Vector3.down);
            if (Physics.Raycast(ray, out var hit, 2000f)) return hit.point.y;
            return 0f;
        }

        /// <summary>Per-frame walking height that also lets the character stand on a BRIDGE / raised walkway
        /// (mesh objects above the terrain), which the plain terrain grid ignores (so the character walked
        /// UNDER bridges, at the ravine floor). We keep the smooth grid as the base and only elevate onto a
        /// mesh when a downward ray FROM JUST ABOVE THE FEET hits a non-terrain collider that sits above the
        /// terrain and within one step of the current height — so pole/banner TOPS (above the head) and the
        /// terrain-vs-collider jitter never catch it, and it never teleports up onto a tall object.</summary>
        private float GroundYUnderPlayer(float x, float z)
        {
            float terrainY = (Terrain != null) ? TerrainFactory.HeightAt(Terrain, x, z)
                : (Physics.Raycast(new Ray(new Vector3(x, 500f, z), Vector3.down), out var g, 2000f) ? g.point.y : 0f);
            float curY = transform.position.y;
            int mask = ~((1 << EntityId.MobLayer) | (1 << NpcId.NpcLayer));
            var ray = new Ray(new Vector3(x, curY + 1.6f, z), Vector3.down);
            if (Physics.Raycast(ray, out var hit, 4.0f, mask, QueryTriggerInteraction.Ignore))
            {
                string n = hit.collider != null ? hit.collider.gameObject.name : null;
                bool isTerrain = n != null && n.StartsWith("terrain_", System.StringComparison.Ordinal);
                if (!isTerrain && hit.point.y > terrainY + 0.5f && hit.point.y <= curY + 1.6f)
                    return hit.point.y;   // standing on a bridge / raised walkway
            }
            return terrainY;
        }
    }
}
