// ============================================================================
//  GameSession.cs - M5 public interface for the Unity layer (M6/M7).
//
//  Drives the full flow (login -> GS -> select -> ENTER WORLD) and then runs a
//  receive loop that parses PLAIN server frames (M2 packets) into a live
//  WorldState, raising events the UI can bind to. Movement is sent as ZONE_MOVE.
//
//  Threading: single-threaded and pull-based. Call Pump(ms) from your game loop
//  (or the harness) to drain and dispatch whatever the server has sent. No
//  background threads, so Unity can call it from the main thread.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using Nd.Net;
using Nd.Packets;
using NineDragons.Data;

namespace Nd.Session
{
    public sealed class GameSession : IDisposable
    {
        private readonly NdWorldConnection _conn;
        private readonly MonsterTable _mobs;   // optional (monster_ref.bmo) enrichment

        public readonly WorldState World = new WorldState();
        public MyCharacter Me => World.Me;
        public IReadOnlyDictionary<ushort, MonsterEntity> Monsters => World.Monsters;
        public IReadOnlyDictionary<ushort, PlayerEntity>  Players  => World.Players;

        public bool InWorld { get; private set; }
        public Action<string> Log = _ => { };

        // ---- events for the eventual UI layer ----
        public event Action<MyCharacter>   MyStateChanged;
        public event Action<MonsterEntity> MonsterSeen;    // MONSTER_EXIST (first time)
        public event Action<MonsterEntity> MonsterMoved;   // MONSTER_MOVE / vital update
        public event Action<ushort>        MonsterDied;    // MONSTER_VITAL < 0
        public event Action<ushort, int>   MonsterDamaged; // MARTIAL_RESULT (mobId, total damage)
        public event Action<ushort, int>   MonsterAttacked; // B_ATKRES_FROM_MOB (50): (mobId, atkType) — mob swings at me
        public event Action<ushort, int>   MonsterSkill;    // B_ATKRES_FROM_MOB (50) cPassiveTitle>0: (mobId, skillId) — mob casts a skill
        private const int MobSkillSuicide = 20;             // _XMOB_SKILL_SUICIDE (XMob.h:97): cPassiveTitle → mob dies
        public event Action<int>           LevelChanged;   // LEVEL_UP_RESULT (106): new inner level
        public event Action                AttackSent;     // player fired a normal melee attack (swing SFX)
        public event Action                SkillCast;      // player fired a martial skill (cast SFX)
        public ushort                      LastSkillTargetId;   // target of the most recent SendSkill (VFX routing)
        public event Action                ItemPickedUp;   // inventory item count rose (pickup SFX)
        public event Action                MoneyGained;    // money rose (coin SFX)
        private bool _invenSynced;         // first InvenInfo is the login snapshot (no pickup SFX)
        public int CombatResults => _combatResults;
        private int _combatResults;
        public event Action<PlayerEntity>  PlayerMoved;    // ZONE_MOVE broadcast
        public event Action<int, byte[]>   UnknownPacket;  // ucMessage id + raw frame
        public event Action<int, string>   ChatReceived;   // ZONE_CHAT (28): (colorIndex, "name: text")

        // ---- skill / martial-arts (võ công) system ----
        /// <summary>One learned skill slot (from MSG_SKILL_INFO 96 / MSG_SKILL_CTRL 98). The list
        /// index IS the ucLearnSkillIndex the fire/step-up packets carry.</summary>
        public sealed class LearnedSkill
        {
            public int Slot;      // index in LearnedSkills == ucLearnSkillIndex
            public int Id;        // skill id (key into SkillTable)
            public int Step;      // current level (급), 0-based
            public int Exp;
            public int State;     // 0 = complete, 1 = training
        }
        public readonly List<LearnedSkill> LearnedSkills = new List<LearnedSkill>();
        /// <summary>Optional skill_table.bsk data — lets SendSkill pick the right martial packet
        /// per usefulType and lets the UI show names/levels. Set by the Unity layer.</summary>
        public NineDragons.Data.SkillTable Skills;
        /// <summary>The skill currently slotted for combat (fired instead of the plain attack).
        /// -1 = none (normal attack).</summary>
        public int SelectedSkillSlot = -1;
        /// <summary>Player's sect / môn phái (CharacListEntry.Group) — for table-level school gating.
        /// Set by the Unity layer after character select (CHARAC_INFO 16 carries no sect field).</summary>
        public int MySect;
        public event Action SkillsChanged;   // learned-skill list updated (learn/level/delete)
        public Action<string> SkillLog = _ => { };  // flag-guarded diagnostics sink (UI sets it)

        // ---- diagnostics: per-id frame counters (all packets seen) ----
        public readonly Dictionary<int, int> PacketCounts = new Dictionary<int, int>();

        public GameSession(string lumpPath, MonsterTable mobs = null)
        {
            _conn = new NdWorldConnection(lumpPath) { Log = s => Log("net: " + s) };
            _mobs = mobs;
        }

        /// <summary>Wire learn-slot (ucLearnSkillIndex) for a skill id, or -1 (mirrors
        /// _XLocalUser::GetLearnedSkillIndex — returns the server slot, not the list position).</summary>
        public int GetLearnedSkillIndex(int skillId)
        {
            for (int i = 0; i < LearnedSkills.Count; i++)
                if (LearnedSkills[i].Id == skillId) return LearnedSkills[i].Slot;
            return -1;
        }

        public LearnedSkill SelectedSkill =>
            (SelectedSkillSlot >= 0 && SelectedSkillSlot < LearnedSkills.Count) ? LearnedSkills[SelectedSkillSlot] : null;

