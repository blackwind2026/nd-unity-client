using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using NineDragons.Assets;

namespace NineDragons.Client
{
    /// <summary>
    /// One place that owns the open .XP archives and caches decoded assets.
    /// Archives cost only their directory in memory (XpArchive is file-backed), so all the
    /// texture packages can stay open; textures/models are decoded once and cached.
    /// </summary>
    public sealed class AssetService : IDisposable
    {
        private XpArchive _models;                 // MR_BASEMODEL.XP  (.x3d/.xbp)
        private XpArchive _environment;            // VR_ENVIRONMENT.XP (.xtv + sidecars)
        private XpArchive _scripts;                // SCR_BASE.XP (Zonelist.ini, *.XMS, ...)
        private XpArchive _anim;                   // AR_BASEANIMATION.XP (NPCModel.XMS, *.XA)
        private XpArchive _sound;                  // SR_SOUND.XP (bgm .ogg, sfx .wav)
        private XpArchive _interface;              // TR_MAININTERFACE.XP (client g_MainInterfaceTextureArchive: HUD/window art)
        private NpcModelTable _npcModels;
        private NpcModelTable _mobModels;
        private NineDragons.Data.ItemTable _items;
        private NineDragons.Data.SkillTable _skills;
        private CharModelTable _charModels;
        private readonly List<XpArchive> _texturePacks = new List<XpArchive>();

        private readonly Dictionary<string, Texture2D> _textures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MeshFactory.StaticModel> _staticModels =
            new Dictionary<string, MeshFactory.StaticModel>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, X3dModel> _rawModels =
            new Dictionary<string, X3dModel>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Material> _materials =
            new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture2D> _ifaceTex =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Sprite> _ifaceSprites =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        private Texture2D _missingTexture;
        private Shader _objectShader;

        // baked SpeedTree models (.ndtree in StreamingAssets/trees) + per-species height from speedtreelist.ini
        private readonly Dictionary<string, NdTreeModel> _trees =
            new Dictionary<string, NdTreeModel>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, float> _treeScales;
        private Dictionary<string, string> _treeComposite;   // spt -> composite atlas (leaf/frond texture)

        private string[] _grassModelNames;
        private readonly Dictionary<string, Material> _grassMats =
            new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Grass model .x3d names from GrassModel.XMS (SCR_BASE.XP); empty on failure.</summary>
        public string[] GetGrassModelNames()
        {
            if (_grassModelNames != null) return _grassModelNames;
            var list = new List<string>();
            try
            {
                if (_scripts.TryExtract("GrassModel.XMS", out var raw))
                {
                    var text = System.Text.Encoding.ASCII.GetString(raw);
                    foreach (var lineRaw in text.Split('\n'))
                    {
                        var line = lineRaw.Trim();
                        if (line.Length == 0 || line.StartsWith("_XM_") || !line.ToLowerInvariant().Contains(".x3d")) continue;
                        // columns: <xbp>  <x3d>  <xa>
                        var cols = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var c in cols)
                            if (c.EndsWith(".x3d", StringComparison.OrdinalIgnoreCase)) { list.Add(c); break; }
                    }
                }
            }
            catch { }
            _grassModelNames = list.ToArray();
            return _grassModelNames;
        }

        /// <summary>Wind-swaying, alpha-cut material for a grass texture (NdGrassModel). Cached.</summary>
        public Material GetGrassMaterial(string textureName)
        {
            string key = textureName ?? "";
            if (_grassMats.TryGetValue(key, out var m)) return m;
            m = new Material(NdShaders.GrassModel) { name = "grass:" + key };
            m.mainTexture = HdTexture(key);   // HD grass
            m.enableInstancing = true;
            _grassMats[key] = m;
            return m;
        }

