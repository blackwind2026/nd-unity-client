// ============================================================================
//  InventoryPackets.cs - inventory request / move / contents.
//  Ground truth: client packet_for_items_trade.h (MSG_INVEN_MOVE, MSG_INVEN_REQ)
//  + src/main.cpp send_inventory() (the exact INVEN_INFO byte layout on the wire).
// ============================================================================
using System;
using System.Collections.Generic;

namespace Nd.Packets
{
    /// <summary>INVEN_INFO row stride selection (wire framing, matches client codexWireStride
    /// and server nd_is_weapon_type/nd_is_cloth_type). Equip rows are 83 bytes, others 10.</summary>
    public static class InvItemType
    {
        public static bool IsWeapon(int t) => t == 0 || t == 15 || t == 19;
        public static bool IsCloth(int t)  => t == 1 || t == 16 || t == 20;
        public static bool IsEquip(int t)  => IsWeapon(t) || IsCloth(t);

        public const int RowEquip = 83;   // weapon / cloth (has refine + socket tail)
        public const int RowOther = 10;   // accessory + everything else (header only)
        public static int RowStride(int type) => IsEquip(type) ? RowEquip : RowOther;
    }

    // --- 146 INVEN_REQ (C->S) : open bag, 3 bytes ---
    public sealed class MsgInvenReq : NdPacket
    {
        public const int Size = 3;
        public override int MsgId => Nd.Packets.MsgId.InvenReq;
        public override int WireSize => Size;
        public override int Write(Span<byte> d) => WritePrefix(d);
        public static MsgInvenReq Parse(ReadOnlySpan<byte> d) => new MsgInvenReq();
    }

    // --- 114 INVEN_MOVE (C<->S) : move/equip/unequip, 10 bytes ---
    //  C->S request AND S->C ack share this layout. Server swaps invId[cFrom]/[cTo].
    public sealed class MsgInvenMove : NdPacket
    {
        public const int Size = 10;
        public sbyte AmorPocket;      // @3 tin-vat slots of worn AMOR (updated in S->C ack)
        public sbyte PantsPocket;     // @4 tin-vat slots of worn PANTS
        public sbyte BackPackExtend;  // @5
        public sbyte Flag;            // @6 cFlag
        public byte  From;            // @7 source slot (0..149)
        public byte  To;              // @8 dest slot
        public byte  SendCount;       // @9 (ack sets 1)

        public override int MsgId => Nd.Packets.MsgId.InvenMove;
        public override int WireSize => Size;
        public override int Write(Span<byte> d)
        {
            int o = WritePrefix(d);   // 3
            Wire.S8(d, o + 0, AmorPocket);
            Wire.S8(d, o + 1, PantsPocket);
            Wire.S8(d, o + 2, BackPackExtend);
            Wire.S8(d, o + 3, Flag);
            Wire.U8(d, o + 4, From);
            Wire.U8(d, o + 5, To);
            Wire.U8(d, o + 6, SendCount);
            return Size;
        }
        public static MsgInvenMove Parse(ReadOnlySpan<byte> d)
        {
            int o = Wire.PrefixSize;
            return new MsgInvenMove
            {
                AmorPocket = Wire.S8(d, o + 0),
                PantsPocket = Wire.S8(d, o + 1),
                BackPackExtend = Wire.S8(d, o + 2),
                Flag = Wire.S8(d, o + 3),
                From = Wire.U8(d, o + 4),
                To = Wire.U8(d, o + 5),
                SendCount = Wire.U8(d, o + 6),
            };
        }
    }

    /// <summary>One inventory row inside INVEN_INFO. The 10-byte common prefix is always
    /// present; equip rows (weapon/cloth) carry a further 73 bytes of refine/socket data,
    /// kept verbatim in <see cref="EquipTail"/> so the packet round-trips byte-exact.</summary>
    public sealed class InvenItem
    {
        public byte   Slot;           // @0  0..149
        public byte   Type;           // @1  major category (decides stride)
        public sbyte  Second;         // @2  sub category
        public ushort Id;             // @3-4 item id (LE)
        public ushort CurDur;         // @5-6 current durability (LE)
        public byte   SocketCount;    // @7
        public byte   Count;          // @8  stack count
        public byte   IncludeLimit;   // @9  1 = has use-limit block
        public byte[] EquipTail;      // 73 extra bytes for equip rows (weapon/cloth); null otherwise

        public bool IsEquip => InvItemType.IsEquip(Type);
        public int  Stride  => InvItemType.RowStride(Type);