        /// <summary>The learned khinh công (light-body / HyperRun) skill, or null. The client only lets a
        /// character use the fast KYUNGGONG run when it has learned such a skill (stype 3); with none, ALT
        /// -move is a normal run. Cheap linear scan over the (small) learned list.</summary>
        public LearnedSkill HyperRunSkill
        {
            get
            {
                if (Skills == null) return null;
                for (int i = 0; i < LearnedSkills.Count; i++)
                    if (LearnedSkills[i].Id > 0 && Skills.TryGet(LearnedSkills[i].Id, out var r) && r.IsHyperRun)
                        return LearnedSkills[i];
                return null;
            }
        }
        public bool HasHyperRun => HyperRunSkill != null;

        /// <summary>The learned vận khí (Dưỡng Sinh / self-recovery) skill, or null — stype 2. Drives the
        /// SELF_CHANNEL sit-and-recover.</summary>
        public LearnedSkill VanKhiSkill
        {
            get
            {
                if (Skills == null) return null;
                for (int i = 0; i < LearnedSkills.Count; i++)
                    if (LearnedSkills[i].Id > 0 && Skills.TryGet(LearnedSkills[i].Id, out var r) && r.IsVanKhi)
                        return LearnedSkills[i];
                return null;
            }
        }
        public bool HasVanKhi => VanKhiSkill != null;

        // ---- vận khí self-channel (SELF_CHANNEL 64) — the network layer; a HUD slot / key calls these ----
        public const int VanKhiStart = 0, VanKhiStop = 2;   // MSG_SELF_CHANNEL_START_REQUEST cMode
        /// <summary>Begin the vận khí recovery channel (sit + recover HP/mana). The server drives the
        /// recovery and replies SELF_CHANNEL_CTRL; moving/being hit stops it (call <see cref="StopVanKhi"/>).</summary>
        public bool StartVanKhi()
        {
            var ls = VanKhiSkill;
            if (ls == null) return false;
            _conn.Send(new Nd.Packets.MsgSelfChannelStartReq
            { Mode = (sbyte)VanKhiStart, LearnSkillIndex = (byte)ls.Slot, SkillId = (short)ls.Id });
            SkillLog($"vankhi: START id={ls.Id} slot={ls.Slot}");
            return true;
        }
        /// <summary>Stop the vận khí channel (moved / attacked / cancelled).</summary>
        public void StopVanKhi()
        {
            var ls = VanKhiSkill;
            _conn.Send(new Nd.Packets.MsgSelfChannelStartReq
            { Mode = (sbyte)VanKhiStop, LearnSkillIndex = ls != null ? (byte)ls.Slot : (byte)0, SkillId = ls != null ? (short)ls.Id : (short)0 });
        }

        // ------------------------------------------------ connect + select ----
        public LoginSuccess Connect(string host, int loginPort, int gsPort, string id, string pw)
        {
            var ok = _conn.Login(host, loginPort, id, pw);
            if (!_conn.EnterGameServer(host, gsPort, id, ok.Uid))
                throw new InvalidOperationException("GS ENTER_RESULT rejected");
            return ok;
        }

        public CharacList RequestCharacterList() => _conn.RequestCharacterList();

        public CharacInfo SelectCharacter(string name)
        {
            var ci = _conn.SelectCharacter(name, raw => Dispatch(raw));
            Me.ApplyCharacInfo(ci, name, _conn.Uid);
            MyStateChanged?.Invoke(Me);
            return ci;
        }

        /// <summary>Send GAMESTARTSIGNAL (20). The server answers with a MONSTER_EXIST
        /// burst + inventory; call Pump() to drain it.</summary>
        public void EnterWorld()
        {
            _conn.SendGameStartSignal();
            InWorld = true;
        }

        /// <summary>Re-send GAMESTARTSIGNAL after a vé-di-chuyển reload finishes loading the new map, so
        /// nd-world spawns the new zone's monsters (the seamless-teleport counterpart of EnterWorld —
        /// the connection is already in-world, so InWorld is left as-is).</summary>
        public void SendGameStart() => _conn.SendGameStartSignal();

        // ------------------------------------------------ receive loop --------
        /// <summary>Drain and dispatch every frame the server sends within the next
        /// `milliseconds`. Returns the number of frames handled.</summary>
        public int Pump(int milliseconds)
        {
            int n = 0;
            // Stopwatch, not Environment.TickCount64 - netstandard2.1 (Unity) lacks the latter.
            long deadline = MonotonicMs() + milliseconds;
            while (true)
            {
                int remain = (int)(deadline - MonotonicMs());
                if (remain <= 0) break;
                byte[] frame = _conn.TryReadFrame(remain);
                if (frame == null) break;   // no more data within the window
                Dispatch(frame);
                n++;
            }
            return n;
        }

        private static long MonotonicMs() =>
            System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);

        // ------------------------------------------------ movement ------------
        /// <summary>Send one ZONE_MOVE (24): report current (x,z) + destination
        /// (gotoX,gotoZ). The server stores (x,z) as our live position; we mirror
        /// that into Me so the reported position tracks what we sent.</summary>
        /// <summary>Ask the server for another player's name + appearance (MSG_ZONE_REQ_CHARAC_INFO 26).
        /// The reply (op27, ZoneCharacInfo) fills the PlayerEntity so it can be drawn as a real character
        /// instead of a marker — the client's CreateUser → SendPacket(REQ_CHARAC_INFO) step.</summary>
        public void SendReqCharacInfo(ushort uid)
            => _conn.Send(new Nd.Packets.MsgZoneReqCharacInfo { UniqueId = uid });

