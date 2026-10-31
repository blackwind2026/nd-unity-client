using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NineDragons.Client
{
    /// <summary>
    /// NPC model index → base .X3D, parsed from NPCModel.XMS (in AR_BASEANIMATION.XP), the
    /// same table the client feeds to g_NPCMODEL. Format (verified):
    ///   _XM_MODELINDEX &lt;n&gt;
    ///   _XM_BIPEDMODEL &lt;npc_*.xbp&gt;
    ///   _XM_BASEMODEL  &lt;npc_*.X3D&gt;
    ///   _XM_ANIMATION IDLE|WALK|... &lt;*.XA&gt;
    /// The XNS record's object_index selects the entry. Mesh vertices are bind-pose, so the
    /// base X3D renders as a standing NPC directly.
    /// </summary>
    public sealed class NpcModelTable
    {
        public sealed class Entry
        {
            public int Index;
            public string BaseModel = "";
            public string Biped = "";
            public string IdleAnim = "", WalkAnim = "", ActionAnim = "";
            // Mob combat clips (MobModel.XMS). The client keys these off the named _XM_ANIMATION
            // tags, mapping them to the g_MobMotionExistTable slots in _XLoadMOBModelScripter
            // (XBipedController.cpp:6104-6155). The tag→slot mapping is offset exactly as the
            // client does it: tag "ATTACK"→attack1, "ATTACK1"→attack2, "ATTACK2"→attack3.
            public string RunAnim = "";
            public string Attack1Anim = "", Attack2Anim = "", Attack3Anim = "";
            public string RattackAnim = "";
            public string BlowAnim = "";           // tag "DAMAGE" (mob hit-reaction, _XMOBACTION_BLOW)
            public string Die1Anim = "", Die2Anim = "";
            public string Skill1Anim = "", Skill2Anim = "";
            public string EnteringAnim = "";       // tag "ENTER1" (_XMOBACTION_ENTERING spawn/appear)

            // Per-motion SOUND (.wav) from each animation's _XM_SOUNDEFFECT line (frame,wav pairs;
            // XBipedController.cpp:5819-5868). We keep the FIRST wav of the motion — the mob's own
            // grunt/cry — so a mob no longer borrows the player's MCh01 scream.
            public string Attack1Sound = "", Attack2Sound = "", Attack3Sound = "", RattackSound = "";
            public string BlowSound = "", Die1Sound = "", Die2Sound = "", Skill1Sound = "", Skill2Sound = "";
            public string DeathSound => Die1Sound.Length > 0 ? Die1Sound : Die2Sound;
            /// <summary>Mob skill clip (skill1-preferred, exactly the client's SetCurMotion fallback in
            /// _XMOBACTION_SKILL, XMob.cpp:1835-1845: skill1 if present, else skill2).</summary>
            public string SkillAnim => Skill1Anim.Length > 0 ? Skill1Anim : Skill2Anim;
            /// <summary>The clip to play while the mob is moving: WALK if it has one, else RUN — exactly the
            /// client's _XMOBACTION_MOVE fallback (XMob.cpp:1926-1939), so a run-only mob still moves its
            /// legs instead of sliding. (Falls back to IDLE so a legless move never plays the wrong clip.)</summary>
            public string MoveAnim => WalkAnim.Length > 0 ? WalkAnim : (RunAnim.Length > 0 ? RunAnim : IdleAnim);
            /// <summary>Grunt for the skill the mob just cast (same skill1-preferred order).</summary>
            public string SkillSound => Skill1Sound.Length > 0 ? Skill1Sound : Skill2Sound;
            /// <summary>Attack grunt for a server atkType, same fallback order as <see cref="AttackAnimFor"/>.</summary>
            public string AttackSoundFor(int atkType)
            {
                switch (atkType)
                {
                    case 1: return First(Attack2Sound, Attack1Sound, Attack3Sound);
                    case 2: return First(Attack3Sound, Attack1Sound, Attack2Sound);
                    case 3: return First(RattackSound, Attack1Sound, Attack2Sound, Attack3Sound);
                    default: return First(Attack1Sound, Attack2Sound, Attack3Sound);
                }
            }

            /// <summary>Death clip the client picks (XMob.cpp:1788-1816): default dietype DIE1 →
            /// die1 if present, else die2.</summary>
            public string DeathAnim => Die1Anim.Length > 0 ? Die1Anim : Die2Anim;
            /// <summary>Mob attack clip for a server atkType (0=atk1,1=atk2,2=atk3,3=rattack),
            /// with the same fallback chain the client uses (XMob.cpp:1698-1771).</summary>
            public string AttackAnimFor(int atkType)
            {
                switch (atkType)
                {
                    case 1: return First(Attack2Anim, Attack1Anim, Attack3Anim);
                    case 2: return First(Attack3Anim, Attack1Anim, Attack2Anim);
                    case 3: return First(RattackAnim, Attack1Anim, Attack2Anim, Attack3Anim);
                    default: return First(Attack1Anim, Attack2Anim, Attack3Anim);
                }
            }
            private static string First(params string[] c)
            { foreach (var s in c) if (!string.IsNullOrEmpty(s)) return s; return null; }
        }

        private readonly Dictionary<int, Entry> _byIndex = new Dictionary<int, Entry>();
        public int Count => _byIndex.Count;

        public Entry Get(int index) => _byIndex.TryGetValue(index, out var e) ? e : null;
        public string BaseModelFor(int index) => Get(index)?.BaseModel;

        public static NpcModelTable Parse(byte[] xms)
        {
            var t = new NpcModelTable();
            using var reader = new StringReader(Encoding.GetEncoding(28591).GetString(xms));
            string line;
            Entry cur = null;
            string lastKind = null;   // the kind of the most recent _XM_ANIMATION, so _XM_SOUNDEFFECT binds to it
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length < 2) continue;
                switch (tok[0])
                {
                    case "_XM_MODELINDEX":
                        if (int.TryParse(tok[1], out int idx))
                        { cur = new Entry { Index = idx }; t._byIndex[idx] = cur; }
                        break;
                    case "_XM_BASEMODEL": if (cur != null) cur.BaseModel = tok[1]; break;
                    case "_XM_BIPEDMODEL": if (cur != null) cur.Biped = tok[1]; break;
                    case "_XM_ANIMATION":
                        if (cur != null && tok.Length >= 3)
                        {
                            // the .XMS mixes case (mobs use "Walk", NPCs "WALK") — compare loosely
                            var kind = tok[1].ToUpperInvariant();
                            var file = tok[2];
                            lastKind = kind;   // a following _XM_SOUNDEFFECT belongs to THIS motion
                            if (kind == "IDLE") cur.IdleAnim = file;
                            else if (kind == "WALK") cur.WalkAnim = file;
                            // first gesture animation (NPCs bow/wave periodically); keep the first only
                            else if (kind.StartsWith("IDLEACTION") && cur.ActionAnim.Length == 0) cur.ActionAnim = file;
                            // mob combat clips — the exact tag→slot mapping the client uses
                            // (XBipedController.cpp:6104-6155). Note the deliberate offset:
                            // "ATTACK"→attack1, "ATTACK1"→attack2, "ATTACK2"→attack3.
                            else if (kind == "RUN") cur.RunAnim = file;
                            else if (kind == "ATTACK") cur.Attack1Anim = file;
                            else if (kind == "ATTACK1") cur.Attack2Anim = file;
                            else if (kind == "ATTACK2") cur.Attack3Anim = file;
                            else if (kind == "RATTACK") cur.RattackAnim = file;
                            else if (kind == "DAMAGE") cur.BlowAnim = file;
                            else if (kind == "DIE1") cur.Die1Anim = file;
                            else if (kind == "DIE2") cur.Die2Anim = file;
                            else if (kind == "SKILL1") cur.Skill1Anim = file;
                            else if (kind == "SKILL2") cur.Skill2Anim = file;
                            else if (kind == "ENTER1") cur.EnteringAnim = file;
                        }
                        break;
                    case "_XM_SOUNDEFFECT":
                        // "_XM_SOUNDEFFECT <frame0> <wav0> <frame1> <wav1> ..." — first wav (tok[2]) is
                        // the motion's main sound; bind it to the motion the preceding _XM_ANIMATION set.
                        if (cur != null && lastKind != null && tok.Length >= 3)
                        {
                            var wav = tok[2];
                            if (!wav.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
                                !wav.EndsWith(".WAV", StringComparison.OrdinalIgnoreCase)) break;   // guard: tok[2] must be a filename
                            switch (lastKind)
                            {
                                case "ATTACK":  cur.Attack1Sound = wav; break;
                                case "ATTACK1": cur.Attack2Sound = wav; break;
                                case "ATTACK2": cur.Attack3Sound = wav; break;
                                case "RATTACK": cur.RattackSound = wav; break;
                                case "DAMAGE":  cur.BlowSound = wav; break;
                                case "DIE1":    cur.Die1Sound = wav; break;
                                case "DIE2":    cur.Die2Sound = wav; break;
                                case "SKILL1":  cur.Skill1Sound = wav; break;
                                case "SKILL2":  cur.Skill2Sound = wav; break;
                            }
                        }
                        break;
                }
            }
            return t;
        }
    }
}
