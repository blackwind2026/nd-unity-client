using UnityEngine;
using UnityEngine.UI;
using Nd.Session;

namespace NineDragons.Client
{
    /// <summary>
    /// In-game HUD built with legacy uGUI (no prefabs / TMP needed, so it survives a headless
    /// build): a player frame (name, level, HP/Force/Concentration/EXP bars) top-left, a hotbar
    /// along the bottom, a minimap top-right, a chat box bottom-left, and toggleable panels —
    /// C = character stats, F11 = collection/pet stub — mirroring the client's layout.
    /// All live values read from the M5 <see cref="GameSession"/>.
    /// </summary>
    public sealed class GameHud : MonoBehaviour
    {
        public GameSession Session;
        public AssetService Assets;   // for the real HUD art from TR_MAININTERFACE.XP
        public string MapName = "";
        public int Level;
        public Texture2D MinimapTex;

        private Font _font;
        private Image _hpFill, _forceFill, _concFill, _expFill;
        private Text _nameText, _hpText, _forceText, _concText, _levelText, _playText;
        private F11Window _f11Window;     // the F11 Awaken window (gallery + guardian), mirrors g_pF11ModernCollection_Window
        private CharWindow _charWindow;   // the C-key 7-tab character window (own component, mirrors _XWindow_MainFrame)
        private InventoryWindow _invWindow;   // the I-key bag window (mirrors _XWindow_Inventory)
        private WarehouseWindow _whWindow;    // the B-key storage window (mirrors _XWindow_Warehouse)
        private NpcShopWindow _shopWindow;    // NPC shop (mirrors _XWindow_NPCTrade)
        private TradeWindow _tradeWindow;     // player trade (mirrors _XWindow_PCTrade)
        private Transform _minimapDot;
        private RectTransform _minimapRect;
        private RectTransform _minimapBlips;   // per-frame entity blips (players/monsters/npcs)
        private RectTransform _minimapMap;     // the map RawImage (scaled by zoom)
        private Text _minimapCoord;            // "X , Z" readout under the map
        private float _minimapZoom = 2f;       // radar zoom (1..4), stepped by the +/- buttons (2=radar quanh người)
        private float _radarCx, _radarCy, _radarWin = 1f;   // tâm+cỡ cửa sổ UV radar (clamp trong [0,1]) — cho blip
        /// <summary>NPC world positions to plot as yellow blips (no NPC stream in the session yet;
        /// the demo seeds a few, and a live NPC source can feed this list).</summary>
        public readonly System.Collections.Generic.List<Vector2> MinimapNpcs = new System.Collections.Generic.List<Vector2>();

        // chat (mirrors _XWindow_ChattingDefault: colored log lines + channel bar + input)
        private Text[] _chatLines;
        private readonly System.Collections.Generic.List<(int col, string msg)> _chatBuf
            = new System.Collections.Generic.List<(int, string)>();
        private readonly object _chatLock = new object();   // ChatReceived may fire off the pump thread
        private bool _chatHooked;
        private InputField _chatInput;
        private Text _chatChannelLabel;
        // TEMP: chat box (bottom-left) hidden for now — anh sẽ quay lại. It is still fully BUILT (so no
        // null refs anywhere), just deactivated, and Enter no longer focuses it. Flip to true to restore.
        private const bool ShowChat = false;
        private int _chatChannel = 7;   // current send channel (index into ChatChannelIds) — mặc định General Chat

        // g_ChattingStringColor (XWindow_ChattingDefault.cpp:71-78), ARGB → Unity Color:
        // NORMAL / MYMESSAGE / WHISPER / WHOLE / USERCLAN / PARTY / GLOBALNOTICE / ADMINNOTICE
        private static readonly Color[] ChatColors =
        {
            Argb(0xFFFC832A), Argb(0xFF03FEFF), Argb(0xFF8BC7F6), Argb(0xFFBCEB7F),
            Argb(0xFFF1DF48), Argb(0xFFF3A2F0), Argb(0xFFE2C9AA), Argb(0xFFFFFFFF),
        };
        // Kênh chat = bộ SELECTOR native (khớp GAME THẬT), tên lấy từ NDTextDB EODString_XSR — chủ 09/09
        // "rõ ràng ko hardcode": Emote 1264/Lion's Roar 2955/Clan 1265/Hero Band 391/Party 392/Whisper 47/
        // Map-Zone 3216/General Chat 3421. ChatChannelNames = FALLBACK (English khớp DB) khi XSD trống.
        private static readonly int[] ChatChannelIds =
            { 1264, 2955, 1265, 391, 392, 47, 3216, 3421 };
        private static readonly string[] ChatChannelNames =
            { "Emote", "Lion's Roar", "Clan", "Hero Band", "Party", "Whisper", "Map/Zone", "General Chat" };
        // kênh GỬI nút cuộn qua (index): General Chat/Lion's Roar/Clan/Hero Band/Party/Whisper
        private static readonly int[] ChatChannelCycle = { 7, 1, 2, 3, 4, 5 };
        private static Color Argb(uint v) => new Color(
            ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, ((v >> 24) & 0xFF) / 255f);
        /// <summary>Chữ UI từ NDTextDB (section EODString_XSR) theo ID; rỗng→fallback. Chủ: "dùng thẳng .xsd".</summary>
        private string IT(int id, string fb) { var s = Assets != null ? Assets.GetInterfaceText(id) : null; return string.IsNullOrWhiteSpace(s) ? fb : s; }
        private string ChannelName(int i) => IT(ChatChannelIds[i], ChatChannelNames[i]);

        // map extent for the minimap dot (set by the client)
        public float MapWorldSize = 512f;

        /// <summary>Swap the minimap to a new map (vé di chuyển teleport): update the name/extent/art and
        /// rebuild the minimap so it shows the new zone. Safe before Start (fields just get read then).</summary>
        public void SetMap(string mapName, float worldSize, Texture2D minimapTex)
        {
            MapName = mapName;
            MapWorldSize = worldSize;
            MinimapTex = minimapTex;
            if (_minimapRect != null) RebuildMinimap();
        }