        public void SendZoneMove(float x, float z, float gotoX, float gotoZ, int animNumber = 0)
        {
            var mv = new MsgZoneMove
            {
                UniqueId = Me.Uid,
                X = x, Z = z, GotoX = gotoX, GotoZ = gotoZ,
                StandingObb = 0,
            };
            mv.AnimationNumber = animNumber;
            _conn.Send(mv);
            Me.X = x; Me.Z = z;
            MyStateChanged?.Invoke(Me);
        }

        /// <summary>Normal melee attack on a monster (MSG_ACTIVE_FIRE / 69). The server is
        /// authoritative: it computes damage and streams MONSTER_VITAL / MARTIAL_RESULT back.</summary>
        public void SendAttack(ushort targetId, int sequence, int animation = 0)
        {
            var f = new Nd.Packets.MsgActiveFire { TargetId = targetId, Animation = (short)animation };
            f.MartialType = 0;   // normal (no-martial) attack
            f.TargetType = 0;    // en_target_type_monster
            f.Sequence = sequence & 0x3;
            _conn.Send(f);
            AttackSent?.Invoke();
        }

        // ------------------------------------------------ skills (võ công) ----
        /// <summary>Ask the server for the learned-skill list (MSG_SKILL_INFO_REQ 96). The reply
        /// (op 96) arrives on the next Pump and fills <see cref="LearnedSkills"/>.</summary>
        public void RequestSkillInfo()
        {
            _conn.Send(new Nd.Packets.MsgSkillInfoReq { Mode = 1 });
            SkillLog("skill: sent SKILL_INFO_REQ (96)");
        }

        /// <summary>Use a learned martial technique on a target (MSG_NO_MARTIAL_FIRE 69). The wire
        /// packet shape follows the skill's usefulType (SMASHING/ROUNDING/ACTION carry the target +
        /// learn-slot index + step in one fire; the server derives the skill from the slot index).
        /// Returns false if the slot is empty or the skill is not a mob-target combat technique.</summary>
        public bool SendSkill(int slot, ushort targetId, int targetType = 0)
        {
            if (slot < 0 || slot >= LearnedSkills.Count) return false;
            var ls = LearnedSkills[slot];
            if (ls.Id <= 0) return false;

            int useful = NineDragons.Data.SkillRecord.UsefulSmashing;   // default: nhất kích (fire+result)
            if (Skills != null && Skills.TryGet(ls.Id, out var rec)) useful = rec.UsefulType;
            SkillLog($"skill: SendSkill slot={slot} id={ls.Id} wireSlot={ls.Slot} step={ls.Step} useful={useful} target={targetId}");

            switch (useful)
            {
                // One-shot fire (target + learn-slot + step in a single MSG_SMASHING_FIRE). The
                // server resolves the skill from the slot and replies with the compact result.
                case NineDragons.Data.SkillRecord.UsefulSmashing:   // 10 nhất kích
                case NineDragons.Data.SkillRecord.UsefulRounding:   // 8  quần công
                case NineDragons.Data.SkillRecord.UsefulAction:     // 11 hành động
                {
                    var f = new Nd.Packets.MsgSmashingFire
                    {
                        TargetId = targetId,
                        LearnSkillIndex = (byte)ls.Slot,   // wire slot = ucLearnSkillIndex
                        SpellStep = (sbyte)ls.Step,
                    };
                    f.MartialType = useful; f.TargetType = targetType;
                    _conn.Send(f);
                    break;
                }
                // Two-step: MARTIAL_READY (68) latches (target, skill, step) on the server, then a
                // bare MARTIAL_FIRE (69) triggers it — charge / casting / channel / finish.
                case NineDragons.Data.SkillRecord.UsefulCharge:     // 3
                case NineDragons.Data.SkillRecord.UsefulCasting:    // 5
                case NineDragons.Data.SkillRecord.UsefulChannel:    // 6
                case NineDragons.Data.SkillRecord.UsefulFinish:     // 7
                {
                    var ready = new Nd.Packets.MsgMartialReady
                    {
                        TargetId = targetId,
                        LearnSkillIndex = (byte)ls.Slot,
                        Step = (sbyte)ls.Step,
                    };
                    ready.MartialType = useful; ready.TargetType = targetType;
                    _conn.Send(ready);
                    var fire = new Nd.Packets.MsgActiveFire { TargetId = targetId };
                    fire.MartialType = useful; fire.TargetType = targetType; fire.Sequence = 0;
                    _conn.Send(fire);
                    break;
                }
                default:
                    SkillLog($"skill: useful={useful} NOT a combat fire type → normal attack");
                    return false;   // passive / hidden / non-combat — nothing to fire
            }
            SkillLog($"skill: FIRE id={ls.Id} slot={ls.Slot} martial={useful} step={ls.Step} target={targetId}");
            LastSkillTargetId = targetId;
            SkillCast?.Invoke();
            return true;
        }

        // ---- NPC trade (MSG_NPC_TRADE_REQ 130) — the shop's network layer; the UI calls these ----
        /// <summary>Buy a stack from an NPC's sale list (mode 1). <paramref name="saleIndex"/> is the
        /// position in the NPC sale list (ucItemNo); <paramref name="price"/> is the money it costs.</summary>
        public void SendNpcBuy(ushort npcId, int saleIndex, int itemType, int itemId, int count, uint price)
            => _conn.Send(new Nd.Packets.MsgNpcTradeReq
            {
                Mode = (sbyte)Nd.Packets.NpcTradeMode.BuyFromNpc, NpcId = npcId,
                ItemNo = (byte)saleIndex, ItemType = (sbyte)itemType, ItemId = (short)itemId,
                Count = (byte)count, NeedMoney = price,
            });

