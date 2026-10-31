// ============================================================================
//  PacketIds.cs - MSG_NO_* message ids + slot/mode constants.
//  Values copied verbatim from src/nd_gs_packets.h and the client
//  Network/packet_for_*.h. The id is the ucMessage byte (offset 2).
// ============================================================================
namespace Nd.Packets
{
    public static class MsgId
    {
        // --- server enter / char select (packet_for_svrmove.h, zone) ---
        public const int SvEnterGame        = 152;
        public const int SvEnterGServer     = 153;
        public const int SvEnterResult      = 154;
        public const int CharacReqList      = 8;
        public const int CharacList         = 9;
        public const int CharacSelect       = 10;
        public const int CharacCreate       = 11;
        public const int CharacDelete       = 12;
        public const int CharacResult       = 13;
        public const int CharacInfo         = 16;
        public const int ZoneCharacInfo     = 27;   // another player appears (name + appearance)
        public const int CharacGameStartSig = 20;
        public const int SeqNumberGs        = 0;
        public const int TimeCheck          = 14;

        // --- zone / world (packet_for_zone_monster.h) ---
        public const int ZoneRestart        = 17;   // S->C revive
        public const int CharacSetState     = 18;   // S->C one status field (honor...)
        public const int StateChange        = 19;   // C<->S peace/battle toggle
        public const int ZoneMove           = 24;   // char move (legacy MSG_ZONE_MOVE)
        public const int ZonePositioning    = 25;
        public const int ZoneReqCharacInfo  = 26;   // C->S ask for another player's name+appearance

        public const int ZoneChatNormal     = 28;
        public const int MonsterMove        = 30;
        public const int MonsterVital       = 31;
        public const int CharacCtrl         = 32;   // binsa / coma / freeze
        public const int CharacSelectMode   = 33;   // C->S choose respawn
        public const int MonsterExist       = 40;
        public const int BAtkResFromMob     = 50;   // S->C mob hits player

        // --- combat (packet_for_battle.h) ---
        public const int SelfChannel        = 64;   // C<->S sit-channel (van khi / khinh cong)
        public const int MartialReady       = 68;
        public const int MartialFire        = 69;   // C->S player attacks / uses a martial skill
        public const int MartialResult      = 70;   // S->C damage result
        public const int MartialEnd         = 71;

        // --- skill list / learn (packet_for_skill.h) ---
        public const int SkillInfo          = 96;   // C->S request (ucMode) / S->C learned-skill list
        public const int SkillSelect        = 97;   // C->S choose a skill's level
        public const int SkillCtrl          = 98;   // C->S learn/step-up/delete req; S->C add/update/delete

        // --- level / stats (packet_for_level.h) ---
        public const int SetLevel           = 104;  // S->C EXP update
        public const int SetPower           = 105;  // S->C hp/force/conc update
        public const int LevelUp            = 106;  // C->S req / S->C result
        public const int PointUse           = 107;  // C<->S add 1 stat point
        public const int MasteryPointUse    = 108;  // C<->S add 1 skill mastery step

        // --- inventory / trade (packet_for_items_trade.h) ---
        public const int InvenMove          = 114;  // C<->S move/equip/unequip
        public const int InvenInfo          = 121;  // S->C inventory contents
        public const int NpcTrade           = 130;  // C->S buy/sell/heal/gamble at an NPC (MSG_NPC_TRADE_REQ); S->C result
        public const int StrItemReq         = 132;  // C->S open warehouse (MSG_NO_STR_ITEM_REQ)
        public const int WorldItem          = 144;  // C->S pick/drop
        public const int InvenReq           = 146;  // C->S open bag
    }

    /// <summary>EN_NPC_TRADE_MODE (packet_for_items_trade.h:699) — cBuyFromNPC in MSG_NPC_TRADE_REQ.</summary>
    public static class NpcTradeMode
    {
        public const int SellToNpc          = 0;   // en_npc_trade_mode_sell
        public const int BuyFromNpc         = 1;   // en_npc_trade_mode_buy
        public const int ContributeMoonpa   = 2;   // donate item to the sect
        public const int Heal               = 5;   // NPC heal
        public const int Gamble             = 6;
        public const int MoveVillage        = 7;   // teleport request
        public const int BuyContribute      = 8;
    }

    /// <summary>Equipment slot indices (client _XINVENTORY_SLOTTYPE_*), see nd_gs_packets.h.</summary>
    public static class InvSlot
    {
        public const int Amor        = 0;
        public const int Pants       = 1;
        public const int Adae        = 2;   // gauntlets
        public const int Shoes       = 3;
        public const int Cap         = 4;
        public const int Mask        = 7;
        public const int Weapon      = 10;
        public const int PocketStart = 25;  // 25..149 = 5 bag pages x 25 slots
        public const int TotalCount  = 150;
    }

    /// <summary>EN_CHARAC_SELECT_MODE (CHARAC_SELECT_MODE cMode).</summary>
    public static class CharacSelMode
    {
        public const int AtCity      = 0;
        public const int AtSave      = 1;
        public const int AtLast      = 2;   // in place
        public const int WaitHelp    = 3;
        public const int CancelWait  = 4;
        public const int SpellRebirth = 5;
        public const int ItemUse     = 6;   // Hoa Da revive in place
        public const int InCastle    = 7;
    }

    /// <summary>STATE_CHANGE_ENUM (peace/battle, Tab key).</summary>
    public static class StateChangeMode
    {
        public const int PeaceReq     = 3;  // C->S
        public const int BattleReq    = 4;  // C->S
        public const int NoticePeace  = 8;  // S->C
        public const int NoticeBattle = 9;  // S->C
    }

    /// <summary>CHARAC_CTRL cFlag.</summary>
    public static class CharacCtrlFlag
    {
        public const int Binsa = 0;  // HP 0 -> dying anim + countdown
        public const int Dead  = 1;  // coma (revive countdown)
    }

    /// <summary>MSG_MOVE.move_mode (packet_for_zone_monster.h).</summary>
    public static class MoveMode
    {
        public const int ReqMove       = 0;   // C->S request
        public const int PlayerMove    = 1;   // S->C broadcast
        public const int CharacStop    = 2;   // C->S stop
        public const int Stop          = 3;   // S->C stop
        public const int LpsJumpReq    = 10;
        public const int LpsJumpSuccess = 11;
        public const int GotoPos       = 20;
        public const int SetSequence   = 21;
        public const int Error         = 30;
    }
}