        private void Start()
        {
            _font = LoadUiFont();
            BuildCanvas();
            // world-space combat feedback (mob HP bars + rising damage numbers) — self-contained
            var combat = gameObject.AddComponent<CombatOverlay>();
            combat.Session = Session;
            // skill window (võ công) — self-contained, opens on K
            var skills = gameObject.AddComponent<SkillPanel>();
            skills.Session = Session; skills.Assets = Assets;
            // top target-status frame (_XWindow_MonsterStatus) — self-contained, shows the clicked mob
            var target = gameObject.AddComponent<TargetFrame>();
            target.Session = Session; target.Assets = Assets;
            // centre-screen system notices (e.g. "One cannot use Kung Fu in Peace Mode")
            gameObject.AddComponent<SystemNotice>();
            // NPC conversation window (opened when the player walks up to a clicked NPC)
            gameObject.AddComponent<NpcDialog>();
            // headless verification: open panels via -ndShowPanels 1
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == "-ndShowPanels" && a[i + 1] == "1")
                { _charWindow.ForceOpenForVerify(); }
            // -ndShowF11 <tab> opens the F11 Awaken window at that tab (0-7; the VALUE is the tab,
            // e.g. "-ndShowF11 7" → Hộ vệ). Non-numeric value → default tab 5 (Ngoại Trang). Also
            // force a usable resolution + centre the window so it is visible at any resolution.
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == "-ndShowF11" && _f11Window != null)
                {
                    int tab = int.TryParse(a[i + 1], out var tv) ? tv : 5;
                    for (int j = 0; j < a.Length - 1; j++) if (a[j] == "-ndF11Tab") int.TryParse(a[j + 1], out tab);
                    tab = Mathf.Clamp(tab, 0, 7);
                    if (Screen.width < 1000 || Screen.height < 700) Screen.SetResolution(1280, 800, false);
                    _f11Window.ForceOpenForVerify(tab);
                    break;
                }
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == "-ndShowInv" && a[i + 1] == "1") _invWindow.Toggle();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == "-ndInvPage" && int.TryParse(a[i + 1], out var pg)) _invWindow.SetPage(pg);   // verify a specific bag
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == "-ndShowWh" && a[i + 1] == "1") _whWindow.Toggle();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == "-ndShowShop" && a[i + 1] == "1") _shopWindow.Toggle();
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == "-ndShowTrade" && a[i + 1] == "1") _tradeWindow.Toggle();
        }

        /// <summary>The shared UI font: the bundled VN-diacritic TTF (Resources/Fonts/nd_nameplate),
        /// with the same fallbacks the world nameplate uses — a VN-capable OS font, then Unity's
        /// LegacyRuntime.ttf. Loaded once and reused by the HUD and every window (they take _font).</summary>
        // the single shared crisp font — same OS Gulim→Malgun→Tahoma→Arial chain as the head nameplate,
        // so the whole HUD (and every window that takes _font) draws with one unified, sharp typeface.
        private static Font LoadUiFont() => WorldNameplate.NameplateFont();

        private void BuildCanvas()
        {
            var canvasGo = new GameObject("HUDCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            // ConstantPixelSize 1:1 — TRUE native pixels for the WHOLE HUD, so the top-left player frame,
            // bottom-right dragon menu + vận-khí cell all render at native size, consistent with the
            // quickslot + Panel C (both 1:1). Was ScaleWithScreenSize (~1.2× at 1920) → HUD read oversized.
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            canvasGo.AddComponent<GraphicRaycaster>();
            var root = canvasGo.transform;

            // UI events (button hover/click sprite-swaps + quickslot drag) need an EventSystem
            if (UnityEngine.EventSystems.EventSystem.current == null)
            {
                var es = new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
                es.transform.SetParent(transform, false);
            }

            BuildPlayerFrame(root);
            BuildMainMenu(root);
            BuildQuickSlot(root);   // the authentic quickslot IS the hotbar (replaces the old placeholder)
            BuildMinimap(root);
            BuildChat(root);
            _charWindow = gameObject.AddComponent<CharWindow>();
            _charWindow.Build(root, _font, Session, Assets, this);
            _invWindow = gameObject.AddComponent<InventoryWindow>();
            _invWindow.Build(root, _font, Session, Assets);
            _whWindow = gameObject.AddComponent<WarehouseWindow>();
            _whWindow.Build(root, _font, Session, Assets);
            _shopWindow = gameObject.AddComponent<NpcShopWindow>();
            _shopWindow.Build(root, _font, Assets);
            _tradeWindow = gameObject.AddComponent<TradeWindow>();
            _tradeWindow.Build(root, _font, Assets);
            _f11Window = gameObject.AddComponent<F11Window>();
            _f11Window.Init(_font, Assets, Session);
        }

        // ---------------- widgets ----------------
        private RectTransform Panel(Transform parent, string name, Color color,
            Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            go.GetComponent<Image>().color = color;
            return rt;
        }

        private Text Label(Transform parent, string text, int size, Color color,
            TextAnchor anchor, Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject("label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = sizeDelta;
            var t = go.AddComponent<Text>();
            t.font = _font; t.text = text; t.fontSize = size; t.color = color;
            t.alignment = anchor; t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        /// <summary>Label positioned by its TOP-LEFT (x,y) relative to the parent's top-left, with
        /// size (w,h). Converts to the center-pivot the base Label uses.</summary>
        private Text LabelTL(Transform parent, string text, int size, Color color, TextAnchor anchor,
            float x, float y, float w, float h)
        {
            return Label(parent, text, size, color, anchor,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(x + w / 2f, y - h / 2f), new Vector2(w, h));
        }

        private Image Bar(Transform parent, Color fillColor, float x, float y, float w, float h)
        {
            var bg = Panel(parent, "bar_bg", new Color(0, 0, 0, 0.72f),
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(x, y), new Vector2(w, h));
            var fillGo = new GameObject("fill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(bg, false);
            var frt = (RectTransform)fillGo.transform;
            frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
            frt.offsetMin = new Vector2(2, 2); frt.offsetMax = new Vector2(-2, -2);
            var img = fillGo.GetComponent<Image>();
            img.color = fillColor; img.type = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Horizontal; img.fillAmount = 1f;
            return img;
        }

        /// <summary>A bare horizontal-fill image (no track background — the frame art provides the
        /// groove), top-left anchored at (x,y) with size (w,h).</summary>
        private Image Fill(Transform parent, Color col, float x, float y, float w, float h)
        {
            var go = new GameObject("fill", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, y); rt.sizeDelta = new Vector2(w, h);
            var img = go.GetComponent<Image>();
            // an Image.Type.Filled needs a SPRITE for fillAmount to take effect — with a null sprite it
            // draws a full quad and ignores fillAmount (that was the "gauge never shrinks" bug). Give it
            // a 1x1 white sprite so the horizontal fill actually clips.
            img.sprite = WhiteSprite();
            img.color = col; img.type = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Horizontal; img.fillOrigin = (int)Image.OriginHorizontal.Left;
            img.fillAmount = 1f; img.raycastTarget = false;
            return img;
        }

        private static Sprite _whiteSprite;
        private static Sprite WhiteSprite()
        {
            if (_whiteSprite == null)
                _whiteSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
            return _whiteSprite;
        }

        // ---- bottom-right main-menu (dragon) cluster: XWindow_MainMenu.cpp ----
        private static Rect R(float x, float y, float w, float h) => new Rect(x, y, w, h);

        private void BuildMainMenu(Transform root)
        {
            if (Assets == null) return;
            const float S = 1.0f, FW = 182f, FH = 93f;   // native 1:1 (was 1.3 — dragon menu read oversized vs quickslot)
            var cont = new GameObject("MainMenu", typeof(RectTransform));
            cont.transform.SetParent(root, false);
            var crt = (RectTransform)cont.transform;
            crt.anchorMin = new Vector2(1, 0); crt.anchorMax = new Vector2(1, 0); crt.pivot = new Vector2(1, 0);
            crt.anchoredPosition = new Vector2(-6, 6); crt.sizeDelta = new Vector2(FW * S, FH * S);

            var bgGo = new GameObject("framebg", typeof(RectTransform), typeof(Image));
            bgGo.transform.SetParent(cont.transform, false);
            var brt = (RectTransform)bgGo.transform;
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            var bimg = bgGo.GetComponent<Image>();
            bimg.sprite = Assets.GetInterfaceSprite("MI_Main_button.tga", new Rect(0, 0, FW, FH));
            bimg.raycastTarget = false;

            var t = cont.transform;
            // each button: NORMAL / hover(READY) / CLICK sub-rects, in that order
            ClusterButton(t, S, 32, 22, 21, 21, R(24, 98, 21, 21), R(48, 99, 21, 21), R(72, 98, 21, 21));      // cashmall
            ClusterButton(t, S, 61, 18, 21, 21, R(24, 123, 21, 21), R(48, 123, 21, 21), R(72, 123, 21, 21));  // cashbox
            ClusterButton(t, S, 77, 43, 32, 32, R(0, 167, 32, 32), R(35, 167, 31, 32), R(71, 167, 32, 32), () => { if (_invWindow != null) _invWindow.Toggle(); });   // inventory (balo) → Hành trang
            ClusterButton(t, S, 100, 71, 21, 21, R(108, 177, 21, 21), R(132, 177, 21, 21), R(156, 177, 21, 21)); // option
            ClusterButton(t, S, 165, 76, 17, 17, R(2, 147, 18, 18), R(22, 147, 18, 18), R(42, 147, 18, 18));  // hedek
            // mail area (XWindow_MainMenu.cpp): messenger TOP + email BOTTOM. Each is a 3-state button
            // whose background (normal/ready/click) is shared from the 3 stacked rects at atlas x=0,y=94/111/128,
            // with the letter icon drawn on top.
            ClusterButton(t, S, 50, 53, 20, 17, R(0, 94, 20, 17), R(0, 111, 20, 17), R(0, 128, 20, 17)); // messenger bg (TOP)
            ClusterButton(t, S, 50, 72, 20, 17, R(0, 94, 20, 17), R(0, 111, 20, 17), R(0, 128, 20, 17)); // email bg (BOTTOM)
            OverlayImage(t, S, 53, 54, 15, 16, R(99, 95, 15, 16));    // messenger icon (TOP) — nudged left 1 / down 1
            OverlayImage(t, S, 52, 75, 17, 12, R(97, 114, 17, 12));   // email icon (BOTTOM) — nudged left 1 / down 1
            BuildRoundButton(t, S, 116, 24, 58, 58);   // big round: normal / hover(img2+img3 blink) / click(img4)
            BuildMeditationButton(t, S);               // vận khí (nội công) — ô đen 30×30 ở (15,56)
        }

        // vận khí: cổng native m_MeditationButton (o 15,56, 30×30). Nền + icon võ công + hào quang, click channel.
        private void BuildMeditationButton(Transform parent, float S)
        {
            var go = new GameObject("meditation", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(15 * S, -56 * S); rt.sizeDelta = new Vector2(30 * S, 30 * S);
            var frame = go.GetComponent<Image>();
            frame.sprite = Assets.GetInterfaceSprite("mi_main_unki_icon.tga", R(1, 1, 30, 30));  // ô đen nền
            frame.raycastTarget = true;                                                          // nhận click

            var iconGo = new GameObject("medicon", typeof(RectTransform), typeof(Image));
            iconGo.transform.SetParent(go.transform, false);
            var irt = (RectTransform)iconGo.transform;
            irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.offsetMin = Vector2.zero; irt.offsetMax = Vector2.zero;
            var icon = iconGo.GetComponent<Image>();
            icon.color = new Color(1, 1, 1, 0); icon.raycastTarget = false; icon.preserveAspect = true;

            var glowGo = new GameObject("medglow", typeof(RectTransform), typeof(Image));
            glowGo.transform.SetParent(go.transform, false);
            var grt = (RectTransform)glowGo.transform;
            grt.anchorMin = Vector2.zero; grt.anchorMax = Vector2.one; grt.offsetMin = Vector2.zero; grt.offsetMax = Vector2.zero;
            var glow = glowGo.GetComponent<Image>();
            glow.sprite = Assets.GetInterfaceSprite("mi_main_unki_icon.tga", R(225, 33, 30, 30));  // hào quang khi ON
            glow.color = new Color(1, 1, 1, 0); glow.raycastTarget = false;

            var med = go.AddComponent<MeditationButton>();
            med.Session = Session; med.Assets = Assets; med.Icon = icon; med.Glow = glow;
        }

        private void BuildRoundButton(Transform parent, float S, float x, float y, float w, float h)
        {
            var go = new GameObject("roundbtn", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x * S, -y * S); rt.sizeDelta = new Vector2(w * S, h * S);
            var main = go.GetComponent<Image>();
            main.sprite = Assets.GetInterfaceSprite("MI_Main_button.tga", R(198, 0, 58, 58));   // img1 normal

            var blGo = new GameObject("blink", typeof(RectTransform), typeof(Image));
            blGo.transform.SetParent(go.transform, false);
            var brt = (RectTransform)blGo.transform;
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            var blImg = blGo.GetComponent<Image>();
            blImg.sprite = Assets.GetInterfaceSprite("MI_Main_button.tga", R(198, 116, 58, 58));  // img3 blink
            blImg.enabled = false; blImg.raycastTarget = false;

            var mrb = go.AddComponent<MenuRoundButton>();
            mrb.Main = main; mrb.Blink = blImg;
            mrb.Normal = main.sprite;
            mrb.Hover = Assets.GetInterfaceSprite("MI_Main_button.tga", R(198, 58, 58, 58));   // img2 hover
            mrb.Click = Assets.GetInterfaceSprite("MI_Main_button.tga", R(198, 174, 58, 58));  // img4 click
        }

        private void ClusterButton(Transform parent, float S, float x, float y, float w, float h, Rect n, Rect r, Rect c, System.Action onClick = null)
        {
            var go = new GameObject("btn", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x * S, -y * S); rt.sizeDelta = new Vector2(w * S, h * S);
            var img = go.GetComponent<Image>();
            img.sprite = Assets.GetInterfaceSprite("MI_Main_button.tga", n);
            var btn = go.GetComponent<Button>();
            btn.transition = Selectable.Transition.SpriteSwap;
            btn.targetGraphic = img;
            btn.spriteState = new SpriteState
            {
                highlightedSprite = Assets.GetInterfaceSprite("MI_Main_button.tga", r),
                pressedSprite = Assets.GetInterfaceSprite("MI_Main_button.tga", c),
                selectedSprite = Assets.GetInterfaceSprite("MI_Main_button.tga", r),
            };
            if (onClick != null) btn.onClick.AddListener(() => onClick());
        }

        private void OverlayImage(Transform parent, float S, float x, float y, float w, float h, Rect rectTD)
        {
            var go = new GameObject("overlay", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x * S, -y * S); rt.sizeDelta = new Vector2(w * S, h * S);
            var img = go.GetComponent<Image>();
            img.sprite = Assets.GetInterfaceSprite("MI_Main_button.tga", rectTD);
            img.raycastTarget = false;
        }

        // ---- bottom-center quickslot bar (custom 2x10 layout, draggable): XWindow_QuickSlot.cpp ----
        private void BuildQuickSlot(Transform root)
        {
            if (Assets == null) return;
            const float S = 1.0f, BW = 460f, BH = 96f;   // native 1:1
            // Own canvas at ConstantPixelSize 1:1 — TRUE pixels, so the 30×30 inner cell/icon renders at
            // 30 REAL px, matching Panel C's equip slots (which are also ConstantPixelSize). The main HUD
            // canvas is ScaleWithScreenSize (~1.2× at 1920) which is what made the quickslot look bigger.
            var qsCanvasGo = new GameObject("QuickSlotCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var qcv = qsCanvasGo.GetComponent<Canvas>();
            qcv.renderMode = RenderMode.ScreenSpaceOverlay;
            qcv.sortingOrder = 5;                 // above the HUD (0), below the windows (20)
            var qsc = qsCanvasGo.GetComponent<CanvasScaler>();
            qsc.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            qsc.scaleFactor = 1f;
            var cont = new GameObject("QuickSlot", typeof(RectTransform), typeof(Image), typeof(DraggableUI));
            cont.transform.SetParent(qsCanvasGo.transform, false);
            var crt = (RectTransform)cont.transform;
            crt.anchorMin = new Vector2(0.5f, 0); crt.anchorMax = new Vector2(0.5f, 0); crt.pivot = new Vector2(0.5f, 0);
            crt.anchoredPosition = new Vector2(0, 8); crt.sizeDelta = new Vector2(BW * S, BH * S);   // default centre-bottom
            var cimg = cont.GetComponent<Image>();
            cimg.color = new Color(0, 0, 0, 0);   // invisible, but a raycast target so the whole bar is grab-draggable
            cont.GetComponent<DraggableUI>().Target = crt;

            var t = cont.transform;
            // bar background: upper row (y0) + lower row (y48), each = left piece + right piece
            QSPiece(t, S, 0, 0, 256, 48, R(0, 109, 256, 48));
            QSPiece(t, S, 256, 0, 204, 48, R(52, 157, 204, 48));
            QSPiece(t, S, 0, 48, 256, 48, R(0, 109, 256, 48));
            QSPiece(t, S, 256, 48, 204, 48, R(52, 157, 204, 48));

            // slot numbers 1-0 on both rows (upper y3, lower y51), at the m_PositionArray x's
            float[] sx = { 8, 48, 88, 128, 168, 256, 296, 336, 376, 416 };
            string[] num = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };
            for (int i = 0; i < 10; i++)
            {
                QSNum(t, S, sx[i] + 3, 4, num[i]);
                QSNum(t, S, sx[i] + 3, 52, num[i]);
            }

            // middle gap: khinh-công cell (upper, framed empty), peace<->battle toggle (lower),
            // prev/next set arrows. (Vận-khí sits stacked in the same slot in the client, so no
            // separate floating cell above the bar.)
            QSCell(t, S, 213, 3, 38, 40);
            QSToggle(t, S, 211, 50, 38, 38);
            QSPiece(t, S, 211, 90, 13, 6, R(0, 235, 13, 6));
            QSPiece(t, S, 236, 90, 13, 6, R(0, 241, 13, 6));

            // FUNCTIONAL layer on top of the art: real skill icons in each cell + number-key/click
            // activation + persistence (XWindow_QuickSlot). Added LAST so the icons draw above the
            // bar art and receive the clicks; the vận-khí cell above centre toggles the channel.
            var qs = cont.AddComponent<QuickSlotBar>();
            qs.Session = Session; qs.Assets = Assets;
            qs.Build(crt, S);
            _quickSlot = qs;
        }
        private QuickSlotBar _quickSlot;
        public QuickSlotBar QuickSlot => _quickSlot;   // for CharWindow's Kung Fu → quickslot drag/right-click

        private void QSCell(Transform parent, float S, float x, float y, float w, float h)
        {
            var go = new GameObject("qscell", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x * S, -y * S); rt.sizeDelta = new Vector2(w * S, h * S);
            var img = go.GetComponent<Image>();
            img.sprite = Assets.GetInterfaceSprite("MI_chat_quick.tga", R(48, 109, 40, 42));   // a single empty slot cell
            img.raycastTarget = false;
        }

        private void QSToggle(Transform parent, float S, float x, float y, float w, float h)
        {
            var go = new GameObject("qstoggle", typeof(RectTransform), typeof(Image), typeof(QsModeToggle));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x * S, -y * S); rt.sizeDelta = new Vector2(w * S, h * S);
            var img = go.GetComponent<Image>();
            var peace = Assets.GetInterfaceSprite("MI_chat_quick.tga", R(0, 157, 38, 38));
            img.sprite = peace;
            var tog = go.GetComponent<QsModeToggle>();
            tog.Img = img; tog.Peace = peace;
            tog.BattleFrames = new[]
            {
                Assets.GetInterfaceSprite("MI_chat_quick.tga", R(38, 205, 29, 29)),
                Assets.GetInterfaceSprite("MI_chat_quick.tga", R(67, 205, 29, 29)),
                Assets.GetInterfaceSprite("MI_chat_quick.tga", R(96, 205, 29, 29)),
                Assets.GetInterfaceSprite("MI_chat_quick.tga", R(125, 205, 29, 29)),
                Assets.GetInterfaceSprite("MI_chat_quick.tga", R(154, 205, 29, 29)),
            };
        }

        private void QSPiece(Transform parent, float S, float x, float y, float w, float h, Rect td)
        {
            var go = new GameObject("qsbg", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x * S, -y * S); rt.sizeDelta = new Vector2(w * S, h * S);
            var img = go.GetComponent<Image>();
            img.sprite = Assets.GetInterfaceSprite("MI_chat_quick.tga", td);
            img.raycastTarget = false;
        }

        private void QSNum(Transform parent, float S, float x, float y, string n)
        {
            var t = LabelTL(parent, n, 11, new Color(1f, 1f, 1f, 0.85f), TextAnchor.UpperLeft, x * S, -y * S, 14 * S, 14 * S);
            t.raycastTarget = false;
        }

        private void BuildPlayerFrame(Transform root)
        {
            // Top-left status frame cut from the client's own art at the exact rects it uses
            // (XWindow_JinStatus.cpp): frame = us_MI_char.tga (0,0,229,96) — full dragon emblem +
            // name panel + gauge tracks; HP groove at (84,41) 141x12, Force at (84,56) 141x12.
            // S matched to the bottom-right main-menu dragon cluster (BuildMainMenu S=1.3) so the two
            // dragon frames render at the same scale (was 1.5 → the top-left read as oversized).
            const float S = 1.0f, FW = 229f, FH = 82f, GX = 84f, GW = 141f, GH = 12f;   // native 1:1 (was 1.3)
            var frame = Panel(root, "PlayerFrame", new Color(0, 0, 0, 0f),
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(6, -6), new Vector2(FW * S, FH * S));

            if (Assets != null)
            {
                var bg = new GameObject("framebg", typeof(RectTransform), typeof(Image));
                bg.transform.SetParent(frame, false);
                var brt = (RectTransform)bg.transform;
                brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
                brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
                var bimg = bg.GetComponent<Image>();
                bimg.sprite = Assets.GetInterfaceSprite("us_MI_char.tga", new Rect(0, 0, FW, FH));
                bimg.raycastTarget = false;

                // dragon-head jin ring: MI_jinborder.tga (4x4 grid of 32x32), spins continuously
                // (XWindow_JinStatus.cpp: m_JinBorderFrame loops 0..14 at ~100ms/frame, drawn at 22,22).
                var ringGo = new GameObject("jinring", typeof(RectTransform), typeof(Image));
                ringGo.transform.SetParent(frame, false);
                var rrt = (RectTransform)ringGo.transform;
                rrt.anchorMin = new Vector2(0, 1); rrt.anchorMax = new Vector2(0, 1); rrt.pivot = new Vector2(0, 1);
                rrt.anchoredPosition = new Vector2(22 * S, -22 * S); rrt.sizeDelta = new Vector2(32 * S, 32 * S);
                var rimg = ringGo.GetComponent<Image>();
                rimg.raycastTarget = false;
                var ringFrames = new Sprite[15];
                for (int f = 0; f < 15; f++)
                    ringFrames[f] = Assets.GetInterfaceSprite("MI_jinborder.tga", new Rect((f % 4) * 32, (f / 4) * 32, 32, 32));
                rimg.sprite = ringFrames[0];
                var seq = ringGo.AddComponent<SpriteSeqAnim>();
                seq.Img = rimg; seq.Frames = ringFrames; seq.FrameSeconds = 0.1f; seq.Loop = true;

                // stat-point "+" button: MI_tra_base.tga. Running-light border cycles 5 glow frames
                // (g_AbilityPosition = x228, y0/28/56/84/112, 28x28); ready=172,118 click=200,118.
                var plusGo = new GameObject("abilityplus", typeof(RectTransform), typeof(Image));
                plusGo.transform.SetParent(frame, false);
                var prt = (RectTransform)plusGo.transform;
                prt.anchorMin = new Vector2(0, 1); prt.anchorMax = new Vector2(0, 1); prt.pivot = new Vector2(0, 1);
                prt.anchoredPosition = new Vector2(25 * S, -83 * S); prt.sizeDelta = new Vector2(28 * S, 28 * S);
                var pimg = plusGo.GetComponent<Image>();
                var glow = new Sprite[5];
                for (int f = 0; f < 5; f++)
                    glow[f] = Assets.GetInterfaceSprite("MI_tra_base.tga", new Rect(228, f * 28, 28, 28));
                pimg.sprite = glow[0];
                var ab = plusGo.AddComponent<AbilityPlusButton>();
                ab.Img = pimg; ab.GlowFrames = glow;
                ab.Ready = Assets.GetInterfaceSprite("MI_tra_base.tga", new Rect(172, 118, 28, 28));
                ab.Click = Assets.GetInterfaceSprite("MI_tra_base.tga", new Rect(200, 118, 28, 28));
                ab.FrameSeconds = 0.2f;
                // click → open the character window: Nhân-vật tab if spare ability points, else
                // the Ngộ-ý (mastery) tab (XProc_MainGameMessageHandler.cpp:12425-12444).
                ab.OnClick = () =>
                {
                    if (_charWindow == null) return;
                    var m = Session != null ? Session.Me : null;
                    int tab = (m != null && m.LeftPoint > 0) ? 0 : 2;
                    _charWindow.OpenAt(tab);
                };

                // side fire flames: MI_total_s.tga (4x4 grid, 30x47 per frame, 16 frames, looping)
                // XWindow_JinStatus.cpp: m_LeftFireImage at (x-6,y+23), m_RightFireImage at (x+54,y+23).
                var fireFrames = new Sprite[16];
                for (int f = 0; f < 16; f++)
                    fireFrames[f] = Assets.GetInterfaceSprite("MI_total_s.tga", new Rect((f % 4) * 30, (f / 4) * 47, 30, 47));
                AddFire(frame, S, -6, 23, fireFrames);   // left flame
                AddFire(frame, S, 54, 23, fireFrames);   // right flame
            }

            // name panel (top): realm/level line, then the character name.
            // Name is RIGHT-aligned (chủ: "canh phải, cách đều trên dưới phải") — native has this
            // exact draw at winWidth-4 (XWindow_JinStatus.cpp:937). The name box right edge sits at
            // GX+GW=225 = FW-4, so MiddleRight tucks the name into the top-right with an equal 4px gap
            // to the top / bottom / right of the grey name-plate groove (box y/height centre it).
            // dòng chức trách/grade (trên tên): CANH PHẢI như tên + màu TRẮNG NGÀ. Native
            // XWindow_JinStatus.cpp:933-934 (nhánh ENGLISH/VN) vẽ `{LevelName} : {sublevel}` bằng
            // PutsAlign RIGHT tại winW-4, màu _XSC_DEFAULT = RGB(189,188,175) (ivory); tên bên dưới là
            // 0xFFFFFFFF trắng tinh. Right-edge trùng tên (cùng width GW-3) (chủ 09/09).
            _levelText = LabelTL(frame, "", 11, new Color(189f / 255f, 188f / 255f, 175f / 255f), TextAnchor.UpperRight,
                GX * S, -6 * S, (GW - 3) * S, 14 * S);
            _nameText = LabelTL(frame, "", 14, Color.white, TextAnchor.MiddleRight,
                GX * S, -19 * S, (GW - 3) * S, 18 * S);   // right edge pulled in ~4px so gap phải = gap trên/dưới

            // HP + Force fills sitting exactly in the grooves, values centered on each
            _hpFill = Fill(frame, new Color(0.86f, 0.16f, 0.16f), GX * S, -41 * S, GW * S, GH * S);
            _forceFill = Fill(frame, new Color(0.18f, 0.40f, 0.92f), GX * S, -56 * S, GW * S, GH * S);
            _concFill = null;
            _hpText = LabelTL(frame, "", 10, Color.white, TextAnchor.MiddleCenter, GX * S, -41 * S, GW * S, GH * S);
            _forceText = LabelTL(frame, "", 10, Color.white, TextAnchor.MiddleCenter, GX * S, -56 * S, GW * S, GH * S);
            _concText = null;
            _playText = LabelTL(frame, "", 10, new Color(0.7f, 0.9f, 1f), TextAnchor.UpperLeft,
                GX * S, -73 * S, GW * S, 14 * S);

            _expFill = null;   // (removed the yellow bottom EXP border per the client look)
        }

        // One looping fire flame (MI_total_s frames) at frame-local (x,y), size 30x47.
        private void AddFire(Transform frame, float S, float x, float y, Sprite[] frames)
        {
            var go = new GameObject("fire", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(frame, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x * S, -y * S); rt.sizeDelta = new Vector2(30 * S, 47 * S);
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            img.sprite = frames[0];
            var seq = go.AddComponent<SpriteSeqAnim>();
            seq.Img = img; seq.Frames = frames; seq.FrameSeconds = 0.1f; seq.Loop = true;
        }

        private void BuildHotbar(Transform root)
        {
            var bar = Panel(root, "Hotbar", new Color(0, 0, 0, 0.4f),
                new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(0, 14), new Vector2(10 * 46 + 8, 52));
            for (int i = 0; i < 10; i++)
            {
                var slot = Panel(bar, "slot" + i, new Color(0.15f, 0.15f, 0.2f, 0.85f),
                    new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                    new Vector2(6 + i * 46, 0), new Vector2(42, 42));
                Label(slot, ((i + 1) % 10).ToString(), 11, new Color(1, 1, 1, 0.6f),
                    TextAnchor.LowerRight, new Vector2(1, 0), new Vector2(1, 0),
                    new Vector2(-4, 3), new Vector2(16, 14));
            }
        }

        /// <summary>Đặt một mảnh sprite theo GÓC TRÊN-TRÁI (x,y từ mép trên-trái của parent, y dương =
        /// xuống) với kích thước (w,h). Sprite cắt từ atlas giao diện; nếu thiếu Assets thì tô màu
        /// fallback để khỏi trống. Dùng dựng khung minimap từ ảnh thật (không vẽ tay).</summary>
        private Image SpriteTL(Transform parent, string name, string tga, Rect clipTD,
                              float x, float y, float w, float h, Color fallback, bool flipX = false)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
            if (flipX) rt.localScale = new Vector3(-1f, 1f, 1f);   // lật ngang (pivot góc trái → phủ về bên TRÁI của x)
            var img = go.GetComponent<Image>();
            var sp = Assets != null ? Assets.GetInterfaceSprite(tga, clipTD) : null;
            if (sp != null) { img.sprite = sp; img.color = Color.white; }
            else img.color = fallback;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Đường dọc 1px (viền), đặt theo góc trên-trái tại cột x, cao h.</summary>
        private void VLine(Transform parent, string name, float x, Color c, float h)
        {
            var p = Panel(parent, name, c, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(x, 0), new Vector2(1, h));
            p.GetComponent<Image>().raycastTarget = false;
        }

        // Minimap 2 CHẾ ĐỘ (native XWindow_WorldMinimap.cpp): THU NHỎ = RADAR quanh người chơi (crop+zoom,
        // người ở GIỮA); PHÓNG TO = TOÀN MAP (Resize Map). Nút hình thoi ◇/◆ toggle. Khung sprite
        // us_MI_char.tga, nền TỐI ĐẶC lấp hở, viền nâu mỏng + nắp kim loại 2 đầu mirror.
        private bool _minimapMax;          // false = radar thu nhỏ; true = toàn map phóng to
        private RawImage _minimapMapImg;   // RawImage map (Refresh đổi uvRect theo chế độ)

        private void BuildMinimap(Transform root)
        {
            _minimapRect = Panel(root, "Minimap", new Color(15f / 255f, 15f / 255f, 13f / 255f, 1f),
                new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(-8, -8), new Vector2(154, 201));
            RebuildMinimap();
        }

        // Dựng lại minimap theo chế độ hiện tại (gọi khi bấm Resize Map). Thu nhỏ 154×201 / Phóng to 430×452.
        private void RebuildMinimap()
        {
            const string TGA = "us_MI_char.tga";
            for (int i = _minimapRect.childCount - 1; i >= 0; i--) DestroyImmediate(_minimapRect.GetChild(i).gameObject);
            float FW = _minimapMax ? 430f : 154f;
            float FH = _minimapMax ? 452f : 201f;
            _minimapRect.sizeDelta = new Vector2(FW, FH);
            float MAPX = 2f, MAPY = 33f, MAPW = FW - 4f, MAPH = FH - MAPY - 18f;   // dưới header 33, trên thanh đáy 18
            var inner = Panel(_minimapRect, "inner", new Color(0.05f, 0.07f, 0.06f, 1f),
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(MAPX, -MAPY), new Vector2(MAPW, MAPH));
            inner.GetComponent<Image>().raycastTarget = false;
            inner.gameObject.AddComponent<RectMask2D>();
            _minimapMapImg = null; _minimapMap = null;
            if (MinimapTex != null)
            {
                var riGo = new GameObject("map", typeof(RectTransform));
                riGo.transform.SetParent(inner, false);
                var rrt = (RectTransform)riGo.transform;
                rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one; rrt.offsetMin = Vector2.zero; rrt.offsetMax = Vector2.zero;
                var ri = riGo.AddComponent<RawImage>();
                ri.texture = MinimapTex; ri.color = new Color(1, 1, 1, 0.95f); ri.raycastTarget = false;
                ri.uvRect = new Rect(0f, 1f, 1f, -1f);   // lật DỌC (texture top-down); Refresh set lại theo chế độ
                _minimapMapImg = ri; _minimapMap = rrt;
            }
            _minimapBlips = Panel(inner, "blips", new Color(0, 0, 0, 0),
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            _minimapBlips.GetComponent<Image>().raycastTarget = false;
            var dot = Panel(inner, "playermark", new Color(0.35f, 0.95f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(7, 7));
            dot.localRotation = Quaternion.Euler(0, 0, 45); dot.GetComponent<Image>().raycastTarget = false;
            _minimapDot = dot;

            // khung sprite (thanh co giãn theo FW). Nắp kim loại 2 đầu (full clip x0 + mirror flipX phải).
            SpriteTL(_minimapRect, "botbar", TGA, new Rect(3, 238, 93, 18), 2, FH - 18, FW - 4, 18, new Color(0.2f, 0.13f, 0.13f, 1f));
            SpriteTL(_minimapRect, "subbar", TGA, new Rect(0, 223, 150, 15), 0, 17, FW, 15, new Color(0.12f, 0.12f, 0.1f, 1f));
            SpriteTL(_minimapRect, "subbarR", TGA, new Rect(0, 223, 6, 15), FW, 17, 6, 15, new Color(0.12f, 0.12f, 0.1f, 1f), true);
            SpriteTL(_minimapRect, "titlebar", TGA, new Rect(0, 206, 153, 17), 0, 0, FW, 17, new Color(0.1f, 0.1f, 0.09f, 1f));
            SpriteTL(_minimapRect, "titlebarR", TGA, new Rect(0, 206, 6, 17), FW, 0, 6, 17, new Color(0.1f, 0.1f, 0.09f, 1f), true);
            var brown = new Color(123f / 255f, 97f / 255f, 95f / 255f, 0.5f);
            VLine(_minimapRect, "eL", 0, brown, FH);
            VLine(_minimapRect, "eR", FW - 1, brown, FH);
            LabelTL(_minimapRect, MapName, 11, new Color(206f / 255f, 206f / 255f, 206f / 255f), TextAnchor.MiddleLeft, 4, -17, FW - 40, 15);
            _minimapCoord = LabelTL(_minimapRect, "", 9, new Color(205f / 255f, 198f / 255f, 166f / 255f), TextAnchor.MiddleRight, 20, -17, FW - 24, 15);

            // nút RESIZE MAP (hình thoi) — góc dưới-phải trên thanh đáy: toggle radar ↔ toàn map (native).
            MinimapZoomBtn(_minimapRect, _minimapMax ? "◆" : "◇", FW - 22, 1, () => { _minimapMax = !_minimapMax; RebuildMinimap(); });
            // nút zoom −/+ CHỈ khi radar (thu nhỏ) — phóng to thì đã là toàn map, không cần
            if (!_minimapMax)
            {
                MinimapZoomBtn(inner, "−", 3, 3, () => _minimapZoom = Mathf.Max(1f, _minimapZoom - 0.5f));
                MinimapZoomBtn(inner, "+", 25, 3, () => _minimapZoom = Mathf.Min(4f, _minimapZoom + 0.5f));
            }
        }

        private void MinimapZoomBtn(Transform parent, string label, float x, float y, System.Action onClick)
        {
            var b = Panel(parent, "z" + label, new Color(0.05f, 0.05f, 0.06f, 0.85f),
                new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(x, y), new Vector2(20, 20));
            var o = b.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0.7f, 0.62f, 0.4f, 0.95f); o.effectDistance = new Vector2(1, -1);
            Label(b, label, 18, new Color(1f, 0.95f, 0.75f), TextAnchor.MiddleCenter,
                new Vector2(0, 0), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            var btn = b.gameObject.AddComponent<Button>(); btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => onClick());
        }

        private static readonly Color BlipPlayer  = new Color(0.9f, 0.9f, 0.95f);
        private static readonly Color BlipMonster = new Color(0.95f, 0.35f, 0.3f);
        private static readonly Color BlipNpc     = new Color(1f, 0.85f, 0.25f);   // NPCs = yellow

        /// <summary>Redraw the minimap entity blips (players white, alive monsters red) at their
        /// world position relative to me, and the player heading. Pooled Images under _minimapBlips.</summary>
        private void UpdateMinimapBlips(MyCharacter me)
        {
            if (_minimapBlips == null || Session == null) return;
            float w = _minimapBlips.rect.width, h = _minimapBlips.rect.height;
            if (w <= 0 || h <= 0) return;
            int n = 0;
            void Blip(float wx, float wz, Color c)
            {
                RectTransform b;
                if (n < _minimapBlips.childCount) b = (RectTransform)_minimapBlips.GetChild(n);
                else
                {
                    b = Panel(_minimapBlips, "b", c, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                        new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4, 4));
                    b.GetComponent<Image>().raycastTarget = false;
                }
                b.GetComponent<Image>().color = c;
                if (_minimapMax)
                {
                    float bnx = Mathf.Clamp01((wx + MapWorldSize * 0.5f) / MapWorldSize);
                    float bnz = Mathf.Clamp01((wz + MapWorldSize * 0.5f) / MapWorldSize);
                    b.anchoredPosition = new Vector2((bnx - 0.5f) * w, (bnz - 0.5f) * h);   // vị trí thật trên toàn map (Z theo chiều ảnh)
                }
                else
                {
                    // radar: vị trí trong cửa sổ đã CLAMP (tâm _radarCx/Cy, cỡ _radarWin) — khớp map + dot
                    float bnx = Mathf.Clamp01((wx + MapWorldSize * 0.5f) / MapWorldSize);
                    float bnz = Mathf.Clamp01((wz + MapWorldSize * 0.5f) / MapWorldSize);
                    b.anchoredPosition = new Vector2((bnx - _radarCx) / _radarWin * w, (bnz - _radarCy) / _radarWin * h);
                }
                b.gameObject.SetActive(true);
                n++;
            }
            foreach (var p in Session.Players.Values) Blip(p.X, p.Z, BlipPlayer);
            foreach (var m in Session.World.AliveMonsters) Blip(m.X, m.Z, BlipMonster);
            foreach (var npc in MinimapNpcs) Blip(npc.x, npc.y, BlipNpc);
            for (int i = n; i < _minimapBlips.childCount; i++) _minimapBlips.GetChild(i).gameObject.SetActive(false);
        }

        private void BuildChat(Transform root)
        {
            // bottom-left chat, mirroring _XWindow_ChattingDefault: a colored log above a 21px bar
            // that carries a channel button (left ~90px) + the input edit (260x12), 15px/line.
            const float CW = 384f, LINE = 15f, BAR = 23f, LINES = 7;
            const float CH = LINE * LINES + BAR + 6f;
            var chat = Panel(root, "Chat", new Color(0, 0, 0, 0.34f),
                new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(8, 26), new Vector2(CW, CH));

            // log lines (bottom-up: line[0] is the newest, sitting just above the bar);
            // top-left y grows negative downward, so line i sits LINE*(i+1) above the bar top.
            _chatLines = new Text[(int)LINES];
            for (int i = 0; i < LINES; i++)
                _chatLines[i] = LabelTL(chat, "", 12, ChatColors[7], TextAnchor.LowerLeft,
                    6, -(CH - BAR - LINE * (i + 1)), CW - 12, LINE);

            // channel bar (uses MI_chat_quick.tga art if available, else a dark strip)
            var barSprite = Assets != null ? Assets.GetInterfaceSprite("MI_chat_quick.tga", new Rect(0, 0, 256, 21)) : null;
            var bar = Panel(chat, "bar", new Color(0.06f, 0.06f, 0.07f, 0.9f),
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0), new Vector2(0, 1), new Vector2(0, BAR));
            if (barSprite != null && barSprite.texture != null && barSprite.texture.width > 2)
            { var im = bar.GetComponent<Image>(); im.sprite = barSprite; im.type = Image.Type.Sliced; im.color = Color.white; }

            // channel button on the left — current send channel, tinted with its colour
            var chBtn = Panel(bar, "chan", new Color(0, 0, 0, 0.5f),
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(4, 0), new Vector2(80, -5));
            Outline(chBtn, new Color(0.5f, 0.45f, 0.3f, 0.8f));
            _chatChannelLabel = LabelTL(chBtn, ChannelName(_chatChannel) + " ▾", 12, ChatColors[_chatChannel], TextAnchor.MiddleCenter,
                0, -1, 80, BAR - 7);
            // clicking the channel button cycles the send channel (Nói→Đội→Bang→Toàn→Thông báo)
            var chBtnBtn = chBtn.gameObject.AddComponent<Button>();
            chBtnBtn.transition = Selectable.Transition.None;
            chBtnBtn.onClick.AddListener(() =>
            {
                int ci = System.Array.IndexOf(ChatChannelCycle, _chatChannel);
                _chatChannel = ChatChannelCycle[(ci + 1) % ChatChannelCycle.Length];
                _chatChannelLabel.text = ChannelName(_chatChannel) + " ▾";
                _chatChannelLabel.color = ChatColors[_chatChannel];
            });

            // input edit box to the right of the channel button (native 260x12 at x+96); a 10px gap
            // from the channel button and an 8px right margin give the bar some breathing room.
            var inGo = new GameObject("chatinput", typeof(RectTransform), typeof(Image));
            inGo.transform.SetParent(bar, false);
            var irt = (RectTransform)inGo.transform;
            irt.anchorMin = new Vector2(0, 0.5f); irt.anchorMax = new Vector2(1, 0.5f); irt.pivot = new Vector2(0, 0.5f);
            irt.anchoredPosition = new Vector2(94, 0); irt.sizeDelta = new Vector2(-102, BAR - 7);
            inGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.45f);
            var txt = LabelTL(irt, "", 12, Color.white, TextAnchor.MiddleLeft, 5, -1, 260, BAR - 9);
            txt.supportRichText = false;
            var ph = LabelTL(irt, "Nhấn Enter để trò chuyện…", 12, new Color(0.6f, 0.6f, 0.6f), TextAnchor.MiddleLeft, 5, -1, 260, BAR - 9);
            _chatInput = inGo.AddComponent<InputField>();
            _chatInput.textComponent = txt; _chatInput.placeholder = ph;
            _chatInput.characterLimit = 80;   // SetMaxLength(80)
            _chatInput.lineType = InputField.LineType.SingleLine;
            _chatInput.onEndEdit.AddListener(SubmitChat);   // Enter sends (see SubmitChat)

            // seed a few system lines so the log reads like the live client at spawn
            AppendChat(7, "[Hệ thống] Chào mừng đến với Cửu Long.");
            AppendChat(6, "[Thông báo] Sự kiện Tết Trung Thu đang diễn ra.");
            AppendChat(0, "Aladdin: xin chào bằng hữu!");

            if (!ShowChat) chat.gameObject.SetActive(false);   // TEMP: hidden (see ShowChat)
        }

        /// <summary>Append one chat line (col = index into ChatColors / _XDEF_CHATMESSAGECOLOR_*).
        /// Keeps a ring buffer; the visible lines are refreshed in Update().</summary>
        public void AppendChat(int col, string msg)
        {
            lock (_chatLock)
            {
                _chatBuf.Add((Mathf.Clamp(col, 0, ChatColors.Length - 1), msg ?? ""));
                if (_chatBuf.Count > 200) _chatBuf.RemoveRange(0, _chatBuf.Count - 200);
            }
        }

        // InputField.onEndEdit fires on Enter OR focus-loss; send only on an actual Enter so a
        // click-away never sends. Sends over the wire; offline (demo) echoes locally as MYMESSAGE.
        private void SubmitChat(string text)
        {
            bool enter = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
            if (!enter || string.IsNullOrEmpty(text)) return;
            bool sent = Session != null && Session.SendChat(_chatChannel, text);
            if (!sent)
            {
                string me = Session != null ? Session.Me.Name : "";
                AppendChat(_chatChannel == 0 ? 1 : _chatChannel, string.IsNullOrEmpty(me) ? text : $"{me}: {text}");
            }
            if (_chatInput != null) { _chatInput.text = ""; _chatInput.ActivateInputField(); }
        }

        private void Outline(RectTransform rt, Color c)
        {
            var o = rt.gameObject.AddComponent<Outline>();
            o.effectColor = c; o.effectDistance = new Vector2(1, -1);
        }

        private void BuildHelp(Transform root)
        {
            Label(root, "Left-click: move   RMB/edge: rotate   Wheel: zoom   C: character   F11: collection",
                12, new Color(1, 1, 1, 0.5f), TextAnchor.LowerCenter,
                new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 74), new Vector2(900, 18));
        }

        // ---------------- live update ----------------
        private void Update()
        {
            if (Session == null) return;
            var me = Session.Me;

            if (Input.GetKeyDown(KeyCode.F11) && _f11Window != null)
                _f11Window.Toggle();

            _nameText.text = me.Name;
            // grade line (above the name) mirrors XWindow_JinStatus.cpp:929 "%s : %d" =
            // g_LevelName[GradeRank] : SubLevel(thành), e.g. "Thiên Ngoại Tiên : 11". Grade name is
            // resolved live from the string DB (LevelName / EODString_XSR) by the derived grade-rank;
            // both are derived from InnerLevel. Numeric fallback if InnerLevel/name are unavailable.
            if (me.InnerLevel > 0 && string.IsNullOrEmpty(me.GradeName) && Assets != null)
            {
                var gn = Assets.GradeName(me.GradeRank);
                if (!string.IsNullOrEmpty(gn)) me.GradeName = gn;
            }
            _levelText.text = (me.InnerLevel > 0 && !string.IsNullOrEmpty(me.GradeName))
                ? $"{me.GradeName} : {me.SubLevel}"
                : $"Lv {Level}  {ClassName(me.Class)}";
            SetBar(_hpFill, _hpText, me.CurLife, me.MaxLife, "HP");
            SetBar(_forceFill, _forceText, me.CurForce, me.MaxForce, "Force");
            if (_playText != null)
            {
                int t = (int)Time.time;
                _playText.text = $"{IT(3149, "Thời gian chơi")}  {t / 3600:00}:{t % 3600 / 60:00}:{t % 60:00}";   // ID 3149 = Exp Remaining Time
            }

            // EXP fill uses the low 16 bits as a coarse progress hint (server sends Jin)
            if (_expFill != null) _expFill.fillAmount = Mathf.Clamp01((me.Exp & 0xFFFF) / 65535f);

            if (_minimapMapImg != null && _minimapDot != null && _minimapBlips != null)
            {
                float w = _minimapBlips.rect.width, h = _minimapBlips.rect.height;
                float nx = Mathf.Clamp01((me.X + MapWorldSize * 0.5f) / MapWorldSize);
                float nz = Mathf.Clamp01((me.Z + MapWorldSize * 0.5f) / MapWorldSize);
                if (_minimapMax)
                {
                    // TOÀN MAP: hiện cả texture (lật dọc), dot ở đúng vị trí thật trên map.
                    // Trục Z: (nz-0.5) — dot ĐI THEO chiều ảnh (chủ 12/09: char ở Shopping Street/dưới mà
                    // dot lên Highland/trên → trục Z bị lật; ảnh map ĐÚNG nên chỉ đảo dot/blip, giữ X + ảnh).
                    _minimapMapImg.uvRect = new Rect(0f, 1f, 1f, -1f);
                    ((RectTransform)_minimapDot).anchoredPosition = new Vector2((nx - 0.5f) * w, (nz - 0.5f) * h);
                }
                else
                {
                    // RADAR: cửa sổ 1/zoom quanh người chơi, nhưng CLAMP tâm trong [0,1] để UV KHÔNG ra
                    // ngoài → hết "chia đôi ghép ngược" (wrap do wrapMode Repeat). Gần biên map thì cửa sổ
                    // dừng ở biên, DOT lệch tâm (đúng native RenderClampMode). zoom=1 → full map (không wrap).
                    float winSize = 1f / _minimapZoom, halfWin = 0.5f / _minimapZoom;
                    float cx = Mathf.Clamp(nx, halfWin, 1f - halfWin);
                    float cy = Mathf.Clamp(nz, halfWin, 1f - halfWin);
                    _radarCx = cx; _radarCy = cy; _radarWin = winSize;   // cho UpdateMinimapBlips
                    _minimapMapImg.uvRect = new Rect(cx - halfWin, (1f - cy) + halfWin, winSize, -winSize);  // V-flip
                    ((RectTransform)_minimapDot).anchoredPosition = new Vector2((nx - cx) / winSize * w, (nz - cy) / winSize * h);
                }
                UpdateMinimapBlips(me);
                if (_minimapCoord != null)
                    // native hiện HalfWorldSize + pos (XWindow_WorldMinimap.cpp:1083-1086) → khớp client thật
                    _minimapCoord.text = $"{Mathf.RoundToInt(MapWorldSize * 0.5f + me.X)} , {Mathf.RoundToInt(MapWorldSize * 0.5f + me.Z)}"
                        + (!_minimapMax && _minimapZoom > 1f ? $"  x{_minimapZoom:0.#}" : "");
            }

            UpdateChatLog();
        }

        // Enter toggles the chat input focus / sends; render the ring buffer into the line labels.
        private void UpdateChatLog()
        {
            if (_chatLines == null) return;
            // subscribe once the session is available: incoming server chat → colored log line
            if (!_chatHooked && Session != null)
            { Session.ChatReceived += AppendChat; _chatHooked = true; }

            // Enter while NOT focused opens the input; the actual send is on onEndEdit (SubmitChat),
            // so a single Enter can't both send and re-open (the earlier focus-vs-Return race).
            if (ShowChat && _chatInput != null && !_chatInput.isFocused &&
                (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
                _chatInput.ActivateInputField();
            lock (_chatLock)
            {
                for (int i = 0; i < _chatLines.Length; i++)
                {
                    int idx = _chatBuf.Count - 1 - i;   // line[0] = newest, just above the bar
                    if (idx >= 0)
                    {
                        var (col, msg) = _chatBuf[idx];
                        _chatLines[i].text = msg;
                        _chatLines[i].color = ChatColors[col];
                    }
                    else _chatLines[i].text = "";
                }
            }
        }

        private void SetBar(Image fill, Text text, int cur, int max, string tag)
        {
            if (fill == null) return;
            float f = max > 0 ? Mathf.Clamp01((float)cur / max) : 0f;
            fill.fillAmount = f;
            // low-health warning: the HP bar blinks (pulses) when at or below 10%
            if (tag == "HP")
            {
                if (f > 0f && f <= 0.10f)
                {
                    float p = 0.35f + 0.65f * Mathf.Abs(Mathf.Sin(Time.time * 6.5f));   // pulse alpha
                    fill.color = new Color(0.92f, 0.10f, 0.10f, p);
                }
                else fill.color = new Color(0.86f, 0.16f, 0.16f, 1f);
            }
            if (text != null) text.text = $"{cur}/{max}";
        }

        private static string ClassName(int c)
        {
            switch (c)
            {
                case 1: return "Warrior";
                case 2: return "Hybrid";
                case 3: return "Nuker";
                case 4: return "Healer";
                default: return "Novice";
            }
        }
    }
}