        /// <summary>Sell one inventory item to an NPC (mode 0). <paramref name="invSlot"/> is the
        /// inventory slot number (ucItemNo); the server pays and updates the inventory in the result.</summary>
        public void SendNpcSell(ushort npcId, int invSlot, int itemType, int itemId, int count)
            => _conn.Send(new Nd.Packets.MsgNpcTradeReq
            {
                Mode = (sbyte)Nd.Packets.NpcTradeMode.SellToNpc, NpcId = npcId,
                ItemNo = (byte)invSlot, ItemType = (sbyte)itemType, ItemId = (short)itemId,
                Count = (byte)count, NeedMoney = 0,
            });

        /// <summary>Ask an NPC for a fixed service that carries no item (heal 5, teleport 7, …).</summary>
        public void SendNpcService(ushort npcId, int mode)
            => _conn.Send(new Nd.Packets.MsgNpcTradeReq { Mode = (sbyte)mode, NpcId = npcId });

        /// <summary>Drag-move an inventory item from slot <paramref name="from"/> to <paramref name="to"/>
        /// (0..149). SERVER-authoritative: the server validates + swaps + answers with an INVEN_MOVE ACK
        /// (114) that we mirror in <see cref="MyCharacter.ApplyInvenMove"/>. Flag 0 = a normal bag/equip
        /// move (not a cash-slot sub-op 0x50/51/52). nd-world main.cpp case 114.</summary>
        public void SendInvenMove(int from, int to)
            => _conn.Send(new Nd.Packets.MsgInvenMove
            {
                AmorPocket = 0, PantsPocket = 0, BackPackExtend = 0,
                Flag = 0, From = (byte)from, To = (byte)to, SendCount = 1,
            });

        /// <summary>Vé di chuyển (Move Village ticket): teleport to the destination whose 1-based row is
        /// <paramref name="sendId"/> (MoveVillageTable.Village.SendId). NPC_TRADE mode 7, village index in
        /// sItemID = sendId (server does g_ndVillage[sendId-1]); it finds/consumes the ticket (id 602) in
        /// the bag itself, so no NPC is needed. The server answers with ZONE_RESTART (same map) or a
        /// resent CHARAC_INFO (different map — new zone/pos) — nd-world main.cpp case 130.</summary>
        public void SendMoveVillage(int sendId)
            => _conn.Send(new Nd.Packets.MsgNpcTradeReq
            {
                Mode = (sbyte)Nd.Packets.NpcTradeMode.MoveVillage, ItemId = (short)sendId,
            });

        /// <summary>True once the world has been built (the initial CHARAC_INFO / EnterWorld is done). Set
        /// by the Unity layer; gates the teleport reload so the login CHARAC_INFO does not trip it.</summary>
        public bool InGame;
        /// <summary>Server teleported us to a DIFFERENT map (CHARAC_INFO resend changed the zone). The Unity
        /// layer rebuilds the map and clears this next frame.</summary>
        public bool PendingZoneReload;
        /// <summary>Server teleported us within the SAME map (ZONE_RESTART). The Unity layer snaps the
        /// player to the new position and clears this next frame.</summary>
        public bool PendingTeleport;

        // MSG_SKILL_CTRL (98) — apply a server-driven skill add / step-up / exp / delete.
        private void ApplySkillCtrl(Nd.Packets.MsgSkillCtrl sc)
        {
            LearnedSkill FindBySlot(int wireSlot)
            {
                for (int i = 0; i < LearnedSkills.Count; i++)
                    if (LearnedSkills[i].Slot == wireSlot) return LearnedSkills[i];
                return null;
            }
            switch (sc.Mode)
            {
                case Nd.Packets.SkillCtrlMode.Add:
                {
                    if (sc.SkillId <= 0) break;
                    var e = FindBySlot(sc.LearnSkillIndex);
                    if (e == null) { e = new LearnedSkill { Slot = sc.LearnSkillIndex }; LearnedSkills.Add(e); }
                    e.Id = sc.SkillId; e.Step = sc.Step; e.Exp = 0; e.State = 0;
                    LearnedSkills.Sort((a, b) => a.Slot.CompareTo(b.Slot));
                    SkillLog($"skill: LEARN id={sc.SkillId} slot={sc.LearnSkillIndex} step={sc.Step}");
                    SkillsChanged?.Invoke();
                    break;
                }
                case Nd.Packets.SkillCtrlMode.UpdateStep:
                {
                    var e = FindBySlot(sc.LearnSkillIndex);
                    if (e != null) { e.Step = sc.Step; e.Exp = 0; SkillLog($"skill: STEP id={e.Id} -> {e.Step}"); SkillsChanged?.Invoke(); }
                    break;
                }
                case Nd.Packets.SkillCtrlMode.UpdateExp:
                {
                    var e = FindBySlot(sc.LearnSkillIndex);
                    if (e != null) { e.Exp = sc.Exp; SkillsChanged?.Invoke(); }
                    break;
                }
                case Nd.Packets.SkillCtrlMode.Delete:
                {
                    for (int i = 0; i < LearnedSkills.Count; i++)
                        if (LearnedSkills[i].Slot == sc.LearnSkillIndex) { LearnedSkills.RemoveAt(i); break; }
                    SkillLog($"skill: DELETE slot={sc.LearnSkillIndex}");
                    SkillsChanged?.Invoke();
                    break;
                }
                case Nd.Packets.SkillCtrlMode.LearnFail:
                case Nd.Packets.SkillCtrlMode.StepUpFail:
                    SkillLog($"skill: FAIL reason={sc.FailReason}");
                    break;
            }
        }

        /// <summary>Request to step-up (level) a learned skill (MSG_SKILL_CTRL_REQ mode 1).</summary>
        public void SendStepUp(int wireSlot) => _conn.Send(Nd.Packets.MsgSkillCtrlReq.StepUp(wireSlot));