        public int Write(Span<byte> d, int o)
        {
            Wire.U8(d, o + 0, Slot);
            Wire.U8(d, o + 1, Type);
            Wire.S8(d, o + 2, Second);
            Wire.U16(d, o + 3, Id);
            Wire.U16(d, o + 5, CurDur);
            Wire.U8(d, o + 7, SocketCount);
            Wire.U8(d, o + 8, Count);
            Wire.U8(d, o + 9, IncludeLimit);
            int stride = Stride;
            for (int i = 10; i < stride; i++)
                d[o + i] = (EquipTail != null && (i - 10) < EquipTail.Length) ? EquipTail[i - 10] : (byte)0;
            return stride;
        }

        public static InvenItem Read(ReadOnlySpan<byte> d, int o)
        {
            var it = new InvenItem
            {
                Slot = Wire.U8(d, o + 0),
                Type = Wire.U8(d, o + 1),
                Second = Wire.S8(d, o + 2),
                Id = Wire.U16(d, o + 3),
                CurDur = Wire.U16(d, o + 5),
                SocketCount = Wire.U8(d, o + 7),
                Count = Wire.U8(d, o + 8),
                IncludeLimit = Wire.U8(d, o + 9),
            };
            if (it.IsEquip)
            {
                it.EquipTail = new byte[InvItemType.RowEquip - 10];
                for (int i = 0; i < it.EquipTail.Length; i++) it.EquipTail[i] = d[o + 10 + i];
            }
            return it;
        }
    }

    // --- 121 INVEN_INFO (S->C) : inventory contents, VARIABLE length ---
    //  Header block (12 bytes): mode + amor/pants pocket + backpack extend + money(u32) + itemCount,
    //  then `itemCount` rows of 10 or 83 bytes each (see InvenItem).
    public sealed class MsgInvenInfo : NdPacket
    {
        public const int HeaderBlock = 12;   // header(2)+msg(1)+mode(1)+amor(1)+pants(1)+back(1)+money(4)+count(1)

        // ucInvenMode (native EN_INVEN_INFO_MODE + ND extensions). Opcode 121 is REUSED: only 1/2 are item lists.
        public const byte ModeElixir = 0, ModeInventory = 1, ModeAppend = 2, ModeCostume = 6, ModeCtmResult = 7;

        public byte InvenMode = 1;
        public byte AmorPocket;
        public byte PantsPocket;
        public byte BackPackExtend;
        public uint Money;
        public List<InvenItem> Items = new List<InvenItem>();

        public override int MsgId => Nd.Packets.MsgId.InvenInfo;
        public override int WireSize
        {
            get
            {
                int n = HeaderBlock;
                foreach (var it in Items) n += it.Stride;
                return n;
            }
        }

        public override int Write(Span<byte> d)
        {
            int size = WireSize;
            new NdHeader(size).Write(d);
            Wire.U8(d, 2, (byte)MsgId);
            Wire.U8(d, 3, InvenMode);
            Wire.U8(d, 4, AmorPocket);
            Wire.U8(d, 5, PantsPocket);
            Wire.U8(d, 6, BackPackExtend);
            Wire.U32(d, 7, Money);
            Wire.U8(d, 11, (byte)Items.Count);
            int o = HeaderBlock;
            foreach (var it in Items) o += it.Write(d, o);
            return o;
        }

        public static MsgInvenInfo Parse(ReadOnlySpan<byte> d)
        {
            var p = new MsgInvenInfo { InvenMode = Wire.U8(d, 3) };
            // Opcode 121 is reused for several bodies (elixir 0, inventory 1, append 2, costume 6, result 7).
            // ONLY the inventory (1) and append (2) modes carry money+count+items at these offsets; the others
            // put a different struct here. Reading them as an item list gives garbage money + 0 items — and the
            // costume packet arriving AFTER the inventory then wiped the real bag. Parse items for 1/2 only.
            if (p.InvenMode != ModeInventory && p.InvenMode != ModeAppend) return p;
            p.AmorPocket = Wire.U8(d, 4);
            p.PantsPocket = Wire.U8(d, 5);
            p.BackPackExtend = Wire.U8(d, 6);
            p.Money = Wire.U32(d, 7);
            int count = Wire.U8(d, 11);
            int o = HeaderBlock;
            for (int i = 0; i < count; i++)
            {
                if (o + 10 > d.Length) break;   // truncated frame guard (native ND row-truncation check)
                var it = InvenItem.Read(d, o);
                p.Items.Add(it);
                o += it.Stride;
            }
            return p;
        }
    }
}