        /// <summary>Baked tree for a .spt name (e.g. "w_Bamboo_270.spt"); null if not baked.</summary>
        public NdTreeModel GetTreeModel(string sptName)
        {
            if (string.IsNullOrEmpty(sptName)) return null;
            if (_trees.TryGetValue(sptName, out var m)) return m;
            m = null;
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, "trees", sptName + ".ndtree");
                if (File.Exists(path)) m = NdTreeModel.Parse(File.ReadAllBytes(path));
            }
            catch { m = null; }
            _trees[sptName] = m;
            return m;
        }

        /// <summary>Target tree height in world units for a .spt (speedtreelist.ini &lt;_TREESCALE_&gt;); 0 if unknown.</summary>
        public float GetTreeScale(string sptName)
        {
            if (_treeScales == null) LoadTreeScales();
            if (sptName != null && _treeScales.TryGetValue(sptName, out var s)) return s;
            return 0f;
        }

        /// <summary>Composite atlas that carries this tree's leaf/frond imagery (speedtreelist
        /// &lt;_COMPOSITION_MAP_&gt;) — the client binds it as the leaf texture; empty if unknown.</summary>
        public string GetTreeComposite(string sptName)
        {
            if (_treeScales == null) LoadTreeScales();
            if (sptName != null && _treeComposite.TryGetValue(sptName, out var c)) return c;
            return "";
        }

        private void LoadTreeScales()
        {
            _treeScales = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            _treeComposite = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string p = Path.Combine(Application.streamingAssetsPath, "trees", "speedtreelist.ini");
                if (!File.Exists(p)) return;
                string lastSpt = null, curComposite = "";
                foreach (var raw in File.ReadAllLines(p))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';') continue;
                    if (line.StartsWith("<_COMPOSITION_MAP_>"))
                        curComposite = line.Substring("<_COMPOSITION_MAP_>".Length).Trim();
                    else if (line.EndsWith(".spt", StringComparison.OrdinalIgnoreCase))
                    {
                        lastSpt = line;
                        _treeComposite[lastSpt] = curComposite;
                    }
                    else if (line.StartsWith("<_TREESCALE_>") && lastSpt != null)
                    {
                        var t = line.Substring("<_TREESCALE_>".Length).Trim();
                        if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                            _treeScales[lastSpt] = v;
                        lastSpt = null;
                    }
                }
            }
            catch { }
        }

        // world objects reference textures from the environment pack first (client:
        // g_WorldObjectTextureArchive), then the others as fallback
        private static readonly string[] TexturePackNames =
        {
            "TR_ENVIRONMENT.XP", "TR_SSTMODEL.XP", "TR_CHARACTER.XP",
            "TR_MONSTER.XP", "TR_VISUALEFFECT.XP", "TR_EFFECT.XP"
        };

        public void Open()
        {
            if (!ClientData.Exists)
                throw new DirectoryNotFoundException($"client data not found at '{ClientData.Root}'");
            // MR_BASEMODEL.XP is OPTIONAL now: every mesh has a converted res\Model\9DModel\<name>.glb
            // and the .xbp skeletons live loose, so the model pack can be retired. Tolerate its absence.
            string modelsPack = Path.Combine(ClientData.Model, "MR_BASEMODEL.XP");
            _models = File.Exists(modelsPack) ? XpArchive.Open(modelsPack) : null;
            // Optional release pack of the converted .glb (loose res\Model\9DModel\ still overrides it).
            // Chủ 09/09: folder = 9DModel; sau này NÉN cả folder thành 1 file — đuôi CHƯA chốt (.XP/.package/…),
            // nên tìm bất kỳ "9DModel.*" trong Model. ⚠️ định dạng nén phải là XP raw pack thì XpArchive.Open
            // đọc được; chọn định dạng khác thì đổi loader chỗ này.
            string gltfPack = null;
            if (Directory.Exists(ClientData.Model))
            {
                var packs = Directory.GetFiles(ClientData.Model, GltfDirName + ".*");
                if (packs.Length > 0) gltfPack = packs[0];
            }
            _gltfPack = gltfPack != null ? XpArchive.Open(gltfPack, false) : null;   // packed raw (unencrypted)
            _environment = XpArchive.Open(Path.Combine(ClientData.Environment, "VR_ENVIRONMENT.XP"));
            _scripts = XpArchive.Open(Path.Combine(ClientData.Script, "SCR_BASE.XP"));
            _anim = XpArchive.Open(Path.Combine(ClientData.Model, "AR_BASEANIMATION.XP"));
            string soundPath = Path.Combine(ClientData.Root, "Sound", "SR_SOUND.XP");
            if (File.Exists(soundPath)) _sound = XpArchive.Open(soundPath);
            string ifacePath = Path.Combine(ClientData.Texture, "TR_MAININTERFACE.XP");
            if (File.Exists(ifacePath)) _interface = XpArchive.Open(ifacePath);
            foreach (var name in TexturePackNames)
            {
                string p = Path.Combine(ClientData.Texture, name);
                if (File.Exists(p)) _texturePacks.Add(XpArchive.Open(p));
            }
            _objectShader = NdShaders.ObjectCutout;
        }

        /// <summary>Zone id → map name table (Zonelist.ini from SCR_BASE.XP).</summary>
        public ZoneTable LoadZoneTable() => ZoneTable.Parse(_scripts.Extract("Zonelist.ini"));

        // map name (no .xtv, case-insensitive) → BGM .ogg pair, from Zonelist.ini. The C++ client
        // keys BGM by zone id (g_ZoneInfoTable[zone].zonebgmfilename1/2); the Unity map/viewer paths
        // only hold the map name, so we resolve by map name instead — same source columns:
        // id short long title FILE bp.x bp.y bp.z service BGM1 BGM2 mapsize ...  (BGM1=token 9, BGM2=10).
        // BGM2 is the alternate track the client crossfades to on its 180s (US)/120s (JP) timer.
        private Dictionary<string, string> _mapBgm;
        private Dictionary<string, string> _mapBgm2;

        /// <summary>Primary looping background-music file (e.g. "hapbi.ogg") for a map name such as
        /// "map_hapbi"/"Map_Bookhae"; null if the map has no BGM column. Parsed once from Zonelist.ini.</summary>
        public string MapBgm(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return null;
            if (_mapBgm == null) BuildMapBgm();
            return _mapBgm.TryGetValue(mapName, out var b) ? b : null;
        }

        /// <summary>Secondary/alternate BGM file (zonebgmfilename2) for a map; the client alternates
        /// to it every 180s. Falls back to the primary when the map lists only one track; null if none.</summary>
        public string MapBgm2(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return null;
            if (_mapBgm == null) BuildMapBgm();
            if (_mapBgm2.TryGetValue(mapName, out var b) && !string.IsNullOrEmpty(b)) return b;
            return _mapBgm.TryGetValue(mapName, out var b1) ? b1 : null;   // fall back to primary
        }

        private void BuildMapBgm()
        {
            _mapBgm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _mapBgm2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!_scripts.TryExtract("Zonelist.ini", out var raw)) return;
                using var reader = new StringReader(System.Text.Encoding.GetEncoding(28591).GetString(raw));
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("_XM_EOS")) break;
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == ';') continue;
                    var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length <= 9) continue;
                    if (!int.TryParse(tok[0], out _)) continue;
                    string file = tok[4];
                    if (file.EndsWith(".xtv", StringComparison.OrdinalIgnoreCase))
                        file = file.Substring(0, file.Length - 4);
                    if (tok[9].EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                        _mapBgm[file] = tok[9];
                    if (tok.Length > 10 && tok[10].EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                        _mapBgm2[file] = tok[10];
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[AssetService] map BGM table: {ex.Message}"); }
        }

        /// <summary>item_table.bit (SCR_BASE.XP), cached — for equipped cloth model indices.</summary>
        public NineDragons.Data.ItemTable Items
        {
            get
            {
                if (_items == null && _scripts.TryExtract("item_table.bit", out var d))
                    _items = NineDragons.Data.ItemTable.Load(d);
                return _items;
            }
        }

        /// <summary>Real item-icon sprite for an inventory item, from its per-type Item_*.tga atlas
        /// (8 columns × 32 px), sliced by the item's sIcon_No (col = icon%8, row = icon/8). Mirrors
        /// XWindow_Inventory::GetIconRect. Weapon + cloth resolved here (weapon uses the sword atlas
        /// pending a thirdType→atlas port); returns null for anything unresolved (caller falls back to
        /// a category icon). Cloth atlas = Item_Clo_{clan:00-06}{a upper/b lower/c gloves/d shoes/e cap}.</summary>
        // weapon SecondType (0 SHORT / 1 LONG / 5 SPECIAL) → ThirdType → Item_Wea_*.tga (item_icons.md §3)
        private static readonly string[] WeaShort = { "Item_Wea_13.tga", "Item_Wea_14.tga", "Item_Wea_02.tga",
            "Item_Wea_03.tga", "Item_Wea_04.tga", "Item_Wea_05.tga", "Item_Wea_06.tga", "Item_Wea_w22.tga" };
        private static int Pg(int iconRaw) => iconRaw > 0 ? (iconRaw - 1) / 64 : 0;   // atlas page (64 icons/file)

        // POTION atlas: file chosen by cSecondType (NOT by page), ladder ported 1:1 from
        // CodexResolvePotionIconResource (item_icons.md §3). One group (EVENT/MASK/ANTI/COOLDOWN)
        // is page-dependent → fileNumber 6 (page 0) / 9 (page ≥1). Suffix always == fileNumber.
        private static string PotionAtlas(int secondType, int iconRaw)
        {
            int fn;
            switch (secondType)
            {
                case 0:  fn = 1; break;   // HERT_IN
                case 1:  fn = 2; break;   // HERT_OUT
                case 2:  fn = 3; break;   // FATIGUE
                case 3:  fn = 4; break;   // NOTFIXED
                case 4:  fn = 5; break;   // FOOD
                case 6:  fn = 7; break;   // HWATA
                case 15: fn = 5; break;   // FIREWORK
                case 16: fn = 3; break;   // NOTMERGE
                case 18: fn = 10; break;  // EVENT2
                case 19: fn = 11; break;  // EXTENDED_19
                case 20: fn = 14; break;  // EXTENDED_20
                case 21: fn = 12; break;  // EXTENDED_21
                case 22: fn = 12; break;  // EXTENDED_22
                case 23: fn = 13; break;  // EXTENDED_23
                case 24: fn = 14; break;  // EXTENDED_24
                case 30: fn = 15; break;  // literal 30
                case 5: case 7: case 8: case 17:                 // EVENT / MASK / ANTI / COOLDOWN
                    fn = Pg(iconRaw) == 0 ? 6 : 9; break;
                default: fn = 8; break;   // ADDEXP/RESETPOINT/NICKNAME/INCHANTRATE/SHOUT/RECALL/25/… → w8
            }
            return $"Item_pot_w{fn}_{fn:000}.tga";
        }

        public Sprite ItemIcon(int type, int id)
        {
            if (Items == null || id <= 0) return null;
            bool weapon = type == 0 || type == 15 || type == 19;
            bool cloth  = type == 1 || type == 16 || type == 20;
            int iconRaw; string atlas;
            if (weapon && Items.TryGetWeapon(type, id, out var w))   // type 0/15/19 → đúng bảng (id trùng)
            {
                iconRaw = w.IconNo;
                atlas = w.SecondType == 0 ? WeaShort[Mathf.Clamp(w.ThirdType, 0, 7)]      // SHORT
                      : w.SecondType == 1 ? (w.ThirdType == 2 ? "Item_Wea_12.tga" : w.ThirdType == 1 ? "Item_Wea_11.tga" : null)  // LONG
                      : w.SecondType == 5 ? "Item_Wea_023.tga"                            // SPECIAL
                      : null;   // SOFT/HIDDEN/MUSICAL (2/3/4): no dedicated atlas → category art
            }
            else if (cloth && Items.TryGetCloth(id, out var cl))
            {
                char letter = cl.SecondType == 1 ? 'b' : cl.SecondType == 2 ? 'c' : cl.SecondType == 3 ? 'd'
                            : cl.SecondType == 4 ? 'e' : cl.SecondType == 7 ? 'h' : 'a';   // 7 = backpack → h
                atlas = $"Item_Clo_{Mathf.Clamp(cl.Clan, 0, 6):00}{letter}.tga";
                iconRaw = cl.IconNo;
            }
            else if (type == 9 && Items.Accessories.TryGetValue(id, out var ac))   // 0-based _XGI_FC: ACCESSORY = 9
            { iconRaw = ac.IconNo; atlas = $"Item_Acc_{Pg(iconRaw) + 1:000}.tga"; }
            else if (type == 3 && Items.Potions.TryGetValue(id, out var po))       // POTION
            { iconRaw = po.IconNo; atlas = PotionAtlas(po.SecondType, iconRaw); }   // cSecondType→fileNumber ladder (item_icons.md §3)
            else if (type == 4 && Items.Consuming.TryGetValue(id, out var co))     // CONSUMING
            { iconRaw = co.IconNo; atlas = $"Item_Con_{Pg(iconRaw) + 1:000}.tga"; }
            else if (type == 5 && Items.Elixir.TryGetValue(id, out var el))        // ELIXIR
            { iconRaw = el.IconNo; atlas = $"ITEM_ELIXIR_{Pg(iconRaw) + 1:00}.tga"; }
            else if (type == 7 && Items.Resource.TryGetValue(id, out var re))      // RESOURCE
            { iconRaw = re.IconNo; atlas = $"Item_Res_{Pg(iconRaw) + 1:000}.tga"; }
            else if (type == 8 && Items.Quest.TryGetValue(id, out var qu))         // QUEST
            { iconRaw = qu.IconNo; atlas = $"Item_que_{Pg(iconRaw) + 1:000}.tga"; }
            else return null;
            if (atlas == null || iconRaw <= 0) return null;   // sIcon_No is 1-based; 0/negative = no icon
            int icon = iconRaw - 1;                            // -> 0-based cell (item_icons.md §1)
            var tex = GetInterfaceTexture(atlas);
            if (tex == null || tex.width <= 2) return null;    // atlas missing → caller falls back
            int col = icon % 8, row = (icon / 8) % 8;
            return GetInterfaceSprite(atlas, new Rect(col * 32, row * 32, 32, 32));
        }

        /// <summary>The võ-công skill icon sprite for a skill id, resolved exactly like the client's
        /// _XWindow_QuickSlot::GetIconRect (XWindow_QuickSlot.cpp:1008): the atlas is
        /// skill_icon_&lt;clan&gt;_&lt;page&gt;.tga where clan = the skill's sect (clanType) and
        /// page = (localId-1)/64, and the cell is an 8×8 grid of 32px icons at (localId-1)%64.
        /// localId = skillId - clanType*1000. Returns null when the skill/atlas is unknown so the
        /// caller can fall back (e.g. draw the slot number only).</summary>
        public Sprite SkillIcon(short skillId)
        {
            if (Skills == null || skillId <= 0) return null;
            if (!Skills.TryGet(skillId, out var rec)) return null;
            int clan = rec.ClanType;                       // 0..6 sects, 8 = event/no-fixed
            int cell = skillId - clan * 1000 - 1;          // 0-based icon index within the clan
            if (cell < 0) return null;
            int page = cell / 64, icon = cell % 64;
            string atlas = $"skill_icon_{clan:00}_{page:00}.tga";   // case-insensitive archive lookup
            var tex = GetInterfaceTexture(atlas);
            if (tex == null || tex.width <= 2) return null;
            return GetInterfaceSprite(atlas, new Rect((icon % 8) * 32, (icon / 8) * 32, 32, 32));
        }

        /// <summary>skill_table.bsk (SCR_BASE.XP), cached — martial-arts (võ công) data table.</summary>
        public NineDragons.Data.SkillTable Skills
        {
            get
            {
                if (_skills == null && _scripts.TryExtract("skill_table.bsk", out var d))
                    _skills = NineDragons.Data.SkillTable.Load(d);
                return _skills;
            }
        }

        private NineDragons.Data.EffectTable _effects;
        private bool _effectsTried;
        /// <summary>VisualEffect.xes (Data/Script) — the particle-group effect library keyed by a skill's
        /// visualEffect id. Loose binary file; cached; null if absent or unparsable.</summary>
        public NineDragons.Data.EffectTable Effects
        {
            get
            {
                if (_effects == null && !_effectsTried)
                {
                    _effectsTried = true;
                    try
                    {
                        string p = Path.Combine(ClientData.Script, "VISUALEFFECT.XES");
                        if (File.Exists(p))
                        {
                            _effects = NineDragons.Data.EffectTable.Load(File.ReadAllBytes(p));
                            Debug.Log($"[EFFECT] VisualEffect.xes: {_effects.Count} templates, {_effects.MeshNames.Count} meshes, aligned={_effects.Aligned}");
                        }
                    }
                    catch (Exception ex) { Debug.LogWarning($"[EFFECT] load: {ex.Message}"); }
                }
                return _effects;
            }
        }

        private NineDragons.Data.NdStringDb _stringDb;
        private bool _stringDbTried;
        /// <summary>Localized string DB (Data/Script/NDTextDB_*.xsd), cached. Holds the DISPLAY
        /// strings the client reads — e.g. the Vietnamese martial-arts names (section
        /// "Skill_Table_Name", keyed by skill id) the Võ công window shows, which are NOT the CP949
        /// bytes in skill_table.bsk. A standalone file next to SCR_BASE.XP, not inside it.</summary>
        public NineDragons.Data.NdStringDb StringDb
        {
            get
            {
                if (_stringDb != null || _stringDbTried) return _stringDb;
                _stringDbTried = true;
                // the running Linux/VN client loads NDTextDB_US.xsd (converted to VN); accept the
                // usual casings/language variants and take the first that exists.
                string[] names = { "NDTextDB_US.xsd", "NDTEXTDB_US.XSD", "NDTextDB_VN.xsd",
                                   "tsNDTextDB_VN.xsd", "tsNDTextDB_US.xsd" };
                foreach (var n in names)
                {
                    string p = Path.Combine(ClientData.Script, n);
                    if (File.Exists(p))
                    {
                        try { _stringDb = NineDragons.Data.NdStringDb.Load(p, NineDragons.Data.NdStringDb.DefaultKeep); }
                        catch (Exception ex) { Debug.LogWarning($"string db '{n}': {ex.Message}"); }
                        break;
                    }
                }
                return _stringDb;
            }
        }

        /// <summary>skill id → Vietnamese display name (string-DB section "Skill_Table_Name"), or null.</summary>
        public IReadOnlyDictionary<int, string> SkillNames => StringDb?.Section("Skill_Table_Name");

        /// <summary>An interface/system string by id — the client's _XGETINTERFACETEXT(strid) =
        /// GetTextString(_XSTRINGDB_SN_EODSTRING_XSR, strid) (XStringDataBase.h:16). Localized VN text.</summary>
        public string GetInterfaceText(int strid) => StringDb?.Get("EODString_XSR", strid) ?? "";

        /// <summary>Danh hiệu (nickname title) theo title-id — NDTextDB section "NicNameRef_title" (22),
        /// native g_StringDataBase.GetTextString(_XSTRINGDB_SN_NICNAMEREF_TITLE, id). "" nếu thiếu.</summary>
        public string GetTitleText(int titleId) => StringDb?.Get("NicNameRef_title", titleId) ?? "";

        /// <summary>Tên map ĐẸP theo zone id — NDTextDB section "zonelist_short" (vd 19 → "Bamboo Village"),
        /// native zoneshortname = GetTextString(_XSTRINGDB_SN_ZONELIST_INI_SHORT, zoneid)
        /// (XProc_FirstLoad.cpp:1524). "" nếu thiếu.</summary>
        public string GetZoneName(int zoneId) => StringDb?.Get("zonelist_short", zoneId) ?? "";

        private NineDragons.Data.MonsterTable _monsters;
        private bool _monstersTried;
        /// <summary>monster_ref.bmo → per-mob-type Level/Hp/Exp table (keyed by RawType = mobID-4000).
        /// This is what feeds MonsterEntity.Enrich so the world nameplate can colour a mob by its real
        /// level gap to the player. In this build it is a LOOSE file (Data/Script/SCR_BASE/monster_ref.bmo),
        /// NOT inside SCR_BASE.XP. Cached; null if absent.</summary>
        public NineDragons.Data.MonsterTable Monsters
        {
            get
            {
                if (_monsters != null || _monstersTried) return _monsters;
                _monstersTried = true;
                // loose extraction first (that is where this build keeps it), then the archive as a fallback
                string p = Path.Combine(ClientData.Script, "SCR_BASE", "monster_ref.bmo");
                try
                {
                    if (File.Exists(p))
                        _monsters = NineDragons.Data.MonsterTable.Load(p);
                    else if (_scripts != null && _scripts.TryExtract("monster_ref.bmo", out var raw))
                        _monsters = NineDragons.Data.MonsterTable.Load(raw);
                }
                catch (Exception ex) { Debug.LogWarning($"monster_ref.bmo: {ex.Message}"); }
                if (_monsters != null) Debug.Log($"[AssetService] monster_ref.bmo: {_monsters.Count} mob types");
                return _monsters;
            }
        }

        /// <summary>mob model index (= MonsterEntity.RawType) → localized display name. This is the
        /// exact source the client's world nameplate uses: g_MonsterName[m_MobType] =
        /// g_StringDataBase.GetTextString(_XSTRINGDB_SN_MOBMODEL_XMS, i) (EmperorOfDragons.cpp:4405),
        /// section "MobModel_XMS" (section 0 of NDTextDB). Returns "" when unknown.</summary>
        public IReadOnlyDictionary<int, string> MobNames => StringDb?.Section("MobModel_XMS");

        public string MobName(int rawType)
        {
            var m = MobNames;
            return (m != null && m.TryGetValue(rawType, out var s) && !string.IsNullOrEmpty(s)) ? s : "";
        }

        /// <summary>map-NPC VN display strings from NDTextDB "NPCDialog": the client resolves them by
        /// resourceid = UniqueID*1000 + offset (XNPCScript.cpp:706) — offset +1 = name, +2 = roll
        /// (occupation). The .xns Name/Roll are Korean CP949 and NOT what the client shows; this is.
        /// Returns false when the NPC has no localized entry (→ suppress rather than show Korean).</summary>
        public bool NpcDisplay(int uniqueId, out string name, out string roll)
        {
            name = roll = null;
            var db = StringDb?.Section("NPCDialog");
            if (db == null || uniqueId <= 0) return false;
            db.TryGetValue(uniqueId * 1000 + 1, out name);
            db.TryGetValue(uniqueId * 1000 + 2, out roll);
            if (string.IsNullOrWhiteSpace(name)) name = null;
            if (string.IsNullOrWhiteSpace(roll)) roll = null;
            return name != null || roll != null;
        }

        /// <summary>품계 grade name (g_LevelName[grade]) for the HUD/char-window grade line, grade 1-20.
        /// Prefers the VN client's dedicated "LevelName" section (id = grade), falling back to the
        /// EOD interface strings where LEVELNAME_NN = id 311+grade (grade_name.md). "" if unresolved.</summary>
        public string GradeName(int grade)
        {
            if (grade <= 0) return "";
            var ln = StringDb?.Section("LevelName");
            if (ln != null && ln.TryGetValue(grade, out var s1) && !string.IsNullOrEmpty(s1)) return s1;
            var eod = StringDb?.Section("EODString_XSR");
            if (eod != null && eod.TryGetValue(311 + grade, out var s2) && !string.IsNullOrEmpty(s2)) return s2;
            return "";
        }

        private Texture2D _mobHpBack, _mobHpFill;
        /// <summary>The two isolated crops of the mob mini-HP bar from mi_party.tga (client
        /// XMob.cpp:106-108 / :99-101): back-border 108x9 clip (148,77,256,86) and blue fill 91x5
        /// clip (157,63,248,68). Returns false if mi_party.tga is not in the interface archive.</summary>
        public bool MobHpBarArt(out Texture2D back, out Texture2D fill)
        {
            back = fill = null;
            if (_mobHpBack == null)
            {
                if (GetInterfaceTexture("mi_party.tga") == MissingTexture) return false;
                var b = GetInterfaceSprite("mi_party.tga", new Rect(148, 77, 108, 9));
                var f = GetInterfaceSprite("mi_party.tga", new Rect(157, 63, 91, 5));
                _mobHpBack = b != null ? b.texture : null;
                _mobHpFill = f != null ? f.texture : null;
            }
            back = _mobHpBack; fill = _mobHpFill;
            return back != null && fill != null;
        }

        /// <summary>CharactorModel.XMS cluster tables (SCR_BASE.XP), cached.</summary>
        public CharModelTable CharModels
        {
            get
            {
                if (_charModels == null && _scripts.TryExtract("CharactorModel.XMS", out var d))
                    _charModels = CharModelTable.Parse(d);
                return _charModels;
            }
        }

        /// <summary>Equipped-cloth part mesh (X3D) for a cluster (e.g. "UPPERBODY"), or null if
        /// the item isn't a known cloth. sex 0 = male (offset 0), else female (+300).</summary>
        public string ClothClusterMesh(string cluster, int sex, int itemId)
        {
            if (itemId <= 0 || Items == null || CharModels == null) return null;
            if (!Items.TryGetCloth(itemId, out var cl)) return null;
            int off = sex == 0 ? 0 : CharModelTable.GenderSeparate;
            return CharModels.Get(cluster, off + cl.ModelIndex);
        }

        /// <summary>Hair part mesh (HAIR cluster) for a hair id — the client's
        /// g_UserModel_HairType[extravalue + hair - 1] (XBipedController.cpp:310/330). The hair id is a
        /// 1-based STYLE index into the cluster (NOT an item id); hair 0 → the gender's default hair at
        /// [extravalue]. sex 0 = male (offset 0), else female (+300). Null if the cluster has no entry.</summary>
        public string HairClusterMesh(int sex, int hairId)
        {
            if (CharModels == null) return null;
            int off = sex == 0 ? 0 : CharModelTable.GenderSeparate;
            int idx = hairId > 0 ? off + hairId - 1 : off;
            return CharModels.Get("HAIR", idx);
        }

        /// <summary>Face part mesh (FACE cluster) for a face id — the client's
        /// g_UserModel_FaceType[extravalue + face - 1] (XBipedController.cpp:321/342). Like hair, the
        /// face id is a 1-based index into the cluster; face 0 → the gender's default face at
        /// [extravalue]. sex 0 = male (offset 0), else female (+300). Null if the cluster has no entry.</summary>
        public string FaceClusterMesh(int sex, int faceId)
        {
            if (CharModels == null) return null;
            int off = sex == 0 ? 0 : CharModelTable.GenderSeparate;
            int idx = faceId > 0 ? off + faceId - 1 : off;
            return CharModels.Get("FACE", idx);
        }

        /// <summary>Held-weapon mesh (WEAPON cluster) for a weapon model index. The cluster names
        /// files like "item_wea_w1_001.X3D"; the real model is gender-prefixed ("m"/"w"). The
        /// weapon is skinned to the right-hand bone so it rides the arm animation. Null if none.</summary>
        public string WeaponMeshByModelIndex(int sex, int modelIndex)
        {
            if (modelIndex <= 0 || CharModels == null) return null;
            var name = CharModels.Get("WEAPON", modelIndex);
            if (string.IsNullOrEmpty(name)) return null;
            return (sex == 0 ? "m" : "w") + name;
        }

        /// <summary>Held-weapon mesh for an equipped weapon item id, or null.</summary>
        public string WeaponMesh(int sex, int weaponId)
        {
            if (weaponId <= 0 || Items == null) return null;
            if (!Items.TryGetWeapon(weaponId, out var w)) return null;
            return WeaponMeshByModelIndex(sex, w.ModelIndex);
        }

        /// <summary>NPC model table (NPCModel.XMS from AR_BASEANIMATION.XP), cached.</summary>
        public NpcModelTable NpcModels
        {
            get
            {
                if (_npcModels == null)
                    _npcModels = _anim.TryExtract("NPCModel.XMS", out var d)
                        ? NpcModelTable.Parse(d) : NpcModelTable.Parse(System.Array.Empty<byte>());
                return _npcModels;
            }
        }

        /// <summary>Real NPC model (bind pose) for a model index, or null.</summary>
        public MeshFactory.StaticModel GetNpcModel(int modelIndex)
        {
            var baseName = NpcModels.BaseModelFor(modelIndex);
            return string.IsNullOrEmpty(baseName) ? null : GetStaticModel(baseName);
        }

        private readonly Dictionary<string, XaAnimation> _anims =
            new Dictionary<string, XaAnimation>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Parsed .XA animation from AR_BASEANIMATION.XP (or MR_BASEMODEL); cached; null if absent.</summary>
        public XaAnimation GetAnimation(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_anims.TryGetValue(name, out var cached)) return cached;
            XaAnimation a = null;
            // loose overlay (Data\Model\AR_BASEANIMATION\ etc.) first, then AR_BASEANIMATION.XP, then MR_BASEMODEL.XP
            byte[] raw = ModelBytes(name, _anim);
            if (raw == null && _models.TryExtract(name, out var d2)) raw = d2;
            if (raw != null)
            {
                try { a = XaAnimation.Parse(raw, name); }
                catch (NineDragons.Assets.InvalidDataException ex) { Debug.LogWarning($"anim '{name}': {ex.Message}"); }
            }
            _anims[name] = a;
            return a;
        }

        /// <summary>Raw bytes of a sound entry (.ogg/.wav) from SR_SOUND.XP; null if absent.</summary>
        public byte[] GetSound(string name)
            => _sound != null && _sound.TryExtract(name, out var d) ? d : null;

        /// <summary>True if SR_SOUND.XP contains this entry (directory check, no extraction). Lets the
        /// BGM manager fall back to bgm2 when a map's bgm1 .ogg is missing from this client build.</summary>
        public bool HasSound(string name)
            => _sound != null && !string.IsNullOrEmpty(name) && _sound.Contains(name);

        /// <summary>Mob model table (MobModel.XMS from SCR_BASE.XP; same _XM_MODELINDEX/_XM_BASEMODEL form).</summary>
        public NpcModelTable MobModels
        {
            get
            {
                if (_mobModels == null)
                    _mobModels = _scripts.TryExtract("MobModel.XMS", out var d)
                        ? NpcModelTable.Parse(d) : NpcModelTable.Parse(System.Array.Empty<byte>());
                return _mobModels;
            }
        }

        private MoveVillageTable _moveVillages;

        /// <summary>Move Village ticket destinations (Move_Village.xms in SCR_BASE.XP) — the key-E teleport
        /// list. Cached; empty when the file is absent.</summary>
        public MoveVillageTable MoveVillages
        {
            get
            {
                if (_moveVillages == null)
                    _moveVillages = _scripts.TryExtract("Move_Village.xms", out var d)
                        ? MoveVillageTable.Parse(d) : MoveVillageTable.Parse(System.Array.Empty<byte>());
                return _moveVillages;
            }
        }

        private RefineEffect.ClothTable _clothEffects;

        /// <summary>Cloth refine-glow table (Cloth_Effect.txt in SCR_BASE.XP) — attr/level/part → up to
        /// 3 VISUALEFFECT.XES ids, feeding the cloth aura (RefineEffect.GetClothEffectIds). Cached;
        /// empty (no glow) when the file is absent.</summary>
        public RefineEffect.ClothTable ClothEffects
        {
            get
            {
                if (_clothEffects == null)
                    _clothEffects = _scripts.TryExtract("Cloth_Effect.txt", out var d)
                        ? RefineEffect.ClothTable.Parse(d) : RefineEffect.ClothTable.Parse(System.Array.Empty<byte>());
                return _clothEffects;
            }
        }

        /// <summary>Real monster model (bind pose) for a raw mob type, or null.</summary>
        public MeshFactory.StaticModel GetMobModel(int rawType)
        {
            var baseName = MobModels.BaseModelFor(rawType);
            return string.IsNullOrEmpty(baseName) ? null : GetStaticModel(baseName);
        }

        private CharBipedTable _maleBiped, _femaleBiped;

        /// <summary>The player character's motion stack (MaleBiped.xms / FemaleBiped.xms from
        /// SCR_BASE.XP), motion index → .xa clip name. Cached per sex (0 = man).</summary>
        public CharBipedTable CharBiped(int sex)
        {
            if (sex == 0)
                return _maleBiped ??= (_scripts.TryExtract("MaleBiped.xms", out var m)
                    ? CharBipedTable.Parse(m) : CharBipedTable.Parse(System.Array.Empty<byte>()));
            return _femaleBiped ??= (_scripts.TryExtract("FemaleBiped.xms", out var w)
                ? CharBipedTable.Parse(w) : CharBipedTable.Parse(System.Array.Empty<byte>()));
        }

        /// <summary>Skinned + idle-animated NPC GameObject for a model index, or null.</summary>
        public GameObject BuildAnimatedNpc(int modelIndex, string name)
        {
            var e = NpcModels.Get(modelIndex);
            if (e == null)
            { if (SkinnedCharacterFactory.LogAnim) UnityEngine.Debug.Log($"[ANIMFAIL] npc {name}: modelIndex {modelIndex} not in NPCModel.XMS"); return null; }
            return SkinnedCharacterFactory.BuildAnimatedEntity(this, e.BaseModel, e.Biped, e.IdleAnim, e.WalkAnim, name, e.ActionAnim);
        }

        /// <summary>Skinned + idle-animated monster GameObject for a raw mob type, or null.</summary>
        public GameObject BuildAnimatedMob(int rawType, string name)
        {
            var e = MobModels.Get(rawType);
            if (e == null)
            { if (SkinnedCharacterFactory.LogAnim) UnityEngine.Debug.Log($"[ANIMFAIL] mob {name}: rawType {rawType} not in MobModel.XMS"); return null; }
            // Pass WALK and RUN as SEPARATE clips (MobModel.XMS has both, ~4086 walk / ~4001 run of
            // 4668 entries) so the animator can play the one that matches the server move mode instead
            // of always the walk clip — the client picks walk↔run from ucMobWalkMode (XMob.cpp:1737-1771),
            // each paired with its own move speed. WalkAnim/RunAnim may be empty for a one-gait mob; the
            // animator's _walk??_run / _run??_walk fallback covers that (same intent as the old MoveAnim).
            return SkinnedCharacterFactory.BuildAnimatedEntity(this, e.BaseModel, e.Biped,
                e.IdleAnim, e.WalkAnim, name, e.ActionAnim, e.RunAnim);
        }

        // ---------------------------------------------------------------- maps

        public XtvTerrain LoadTerrain(string mapName)
        {
            var data = _environment.Extract(mapName + ".xtv");
            return XtvTerrain.Parse(data, mapName + ".xtv");
        }

        public XnsNpcList LoadNpcs(string mapName)
        {
            return _environment.TryExtract(mapName + ".XNS", out var data)
                ? XnsNpcList.Parse(data, mapName + ".XNS") : null;
        }

        public XziAreaList LoadAreas(string mapName)
        {
            return _environment.TryExtract(mapName + ".XZI", out var data)
                ? XziAreaList.Parse(data, mapName + ".XZI") : null;
        }

        // -------------------------------------------------------------- models

        // ---- loose-file overlay -------------------------------------------------------------
        // Read extracted models/skeletons/animations straight from Data\Model\** (including the
        // MR_BASEMODEL\ and AR_BASEANIMATION\ subfolders the user extracts into) BEFORE the .XP, so
        // dropping a model the packaged archive lacks (e.g. the missing mitem_wea_w13_016.X3D or any
        // shoes/body/weapon mesh) makes the client pick it up with NO repack. filename (no path,
        // case-insensitive) -> full path; deeper/last-seen wins so a loose copy overrides the archive.
        private Dictionary<string, string> _looseModelIndex;
        private int _looseHitLog;
        /// <summary>When true, log each loose model/anim hit (wired from -ndLogParts/-ndLogLoose).</summary>
        public static bool LogLoose;

        private Dictionary<string, string> LooseModelIndex
        {
            get
            {
                if (_looseModelIndex != null) return _looseModelIndex;
                _looseModelIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string dir = ClientData.Model;   // Data\Model  (+ MR_BASEMODEL\, AR_BASEANIMATION\ under it)
                    if (Directory.Exists(dir))
                        foreach (var path in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
                        {
                            string ext = Path.GetExtension(path);
                            if (ext.Length == 0 || ext.Equals(".xp", StringComparison.OrdinalIgnoreCase)) continue;
                            _looseModelIndex[Path.GetFileName(path)] = path;   // last-wins
                        }
                    Debug.Log($"[LOOSE] Data\\Model overlay: indexed {_looseModelIndex.Count} loose files under {dir}");
                }
                catch (Exception ex) { Debug.LogWarning($"[LOOSE] index build: {ex.Message}"); }
                return _looseModelIndex;
            }
        }

        /// <summary>Raw bytes for a model/skeleton/animation file, LOOSE overlay first
        /// (Data\Model\**), else the given .XP archive; null when neither has it.</summary>
        private byte[] ModelBytes(string name, XpArchive archiveFallback)
        {
            if (LooseModelIndex.TryGetValue(name, out var path))
            {
                try
                {
                    var b = File.ReadAllBytes(path);
                    if (LogLoose && _looseHitLog < 4000) { Debug.Log($"[LOOSE] '{name}' <- {path}"); _looseHitLog++; }
                    return b;
                }
                catch (Exception ex) { Debug.LogWarning($"[LOOSE] read '{name}': {ex.Message}"); }
            }
            return (archiveFallback != null && archiveFallback.TryExtract(name, out var d)) ? d : null;
        }

        /// <summary>Parsed X3D/XBP model (mesh + skin + skeleton) by file name; cached; null if absent.
        /// Loads the loose Data\Model overlay first, then MR_BASEMODEL.XP.</summary>
        public X3dModel GetRawModel(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            if (_rawModels.TryGetValue(fileName, out var cached)) return cached;
            X3dModel m = null;
            var raw = ModelBytes(fileName, _models);
            if (raw != null)
            {
                try { m = X3dModel.Parse(raw, fileName); }
                catch (NineDragons.Assets.InvalidDataException ex) { Debug.LogWarning($"raw model '{fileName}': {ex.Message}"); }
            }
            _rawModels[fileName] = m;
            return m;
        }

        /// <summary>Static world model by file name (e.g. "obj_hapbi_01.x3d"); null if absent.
        /// Loose Data\Model overlay first, then MR_BASEMODEL.XP.</summary>
        public MeshFactory.StaticModel GetStaticModel(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            if (_staticModels.TryGetValue(fileName, out var cached)) return cached;

            MeshFactory.StaticModel built = null;
            // glTF overlay: a converted Data\Model\gltf\<name>.glb wins over the .x3d (Blender-editable;
            // one submesh per material, textures resolved by name). This is the path GrassFactory uses.
            try
            {
                var gbytes = GlbBytes(fileName);
                if (gbytes != null)
                {
                    built = MeshFactory.BuildStaticGlb(gbytes, fileName);
                    if (built != null && LogLoose && _looseHitLog < 4000) { Debug.Log($"[GLTF-STATIC] '{fileName}' (glb)"); _looseHitLog++; }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[GLTF] static '{fileName}': {ex.Message}"); }

            if (built == null)
            {
                var raw = ModelBytes(fileName, _models);
                if (raw != null)
                {
                    try
                    {
                        built = MeshFactory.BuildStatic(X3dModel.Parse(raw, fileName), fileName);
                    }
                    catch (NineDragons.Assets.InvalidDataException ex)
                    {
                        Debug.LogWarning($"model '{fileName}': {ex.Message}");
                    }
                }
            }
            _staticModels[fileName] = built;   // cache misses too
            return built;
        }

        // ---- glTF overlay: a prebuilt Data\Model\gltf\<name>.glb WINS over the .x3d ----------------
        // Self-contained (mesh + embedded textures, RH->Unity handled by GlbLoader). One INACTIVE
        // template GameObject is built per model and cached; callers Instantiate it per placement so
        // the mesh/materials are shared across every copy of that object in the map.
        private readonly Dictionary<string, GameObject> _glbTemplates =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private Transform _glbHolder;
        private XpArchive _gltfPack;   // pack .glb nén tuỳ chọn (9DModel.<đuôi>, đuôi chưa chốt)
        internal const string GltfDirName = "9DModel";   // folder .glb (đổi từ "gltf" 09/09); pack nén = 9DModel.<đuôi>

        /// <summary>Bytes of a converted model's .glb: loose <c>Data\Model\gltf\&lt;base&gt;.glb</c> first
        /// (a dropped file overrides — the Blender-edit workflow), else the MR_GLTF.XP pack; null if
        /// neither has it. One choke point for every glb load (static objects, grass, skinned parts).</summary>
        public byte[] GlbBytes(string modelFileName)
        {
            if (string.IsNullOrEmpty(modelFileName)) return null;
            string baseName = Path.GetFileNameWithoutExtension(modelFileName);
            try
            {
                string loose = Path.Combine(ClientData.Model, GltfDirName, baseName + ".glb");
                if (File.Exists(loose)) return File.ReadAllBytes(loose);
            }
            catch { }
            return (_gltfPack != null && _gltfPack.TryExtract(baseName + ".glb", out var d)) ? d : null;
        }

        /// <summary>Cheap presence test for a MOUNTABLE mesh: true if a converted <c>.glb</c> (loose
        /// 9DModel\ overlay or pack) OR the raw <c>.X3D</c> (MR_BASEMODEL.XP / loose model overlay)
        /// exists for this file name. This build ships glb-only converted models, so a weapon whose
        /// <c>.X3D</c> is absent from MR_BASEMODEL.XP still HAS a mountable <c>.glb</c> — the old
        /// GetRawModel()-only check reported such weapons "absent" and left the hand empty (chủ 09/09).
        /// Windows file lookup is case-insensitive, so "mitem_..." resolves the on-disk "Mitem_...".</summary>
        public bool HasModel(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            try
            {
                string loose = Path.Combine(ClientData.Model, GltfDirName, baseName + ".glb");
                if (File.Exists(loose)) return true;
            }
            catch { }
            if (_gltfPack != null && _gltfPack.Contains(baseName + ".glb")) return true;
            return _models != null && _models.Contains(fileName);
        }

        /// <summary>Bytes of a converted animation .glb for a clip name: loose
        /// <c>Data\Model\gltf_anim\&lt;clip&gt;.glb</c> (edited in Blender) — the modern animation edit
        /// path; null if none (caller falls back to the .XA). Clip name may carry a .xa extension.</summary>
        public byte[] GlbAnimBytes(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return null;
            try
            {
                string b = Path.GetFileNameWithoutExtension(clipName);
                string p = Path.Combine(ClientData.Model, "gltf_anim", b + ".glb");
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
            catch { }
            return null;
        }

        /// <summary>Inactive template built from Data\Model\gltf\&lt;name&gt;.glb, or null if none.</summary>
        public GameObject GetObjectGlbTemplate(string meshFile)
        {
            if (string.IsNullOrEmpty(meshFile)) return null;
            if (_glbTemplates.TryGetValue(meshFile, out var cached)) return cached;

            GameObject tmpl = null;
            try
            {
                string baseName = Path.GetFileNameWithoutExtension(meshFile);
                var bytes = GlbBytes(meshFile);
                if (bytes != null)
                {
                    if (_glbHolder == null)
                    {
                        var holder = new GameObject("~glbTemplates"); holder.SetActive(false);
                        UnityEngine.Object.DontDestroyOnLoad(holder);
                        _glbHolder = holder.transform;
                    }
                    tmpl = GlbLoader.Load(bytes, baseName, _objectShader, GetObjectMaterial);   // texture by name from TR_*.XP
                    tmpl.transform.SetParent(_glbHolder, false);
                    if (LogLoose && _looseHitLog < 4000) { Debug.Log($"[GLTF] '{meshFile}' (glb)"); _looseHitLog++; }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[GLTF] template '{meshFile}': {ex.Message}"); tmpl = null; }
            _glbTemplates[meshFile] = tmpl;   // cache misses too
            return tmpl;
        }

        // ------------------------------------------------------------ textures

        public Texture2D GetTexture(string name)
        {
            if (string.IsNullOrEmpty(name)) return MissingTexture;
            if (_textures.TryGetValue(name, out var cached)) return cached;

            Texture2D tex = null;
            foreach (var pack in _texturePacks)
            {
                if (!pack.TryExtract(name, out var raw)) continue;
                try { tex = TextureFactory.FromBytes(raw, name); }
                catch (Exception ex) { Debug.LogWarning($"texture '{name}': {ex.Message}"); }
                break;
            }
            if (tex == null) tex = MissingTexture;
            _textures[name] = tex;
            return tex;
        }

        /// <summary>Decoded HUD/window texture from TR_MAININTERFACE.XP, oriented UPRIGHT for uGUI
        /// (the raw decode is top-down like DDS; rows are flipped here so sprites read normally).
        /// Cached. Mirrors the client's g_MainInterfaceTextureArchive.GetTexture.</summary>
        public Texture2D GetInterfaceTexture(string name)
        {
            if (_interface == null || string.IsNullOrEmpty(name)) return MissingTexture;
            if (_ifaceTex.TryGetValue(name, out var cached)) return cached;
            Texture2D tex = null;
            if (_interface.TryExtract(name, out var raw))
            {
                try
                {
                    var nd = NineDragons.Assets.NdTexture.Load(raw, name);
                    var rgba = nd.ToRgba32();                       // top-down RGBA
                    int w = nd.Width, h = nd.Height, stride = w * 4;
                    var flip = new byte[rgba.Length];
                    for (int y = 0; y < h; y++)
                        Array.Copy(rgba, y * stride, flip, (h - 1 - y) * stride, stride);
                    tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp };
                    tex.LoadRawTextureData(flip);
                    tex.Apply(false, false);   // keep readable so GetInterfaceSprite can copy sub-regions out
                }
                catch (Exception ex) { Debug.LogWarning($"iface tex '{name}': {ex.Message}"); }
            }
            if (tex == null) tex = MissingTexture;
            _ifaceTex[name] = tex;
            return tex;
        }

        /// <summary>uGUI sprite for a sub-rect of an interface atlas. `topDownRect` uses the atlas's
        /// natural top-left origin (matching the .tga preview). The region is COPIED into its own
        /// isolated texture so the sprite can only ever show that region — never a neighbouring
        /// atlas element bleeding in.</summary>
        public Sprite GetInterfaceSprite(string name, Rect topDownRect)
            => GetInterfaceSprite(name, topDownRect, default);

        /// <summary>Same as above but with a 9-slice <paramref name="border"/> (L,B,R,T in the crop's
        /// pixels) so the sprite can be drawn with Image.Type.Sliced and keep crisp carved corners.</summary>
        public Sprite GetInterfaceSprite(string name, Rect topDownRect, Vector4 border)
        {
            string key = $"{name}|{topDownRect.x},{topDownRect.y},{topDownRect.width},{topDownRect.height}"
                       + (border == default ? "" : $"|b{border.x},{border.y},{border.z},{border.w}");
            if (_ifaceSprites.TryGetValue(key, out var s)) return s;
            var full = GetInterfaceTexture(name);
            int w = Mathf.Clamp((int)topDownRect.width, 1, full.width);
            int h = Mathf.Clamp((int)topDownRect.height, 1, full.height);
            int x = Mathf.Clamp((int)topDownRect.x, 0, full.width - w);
            int y = Mathf.Clamp(full.height - (int)topDownRect.y - h, 0, full.height - h);
            var crop = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name + "#crop", wrapMode = TextureWrapMode.Clamp };
            try { crop.SetPixels(full.GetPixels(x, y, w, h)); crop.Apply(false, true); }
            catch (Exception ex) { Debug.LogWarning($"iface crop '{name}': {ex.Message}"); }
            var sp = Sprite.Create(crop, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            sp.name = "iface:" + name;
            _ifaceSprites[key] = sp;
            return sp;
        }

        /// <summary>Shared cutout material per texture name (world objects).</summary>
        public Material GetObjectMaterial(string textureName)
        {
            string key = textureName ?? "";
            if (_materials.TryGetValue(key, out var m)) return m;
            m = new Material(_objectShader) { name = "obj_" + key };
            m.mainTexture = HdTexture(key);   // HD: upscale + sharpen env/building textures
            _materials[key] = m;
            return m;
        }

        /// <summary>Shared OPAQUE material per texture name (character skin/cloth). Body meshes are
        /// opaque in the original client, but many skin/cloth textures are DDS-DXT5 / 32-bit TGA
        /// whose alpha channel is unused (0). The world cutout shader would clip(a-0.5) those to
        /// nothing — dropping hands, legs, and sleeveless-armor arms. _Cutoff=0 keeps every texel
        /// (clip only where a&lt;0, i.e. never) so the whole limb renders.</summary>
        public Material GetCharacterMaterial(string textureName)
        {
            string key = "chr:" + (textureName ?? "");
            if (_materials.TryGetValue(key, out var m)) return m;
            // NineDragons/Character = per-pixel lit + optional normal map (surface depth = the "HD" look).
            // A generated normal map (from the diffuse) gives cloth/skin real relief under the scene light.
            m = new Material(NdShaders.Character) { name = "chr_" + (textureName ?? "") };
            var tex = HdTexture(textureName ?? "");       // upscaled + sharpened diffuse (HD look)
            m.mainTexture = tex;
            m.SetFloat("_Cutoff", 0f);
            var nrm = GenerateNormalMap(textureName);
            if (nrm != null) { m.SetTexture("_BumpMap", nrm); m.SetFloat("_NormalStrength", 1.4f); }
            _materials[key] = m;
            return m;
        }

        /// <summary>Character material from an EMBEDDED glb texture (a model authored + textured in
        /// Blender whose texture isn't in TR_*.XP). Same shader as <see cref="GetCharacterMaterial"/>,
        /// opaque, no pack lookup. Cached by <paramref name="cacheKey"/>.</summary>
        public Material GetCharacterMaterialFromTexture(Texture2D tex, string cacheKey)
        {
            string key = "chrtex:" + (cacheKey ?? "");
            if (_materials.TryGetValue(key, out var m)) return m;
            m = new Material(NdShaders.Character) { name = "chrtex_" + (cacheKey ?? "") };
            m.mainTexture = tex != null ? tex : MissingTexture;
            m.SetFloat("_Cutoff", 0f);
            _materials[key] = m;
            return m;
        }

        private readonly Dictionary<string, Texture2D> _hdTex = new Dictionary<string, Texture2D>();
        private Material _sharpenMat;

        /// <summary>Upscaled (2x) + unsharp-masked copy of a texture for the "HD" character/cloth look:
        /// the 256px skin/cloth source stays soft under bilinear scaling, so a straight blit through
        /// NineDragons/Sharpen restores crisp edges (orientation preserved). Cached; falls back to the
        /// plain texture if the GPU readback is unavailable (headless).</summary>
        public Texture2D HdTexture(string name)
        {
            if (string.IsNullOrEmpty(name)) return MissingTexture;
            if (_hdTex.TryGetValue(name, out var cached)) return cached;
            var src = GetTexture(name);
            Texture2D outTex = src;
            try
            {
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null
                    && src != null && src != MissingTexture && src.width >= 8 && src.width <= 1024)
                {
                    if (_sharpenMat == null)
                    {
                        _sharpenMat = new Material(NdShaders.Sharpen);
                        _sharpenMat.SetFloat("_Amount", 0.85f);
                        _sharpenMat.SetFloat("_Blur", 1.0f);
                    }
                    int W = src.width * 2, H = src.height * 2;
                    var rt = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(src, rt, _sharpenMat);
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    var t = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = name + "#hd", wrapMode = src.wrapMode };
                    t.ReadPixels(new Rect(0, 0, W, H), 0, 0); t.Apply(true, false);
                    RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                    outTex = t;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"HD tex '{name}': {ex.Message}"); outTex = src; }
            _hdTex[name] = outTex;
            return outTex;
        }

        private readonly Dictionary<string, Texture2D> _normalMaps = new Dictionary<string, Texture2D>();

        /// <summary>Runtime normal map from a diffuse texture (HIGH-PASS luminance → Sobel), so cloth/skin
        /// gets surface relief under the scene light. Plain-RGB encoding (shader decodes xyz*2-1).</summary>
        private Texture2D GenerateNormalMap(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_normalMaps.TryGetValue(name, out var cached)) return cached;
            Texture2D result = null;
            try
            {
                byte[] raw = null;
                foreach (var pack in _texturePacks) if (pack.TryExtract(name, out raw)) break;
                if (raw != null)
                {
                    var nd = NineDragons.Assets.NdTexture.Load(raw, name);
                    result = BuildNormal(nd.ToRgba32(), nd.Width, nd.Height);
                }
            }
            catch (Exception ex) { Debug.LogWarning($"normal '{name}': {ex.Message}"); }
            _normalMaps[name] = result;
            return result;
        }

        private static Texture2D BuildNormal(byte[] rgba, int w, int h)
        {
            if (rgba == null || rgba.Length < w * h * 4 || w < 4 || h < 4) return null;
            var lum = new float[w * h];
            for (int p = 0; p < w * h; p++) { int b = p * 4; lum[p] = 0.299f * rgba[b] + 0.587f * rgba[b + 1] + 0.114f * rgba[b + 2]; }
            int r = Mathf.Max(2, w / 48);
            var blur = BoxBlur(lum, w, h, r);
            var detail = new float[w * h];
            for (int p = 0; p < w * h; p++) detail[p] = lum[p] - blur[p];   // fine surface detail only
            const float S = 0.012f;
            var pix = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int xm = x > 0 ? x - 1 : x, xp = x < w - 1 ? x + 1 : x;
                    int ym = y > 0 ? y - 1 : y, yp = y < h - 1 ? y + 1 : y;
                    float dx = detail[y * w + xp] - detail[y * w + xm];
                    float dy = detail[yp * w + x] - detail[ym * w + x];
                    float nx = -dx * S, ny = -dy * S, nz = 1f;
                    float inv = 1f / Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                    nx *= inv; ny *= inv; nz *= inv;
                    pix[y * w + x] = new Color32((byte)((nx * 0.5f + 0.5f) * 255f), (byte)((ny * 0.5f + 0.5f) * 255f), (byte)((nz * 0.5f + 0.5f) * 255f), 255);
                }
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true, true) { name = "nrm", wrapMode = TextureWrapMode.Repeat };
            t.SetPixels32(pix); t.Apply(true, true);
            return t;
        }

        private static float[] BoxBlur(float[] src, int w, int h, int r)
        {
            var tmp = new float[w * h]; var dst = new float[w * h]; float inv = 1f / (2 * r + 1);
            for (int y = 0; y < h; y++) { float s = 0; for (int k = -r; k <= r; k++) s += src[y * w + Mathf.Clamp(k, 0, w - 1)];
                for (int x = 0; x < w; x++) { tmp[y * w + x] = s * inv; int add = Mathf.Clamp(x + r + 1, 0, w - 1), sub = Mathf.Clamp(x - r, 0, w - 1); s += src[y * w + add] - src[y * w + sub]; } }
            for (int x = 0; x < w; x++) { float s = 0; for (int k = -r; k <= r; k++) s += tmp[Mathf.Clamp(k, 0, h - 1) * w + x];
                for (int y = 0; y < h; y++) { dst[y * w + x] = s * inv; int add = Mathf.Clamp(y + r + 1, 0, h - 1), sub = Mathf.Clamp(y - r, 0, h - 1); s += tmp[add * w + x] - tmp[sub * w + x]; } }
            return dst;
        }

        public Texture2D MissingTexture
        {
            get
            {
                if (_missingTexture == null)
                {
                    _missingTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                    { name = "missing" };
                    var px = new Color32[] { new Color32(255, 0, 255, 255), new Color32(40, 0, 40, 255),
                                             new Color32(40, 0, 40, 255), new Color32(255, 0, 255, 255) };
                    _missingTexture.SetPixels32(px);
                    _missingTexture.Apply();
                }
                return _missingTexture;
            }
        }

        public void Dispose()
        {
            _models?.Dispose();
            _environment?.Dispose();
            _scripts?.Dispose();
            _anim?.Dispose();
            _sound?.Dispose();
            foreach (var p in _texturePacks) p.Dispose();
            _texturePacks.Clear();
        }
    }
}
