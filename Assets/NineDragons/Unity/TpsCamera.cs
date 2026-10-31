using UnityEngine;
using UnityEngine.EventSystems;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// Third-person camera reproducing the Windows client's _X3PCamera exactly
    /// (client\XControl\X3PCamera.cpp + XProc_MainGame.cpp input):
    ///
    ///  - ROTATE by pushing the mouse to a screen edge (6 px band, XProc_MainGame
    ///    _XDEF_ROTATECAMERAEDGEAREA). Speed accelerates +0.05/frame up to 2.0 deg while an
    ///    edge is held, decays -0.2/frame when released (_XDEF_ROTATESPEEDINCREASE/DECREASE).
    ///    Left/right edge = yaw ∓, top/bottom edge = pitch ∓ (AddYaw/AddPitch).
    ///  - ZOOM with the wheel: distance -= zDelta*0.002 (0.24 per notch); LCTRL ×5
    ///    (_XDEF_RAPID_RATE). Distance clamped [0.8, 150].
    ///  - Camera placement = FindCameraPosition2D:
    ///      cam = target + ( sin(yaw)cos(pitch), sin(pitch), cos(yaw)cos(pitch) ) * distance
    ///    with target at the character + camera eye height (1.18, _XDEF_CHARACTERAVERAGEHEIGHT_CAMERA).
    ///  - Pitch clamped [-85, 80] as the game sets (SetPitchLimits, XProc_MainGame:1953).
    ///
    /// Arrow-key rotate (2.5 deg/press, _XDEF_KEY_CAMERAROTATESPEED) is also wired for parity.
    /// RMB-drag rotate is added as a modern convenience; it does not exist in the client but
    /// does not interfere with edge-scroll.
    /// </summary>
    public sealed class TpsCamera : MonoBehaviour
    {
        public Transform target;
        public XtvTerrain terrain;

        public const float EyeHeight = 1.18f;
        public const float EdgeArea = 6f;
        public const float RotAccel = 0.05f;
        public const float RotDecel = 0.2f;
        public const float RotMax = 2.0f;
        public const float KeyRotate = 2.5f;
        public const float WheelPerDelta = 0.002f;   // * zDelta (120/notch)
        public const float RapidRate = 5.0f;         // LCTRL
        public const float MinDistance = 0.8f;
        public const float MaxDistance = 150f;
        public const float MinPitch = -85f;
        public const float MaxPitch = 80f;

        public float yaw = 0f;
        public float pitch = 16f;
        public float distance = 6f;      // client spawn distance is 5 (SetDistance 5.0)

        public bool enableRmbDrag = true;
        public float rmbSensitivity = 3.0f;

        // Smoothing: the raw yaw/pitch/distance are applied instantly (responsive), but the camera
        // POSITION eases toward its computed spot so target jitter / collision pops don't transfer
        // as a shake. Small time = still snappy, just not stuttery.
        public float posSmoothTime = 0.06f;

        private float _rotSpeed;
        private bool _armed;             // edge-scroll only after the mouse actually moves in-window
        private Vector3 _lastMouse;
        private bool _haveLastMouse;
        private Vector3 _posVel;         // SmoothDamp velocity for the camera position
        private float _curDist;          // collision-smoothed distance (eases out, snaps in)
        private bool _curDistInit;
        private bool _placed;            // false until the first frame is snapped (no swoop-in)
        private Vector3 _lastTargetPos;
        private const float TeleportSnapDist = 20f;  // target jump this big = map-enter/teleport → snap, don't glide

        private void LateUpdate()
        {
            if (target == null) return;
            UpdateArmed();
            HandleZoom();
            HandleEdgeRotate();
            HandleKeyRotate();
            HandleRmbDrag();
            Place();
        }

        // The Windows client only edge-scrolls while the game window is focused and the cursor
        // is being moved. In a headless/idle window Input.mousePosition sits at (0,0) — the
        // bottom-left CORNER — which would otherwise spin the camera to max pitch ("fly to sky").
        // Arm edge-scroll only once the mouse has genuinely moved inside the window with focus.
        private void UpdateArmed()
        {
            var m = Input.mousePosition;
            if (!_haveLastMouse) { _lastMouse = m; _haveLastMouse = true; return; }
            bool moved = (m - _lastMouse).sqrMagnitude > 0.01f;
            _lastMouse = m;
            bool inWindow = m.x > 0 && m.x < Screen.width && m.y > 0 && m.y < Screen.height;
            if (moved && inWindow && Application.isFocused) _armed = true;
            if (!Application.isFocused) _armed = false;
        }

        private void HandleZoom()
        {
            // Only zoom when the pointer is over the WORLD, not a UI window/panel. Scrolling to read a
            // panel (Character/Hành trang…) or over the HUD must not zoom the camera — the wheel belongs
            // to the panel there (chủ: "cuộn xem thông tin mà bị zoom hoài"; chỉ zoom khi trỏ ra môi trường).
            var es = EventSystem.current;
            if (es != null && es.IsPointerOverGameObject()) return;
            float notches = Input.mouseScrollDelta.y;      // +1 per notch up (== +120 zDelta)
            if (Mathf.Abs(notches) > 0.0001f)
            {
                float move = notches * 120f * WheelPerDelta;
                if (Input.GetKey(KeyCode.LeftControl)) move *= RapidRate;
                distance = Mathf.Clamp(distance - move, MinDistance, MaxDistance);
            }
        }

        private void HandleEdgeRotate()
        {
            var m = Input.mousePosition;
            int w = Screen.width, h = Screen.height;
            int dyaw = 0, dpitch = 0;
            bool onEdge = false;
            // only when the cursor is actually inside the window
            if (m.x >= 0 && m.x <= w && m.y >= 0 && m.y <= h)
            {
                if (m.x > w - EdgeArea) { dyaw = 1; onEdge = true; }
                else if (m.x < EdgeArea) { dyaw = -1; onEdge = true; }
                // Unity y is bottom-up; client z is top-down. top edge = client z<EDGE = pitch-1.
                if (m.y > h - EdgeArea) { dpitch = -1; onEdge = true; }   // top of screen
                else if (m.y < EdgeArea) { dpitch = 1; onEdge = true; }  // bottom of screen
            }

            if (onEdge && _armed)
            {
                _rotSpeed = Mathf.Min(_rotSpeed + RotAccel, RotMax);
                if (dyaw != 0) AddYaw(_rotSpeed * dyaw);
                if (dpitch != 0) AddPitch(_rotSpeed * dpitch);
            }
            else
            {
                _rotSpeed = Mathf.Max(_rotSpeed - RotDecel, 0f);
            }
        }

        private void HandleKeyRotate()
        {
            if (Input.GetKey(KeyCode.LeftArrow)) AddYaw(-KeyRotate);
            if (Input.GetKey(KeyCode.RightArrow)) AddYaw(KeyRotate);
            if (Input.GetKey(KeyCode.UpArrow)) AddPitch(-KeyRotate);
            if (Input.GetKey(KeyCode.DownArrow)) AddPitch(KeyRotate);
        }

        private void HandleRmbDrag()
        {
            if (!enableRmbDrag || !Input.GetMouseButton(1)) return;
            AddYaw(Input.GetAxis("Mouse X") * rmbSensitivity);
            AddPitch(-Input.GetAxis("Mouse Y") * rmbSensitivity);
        }

        public void AddYaw(float a)
        {
            yaw += a;
            if (yaw > 360f) yaw %= 360f;
            else if (yaw < 0f) yaw += 360f;
        }

        public void AddPitch(float a)
        {
            pitch = Mathf.Clamp(pitch + a, MinPitch, MaxPitch);
        }

        // Collision ray ignores the mobs (they wander across the sightline and would otherwise
        // yank the camera in/out) and the player — only real geometry (terrain/buildings/props)
        // should pull the camera in.
        private static readonly int CollisionMask =
            ~((1 << EntityId.MobLayer) | (1 << NpcId.NpcLayer) | (1 << 2)); // 2 = Ignore Raycast; mobs/NPCs don't block the camera

        private void Place()
        {
            var t = target.position + Vector3.up * EyeHeight;
            float ry = yaw * Mathf.Deg2Rad, rp = pitch * Mathf.Deg2Rad;
            var ndir = new Vector3(
                Mathf.Sin(ry) * Mathf.Cos(rp),
                Mathf.Sin(rp),
                Mathf.Cos(ry) * Mathf.Cos(rp));            // unit direction head→camera

            // desired distance, shortened when static geometry blocks the line to the head
            float want = distance;
            if (Physics.Raycast(t, ndir, out var hit, distance, CollisionMask, QueryTriggerInteraction.Ignore))
                want = Mathf.Max(MinDistance, hit.distance - 0.3f);

            // ease the distance: snap IN instantly (avoid clipping through a wall), ease OUT slowly
            if (!_curDistInit) { _curDist = want; _curDistInit = true; }
            _curDist = want < _curDist ? want : Mathf.Lerp(_curDist, want, 1f - Mathf.Exp(-6f * Time.deltaTime));

            var cam = t + ndir * _curDist;
            if (terrain != null)
            {
                float g = TerrainFactory.HeightAt(terrain, cam.x, cam.z) + 0.5f;
                if (cam.y < g) cam.y = g;
            }

            // On the FIRST frame, and whenever the target jumps far (map-enter / teleport), SNAP the
            // camera straight to its spot — otherwise SmoothDamp would visibly swoop in from the old
            // (origin / previous-map) position, which reads as the "screen jerks through the terrain
            // then snaps back" glitch on map entry. Normal movement still eases for smoothness.
            bool snap = !_placed || (target.position - _lastTargetPos).sqrMagnitude > TeleportSnapDist * TeleportSnapDist;
            _lastTargetPos = target.position;
            if (snap) { transform.position = cam; _posVel = Vector3.zero; _placed = true; }
            else transform.position = Vector3.SmoothDamp(transform.position, cam, ref _posVel, posSmoothTime);

            // guard the degenerate case (camera coincident with the head) so the view can't flip
            var look = t - transform.position;
            if (look.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(look, Vector3.up);
        }
    }
}