        /// <summary>Walk from Me's current position to (destX,destZ) in `steps` ZONE_MOVE
        /// packets, pumping briefly between each so the entity stream keeps flowing.
        /// Returns the number of ZONE_MOVE packets sent.</summary>
        public int MoveTo(float destX, float destZ, int steps = 15, int pumpMsPerStep = 120)
        {
            if (steps < 1) steps = 1;
            float sx = Me.X, sz = Me.Z;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float cx = sx + (destX - sx) * t;
                float cz = sz + (destZ - sz) * t;
                SendZoneMove(cx, cz, destX, destZ);
                if (pumpMsPerStep > 0) Pump(pumpMsPerStep);
            }
            return steps;
        }

        // whitespace-separated ints from an nd-world push text payload (sscanf "%d %d ..." mirror)
        private static int[] ParseInts(string s)
        {
            if (string.IsNullOrEmpty(s)) return System.Array.Empty<int>();
            var parts = s.Split(new[] { ' ', '\t', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            var r = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) int.TryParse(parts[i], out r[i]);
            return r;
        }

        // incoming chat cMode (MSGZoneChatNormal :17337) → g_ChattingStringColor index (0-7):
        // NORMAL/MYMESSAGE/WHISPER/WHOLE/USERCLAN/PARTY/GLOBALNOTICE/ADMINNOTICE.
        private static int ChatColorForMode(int mode, bool own)
        {
            if (mode == 32 || mode == 33) return 2;                 // whisper
            if (mode == 16)               return 5;                 // party
            if (mode == 17 || mode == 18) return 4;                 // clan / ally
            if (mode == 15)               return 3;                 // zone → whole
            if (mode >= 96 && mode <= 99) return 6;                 // shout → notice
            if (mode == 50 || mode == 126) return 6;               // shutdown / golden → notice
            if (mode >= 127)              return 7;                 // admin notice
            return own ? 1 : 0;                                     // normal (own = MYMESSAGE)
        }

        // ChatColors index (from the HUD) → outgoing cMode send code (SendChatMessage :3843-3893).
        private static byte ChatSendMode(int channel)
        {
            switch (channel)
            {
                case 5: return 16;   // party
                case 4: return 17;   // clan
                case 3: return 15;   // zone
                case 6: return 96;   // shout
                default: return 0;   // normal
            }
        }

        /// <summary>Send a zone-chat line on the given channel (ChatColors index). Returns true if it
        /// went out over the wire; false when offline (caller may echo locally instead).</summary>
        public bool SendChat(int channel, string text)
        {
            if (!InWorld || string.IsNullOrEmpty(text)) return false;
            if (text.Length > Nd.Packets.MsgZoneChatNormal.MaxSay)
                text = text.Substring(0, Nd.Packets.MsgZoneChatNormal.MaxSay);
            try { _conn.Send(new Nd.Packets.MsgZoneChatNormal { Mode = ChatSendMode(channel), Name = Me.Name, Say = text }); }
            catch { return false; }
            return true;
        }

        // ------------------------------------------------ dispatch ------------
        private void Dispatch(byte[] buf)
        {
            int id = Wire.PeekMsgId(buf);
            PacketCounts.TryGetValue(id, out int c); PacketCounts[id] = c + 1;

            switch (id)
            {
                case MsgId.CharacInfo:      // 16
                {
                    var ci = MsgCharacInfo.Parse(buf);
                    // A mid-game CHARAC_INFO with a NEW zone = a seamless teleport to a different map
                    // (vé di chuyển / portal, nd-world main.cpp case 130 resend). Flag it so the Unity
                    // layer rebuilds the map; the login CHARAC_INFO is gated out by InGame.
                    if (InGame && ci.Zone != Me.Zone) PendingZoneReload = true;
                    Me.Zone = ci.Zone; Me.Class = ci.Class; Me.ClassGrade = ci.ClassGrade;
                    Me.X = ci.InitPosX; Me.Z = ci.InitPosZ;
                    Me.CurLife = ci.CurLifePower; Me.MaxLife = ci.MaxLifePower;
                    Me.CurForce = ci.CurForcePower; Me.MaxForce = ci.MaxForcePower;
                    Me.CurConc = ci.CurConcentrationPower; Me.MaxConc = ci.MaxConcentrationPower;
                    Me.Exp = ci.LeftJin; Me.Honor = ci.Honor;
                    Me.LeftPoint = ci.LeftPoint; Me.LeftMasteryPoint = ci.LeftMasteryPoint;
                    MyStateChanged?.Invoke(Me);
                    break;
                }
                case MsgId.SetPower:        // 105
                    Me.ApplySetPower(MsgSetPower.Parse(buf));
                    MyStateChanged?.Invoke(Me);
                    break;

                case 210:                   // MSGNDPush — nd-world server-authoritative push
                {                           // wire: [2]=210 [3]=sub [4..]=ASCII text (sscanf'd)
                    if (buf.Length > 4)
                    {
                        int sub = buf[3];
                        int end = 4; while (end < buf.Length && buf[end] != 0) end++;
                        string text = System.Text.Encoding.ASCII.GetString(buf, 4, end - 4);
                        // sub 13 = MAXSTAT (_XDEF_ND_PUSHSUB_MAXSTAT): "<maxLife> <maxForce> <maxConc>",
                        // cash-folded + server-authoritative. CHARAC_INFO's max is only the stale BASE,
                        // so without this the HUD shows cur > max (XNetwork_item_trade.cpp:9271).
                        if (sub == 13) { Me.ApplyMaxStatPush(ParseInts(text)); MyStateChanged?.Invoke(Me); }
                        else if (sub == 16) { Me.ApplyStatPush16(ParseInts(text)); MyStateChanged?.Invoke(Me); }
                        else if (sub == 17) { Me.ApplyStatPush17(ParseInts(text)); MyStateChanged?.Invoke(Me); }
                    }
                    break;
                }

                case MsgId.ZoneChatNormal:  // 28 — incoming zone chat (normal/whisper/party/clan/notice)
                {
                    var ch = Nd.Packets.MsgZoneChatNormal.Parse(buf);
                    bool own = !string.IsNullOrEmpty(ch.Name) &&
                               string.Equals(ch.Name, Me.Name, StringComparison.OrdinalIgnoreCase);
                    int col = ChatColorForMode(ch.Mode, own);
                    // reference tempbuffer = cName + ":" + cSay, except mode 33 (face-mask) has no name
                    string line = (ch.Mode == 33 || string.IsNullOrEmpty(ch.Name))
                        ? ch.Say : $"{ch.Name}: {ch.Say}";
                    ChatReceived?.Invoke(col, line);
                    break;
                }

                case MsgId.SetLevel:        // 104
                    Me.Exp = MsgSetLevel.Parse(buf).Jin;
                    MyStateChanged?.Invoke(Me);
                    break;

                case MsgId.LevelUp:         // 106 LEVEL_UP_RESULT (S->C): new inner level on level-up
                {
                    var lu = MsgLevelUpResult.Parse(buf);
                    if (lu.LevelupInfo == 1 && lu.InnerLevel > 0)   // 1 = leveled up
                    {
                        Me.LeftPoint = lu.LeftPoint; Me.LeftMasteryPoint = lu.LeftMateryPoint;
                        LevelChanged?.Invoke(lu.InnerLevel);        // nameplate colour path re-reads this
                        MyStateChanged?.Invoke(Me);
                    }
                    break;
                }

                case MsgId.CharacSetState:  // 18 (honor/fame etc.)
                {
                    var s = MsgCharacSetState.Parse(buf);
                    if (s.Flag == 4) Me.Honor = s.State;   // CHARAC_STATE_EN_HONOR
                    MyStateChanged?.Invoke(Me);
                    break;
                }
                case MsgId.InvenInfo:       // 121
                {
                    uint prevMoney = Me.Money; int prevCount = Me.Inventory.Count;
                    var invp = MsgInvenInfo.Parse(buf);
                    NdInvenDiag.Dump(buf, invp);   // TEMP diagnostic: dump wire + parsed inventory to nd_inven_diag.log
                    Me.ApplyInventory(invp);
                    if (_invenSynced)   // skip the initial login snapshot; fire on later gains
                    {
                        if (Me.Money > prevMoney) MoneyGained?.Invoke();
                        if (Me.Inventory.Count > prevCount) ItemPickedUp?.Invoke();
                    }
                    _invenSynced = true;
                    MyStateChanged?.Invoke(Me);
                    break;
                }

                case MsgId.InvenMove:       // 114 ACK (S->C): server swapped from<->to; mirror it locally
                {
                    var mv = MsgInvenMove.Parse(buf);
                    Me.ApplyInvenMove(mv.From, mv.To);
                    MyStateChanged?.Invoke(Me);
                    break;
                }

                case MsgId.ZonePositioning: // 25 (server position correction)
                {
                    var z = MsgZonePositioning.Parse(buf);
                    Me.Zone = z.Zone; Me.X = z.InitPosX; Me.Z = z.InitPosZ;
                    MyStateChanged?.Invoke(Me);
                    break;
                }
                case MsgId.ZoneRestart:     // 17 (revive / same-map vé di chuyển)
                {
                    var z = MsgZoneRestart.Parse(buf);
                    Me.X = z.RestartPosX; Me.Z = z.RestartPosZ; Me.CurLife = z.VitalPower;
                    if (InGame) PendingTeleport = true;   // snap the player to the new spot (in-place teleport)
                    MyStateChanged?.Invoke(Me);
                    break;
                }

                case MsgId.MonsterExist:    // 40
                {
                    var e = MsgMonsterExist.Parse(buf);
                    if (!World.Monsters.TryGetValue(e.MobId, out var m))
                    {
                        m = new MonsterEntity { Id = e.MobId, WireType = e.MobType };
                        m.Enrich(_mobs);
                        World.Monsters[e.MobId] = m;
                    }
                    m.WireType = e.MobType; m.X = e.X; m.Z = e.Z;
                    m.GotoX = e.X; m.GotoZ = e.Z;
                    m.VitalRate = e.MobVitalRate; m.Alive = e.MobVitalRate >= 0;
                    MonsterSeen?.Invoke(m);
                    break;
                }
                case MsgId.MonsterMove:     // 30
                {
                    var mm = MsgMonsterMove.Parse(buf);
                    if (!World.Monsters.TryGetValue(mm.MobId, out var m))
                    {
                        m = new MonsterEntity { Id = mm.MobId, WireType = mm.MobType };
                        m.Enrich(_mobs);
                        World.Monsters[mm.MobId] = m;
                    }
                    m.WireType = mm.MobType; m.X = mm.X; m.Z = mm.Z;
                    m.GotoX = mm.GotoX; m.GotoZ = mm.GotoZ;
                    m.WalkMode = mm.WalkMode; m.SpeedRate = mm.SpeedRate;   // per-move speed (u/s = base*rate/20)
                    m.VitalRate = mm.MobVitalRate; m.Alive = mm.MobVitalRate >= 0;
                    MonsterMoved?.Invoke(m);
                    break;
                }
                case MsgId.MonsterVital:    // 31
                {
                    var mv = MsgMonsterVital.Parse(buf);
                    if (World.Monsters.TryGetValue(mv.MobId, out var m))
                    {
                        m.VitalRate = mv.MobVitalRate;
                        if (mv.MobVitalRate < 0) { m.Alive = false; MonsterDied?.Invoke(mv.MobId); }
                        else MonsterMoved?.Invoke(m);
                    }
                    break;
                }

                case MsgId.ZoneCharacInfo:  // 27 (another player appears — name + appearance)
                {
                    // #pragma pack(1) wire layout (CODEX_OFFLINE nick 6+2):
                    //  uid@24, (cGM:5|cSex:3)@26, face@27, hair@28,
                    //  sAmor@37, sPants@39, sCap@41, sUseWeapon@49, posX@52, posZ@56
                    if (buf.Length >= 60)
                    {
                        ushort uid = (ushort)(buf[24] | buf[25] << 8);
                        if (uid != Me.Uid)
                        {
                            if (!World.Players.TryGetValue(uid, out var p))
                            { p = new PlayerEntity { Uid = uid }; World.Players[uid] = p; }
                            p.Name = Nd.Packets.Wire.CStr(buf, 3, 13);
                            p.Sex = (buf[26] >> 5) & 0x7;
                            p.Face = (sbyte)buf[27];
                            p.Hair = (sbyte)buf[28];
                            p.Amor = (short)(buf[37] | buf[38] << 8);
                            p.Pants = (short)(buf[39] | buf[40] << 8);
                            p.Cap = (short)(buf[41] | buf[42] << 8);
                            p.Weapon = (short)(buf[49] | buf[50] << 8);
                            p.X = BitConverter.ToSingle(buf, 52);
                            p.Z = BitConverter.ToSingle(buf, 56);
                            p.GotoX = p.X; p.GotoZ = p.Z;
                            p.HasAppearance = true;
                            PlayerMoved?.Invoke(p);
                        }
                    }
                    break;
                }
                case MsgId.ZoneMove:        // 24 (S->C = another player's move)
                {
                    var zm = MsgZoneMove.Parse(buf);
                    if (zm.UniqueId != Me.Uid)
                    {
                        if (!World.Players.TryGetValue(zm.UniqueId, out var p))
                        { p = new PlayerEntity { Uid = zm.UniqueId }; World.Players[zm.UniqueId] = p; }
                        p.X = zm.X; p.Z = zm.Z; p.GotoX = zm.GotoX; p.GotoZ = zm.GotoZ;
                        p.AnimationNumber = zm.AnimationNumber;
                        PlayerMoved?.Invoke(p);
                    }
                    break;
                }

                case MsgId.BAtkResFromMob:  // 50 (mob hit me -> Damage = remaining HP)
                {
                    var r = MsgBAtkResFromMob.Parse(buf);
                    if (r.DefenderId == Me.Uid) { Me.CurLife = r.Damage; MyStateChanged?.Invoke(Me); }
                    // The attacking mob plays its attack clip (XNetwork_Battle.cpp:679-686):
                    // ucAtkType (bits 4-5 of the packed byte) picks atk1/2/3/rattack.
                    MonsterAttacked?.Invoke(r.AttackerId, (r.AtkByte >> 4) & 0x3);
                    // cPassiveTitle>0 rides the SAME packet to signal a mob SKILL (XNetwork_Battle.cpp:942-987):
                    // the id matches the mob's skill1_id/skill2_id → SKILL1/SKILL2 motion; SUICIDE(20)→die,
                    // REBIRTH(21) plays a skill too. We lack the client attrib table, so AnimDriver plays the
                    // mob's skill motion skill1-preferred (the client's own SetCurMotion fallback order).
                    if (r.PassiveTitle > 0)
                    {
                        if (r.PassiveTitle == MobSkillSuicide) MonsterDied?.Invoke(r.AttackerId); // the mob blows itself up
                        else MonsterSkill?.Invoke(r.AttackerId, r.PassiveTitle);
                    }
                    break;
                }

                case MsgId.MartialResult:   // 70 combat result — apply damage/death to the target mob
                {
                    // Skill techniques (charge/casting/rounding/smashing) return the compact
                    // MSG_SMASHING_RESULT_MOB layout (20/24 B), NOT the 26-B active result. The
                    // server picks the layout by the echoed martial-type nibble, so dispatch on it.
                    int martial = buf.Length > 3 ? (buf[3] & 0xF) : 0;
                    if (martial == NineDragons.Data.SkillRecord.UsefulCharge ||
                        martial == NineDragons.Data.SkillRecord.UsefulCasting ||
                        martial == NineDragons.Data.SkillRecord.UsefulRounding ||
                        martial == NineDragons.Data.SkillRecord.UsefulSmashing)
                    {
                        var sr = MsgSmashingResultMob.Parse(buf);
                        _combatResults++;
                        if (World.Monsters.TryGetValue(sr.TargetId, out var stm))
                        {
                            if (sr.MobVitalRate < 0)
                            { stm.Alive = false; stm.VitalRate = -1; MonsterDied?.Invoke(sr.TargetId); }
                            else { stm.VitalRate = sr.MobVitalRate; MonsterMoved?.Invoke(stm); }
                            MonsterDamaged?.Invoke(sr.TargetId, sr.Damage);
                        }
                        SkillLog($"skill: RESULT martial={martial} skill={sr.SkillId} dmg={sr.Damage} vital={sr.MobVitalRate}");
                        break;
                    }
                    // Channel(6) / Finish(7) carry their own compact layouts (24 / 28 B) — read the
                    // target, vital and damage at the offsets the server writes for each.
                    if ((martial == NineDragons.Data.SkillRecord.UsefulChannel && buf.Length >= 24) ||
                        (martial == NineDragons.Data.SkillRecord.UsefulFinish && buf.Length >= 28))
                    {
                        ushort tgt; sbyte vital; int dmg;
                        if (martial == NineDragons.Data.SkillRecord.UsefulChannel)
                        {
                            tgt = (ushort)(buf[13] | buf[14] << 8);
                            vital = (sbyte)buf[19];
                            dmg = buf[22] | buf[23] << 8;
                        }
                        else // finish(7)
                        {
                            tgt = (ushort)(buf[10] | buf[11] << 8);
                            vital = (sbyte)buf[23];
                            dmg = buf.Length >= 32 ? unchecked((int)Wire.U32(buf, 28)) : (buf[25] | buf[26] << 8);
                        }
                        _combatResults++;
                        if (World.Monsters.TryGetValue(tgt, out var cm))
                        {
                            if (vital < 0) { cm.Alive = false; cm.VitalRate = -1; MonsterDied?.Invoke(tgt); }
                            else { cm.VitalRate = vital; MonsterMoved?.Invoke(cm); }
                            MonsterDamaged?.Invoke(tgt, dmg);
                        }
                        SkillLog($"skill: RESULT martial={martial} dmg={dmg} vital={vital}");
                        break;
                    }

                    var r = MsgActiveResultMob.Parse(buf);
                    _combatResults++;
                    if (World.Monsters.TryGetValue(r.TargetId, out var tm))
                    {
                        sbyte vr = r.MobVitalRate3 != 0 ? r.MobVitalRate3
                                 : (r.MobVitalRate2 != 0 ? r.MobVitalRate2 : r.MobVitalRate1);
                        if (r.MobVitalRate1 < 0 || r.MobVitalRate2 < 0 || r.MobVitalRate3 < 0)
                        { tm.Alive = false; tm.VitalRate = -1; MonsterDied?.Invoke(r.TargetId); }
                        else { tm.VitalRate = vr; MonsterMoved?.Invoke(tm); }
                        MonsterDamaged?.Invoke(r.TargetId, r.Damage1 + r.Damage2 + r.Damage3);
                    }
                    break;
                }

                case MsgId.SkillInfo:       // 96 learned-skill list (S->C)
                {
                    var si = MsgSkillInfo.Parse(buf);
                    LearnedSkills.Clear();
                    for (int i = 0; i < si.Count; i++)
                    {
                        var w = si.Skills[i];
                        if (w.Id <= 0) continue;   // empty slot — server keeps the index gap, skip for the list
                        LearnedSkills.Add(new LearnedSkill
                        { Slot = i, Id = w.Id, Step = w.Step, Exp = w.Exp, State = w.SkillState });
                    }
                    SkillLog($"skill: SKILL_INFO {LearnedSkills.Count} learned (of {si.Count} slots)");
                    SkillsChanged?.Invoke();
                    break;
                }
                case MsgId.SkillCtrl:       // 98 skill add / step-up / exp / delete (S->C)
                {
                    var sc = MsgSkillCtrl.Parse(buf);
                    ApplySkillCtrl(sc);
                    break;
                }

                // Frames we knowingly ignore for state (still counted above).
                case MsgId.MasteryPointUse: // 108 mastery restore burst
                case MsgId.CharacCtrl:      // 32 death/coma control
                case MsgId.StateChange:     // 19 peace/battle ack
                case MsgId.TimeCheck:       // 14
                    break;

                default:
                    UnknownPacket?.Invoke(id, buf);
                    break;
            }
        }

        // ------------------------------------------------ snapshot ------------
        public string Snapshot(int maxMonsters = 20)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---------------- LIVE SNAPSHOT ----------------");
            sb.AppendLine($"  Me: '{Me.Name}' uid={Me.Uid} zone={Me.Zone} class={Me.Class}/{Me.ClassGrade}");
            sb.AppendLine($"      pos=({Me.X:0.##},{Me.Z:0.##})  " +
                          $"Life {Me.CurLife}/{Me.MaxLife}  Force {Me.CurForce}/{Me.MaxForce}  Conc {Me.CurConc}/{Me.MaxConc}");
            sb.AppendLine($"      EXP={Me.Exp}  Honor={Me.Honor}  Money={Me.Money}  Inventory={Me.Inventory.Count} items");

            int alive = 0; foreach (var _ in World.AliveMonsters) alive++;
            sb.AppendLine($"  Nearby monsters: {alive} alive / {World.Monsters.Count} known" +
                          (World.Players.Count > 0 ? $"   Players: {World.Players.Count}" : ""));

            int shown = 0;
            foreach (var m in World.AliveMonsters)
            {
                if (shown++ >= maxMonsters) { sb.AppendLine($"      ... (+{alive - maxMonsters} more)"); break; }
                float dx = m.X - Me.X, dz = m.Z - Me.Z;
                double dist = Math.Sqrt(dx * dx + dz * dz);
                string info = m.Level > 0 ? $"lvl{m.Level} hp{m.Hp} exp{m.ExpReward}" : "(no monref)";
                sb.AppendLine($"      mob id={m.Id} type={m.WireType}(raw {m.RawType}) {info} " +
                              $"pos=({m.X:0.#},{m.Z:0.#}) vital={m.VitalRate}% dist={dist:0.#}");
            }
            sb.Append("-----------------------------------------------");
            return sb.ToString();
        }

        public string PacketSummary()
        {
            var sb = new StringBuilder("  packets seen: ");
            var keys = new List<int>(PacketCounts.Keys); keys.Sort();
            foreach (var k in keys) sb.Append($"{k}x{PacketCounts[k]} ");
            return sb.ToString();
        }

        public void Dispose() => _conn.Dispose();
    }
}
