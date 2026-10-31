// ============================================================================
//  NdPackets.cs - build outgoing / parse incoming 9D packets for M1.
//  Layouts mirror nd-world/src/nd_gs_packets.h and nd-login/src/nd_packets.h
//  (all #pragma pack(1)). Byte offsets are annotated against those structs.
//
//  Header = 2 bytes: sLength:12 | sCrypto:2 | sCompressed:2 (little-endian).
//  ucMessage always sits at offset 2. Outgoing bodies are built with
//  sLength=bodyLen in the header; NdCrypto.Encapsulate() rewrites length +
//  appends the crc/seq tail.
// ============================================================================
using System;
using System.Text;

namespace Nd.Net
{
    public static class Msg
    {
        // login
        public const byte SEQUENCE_NUMBER = 0;
        public const byte LOGIN = 1;
        public const byte LOGIN_SUCCESS = 3;
        public const byte LOGIN_FAIL = 4;
        // GS
        public const byte SV_ENTER_GSERVER = 153;
        public const byte SV_ENTER_RESULT = 154;
        public const byte CHARAC_REQ_LIST = 8;
        public const byte CHARAC_LIST = 9;
        public const byte CHARAC_SELECT = 10;
        public const byte CHARAC_INFO = 16;
        public const byte ZONE_POSITIONING = 25;
        public const byte GAMESTARTSIGNAL = 20;
    }

    // ---- outgoing packet bodies (header + payload, no tail; Encapsulate adds tail) ----
    public static class PacketBuilder
    {
        // Write the 2-byte header carrying sLength=bodyLen (crypto flag cleared).
        private static void PutHeader(byte[] buf, int bodyLen)
        {
            buf[0] = (byte)(bodyLen & 0xFF);
            buf[1] = (byte)((bodyLen >> 8) & 0x0F);
        }

        // MSG_LOGIN (214-byte body). passport must be 200 bytes.
        public static byte[] Login(byte[] passport, int cap = 256)
        {
            int bodyLen = 214;
            byte[] b = new byte[Math.Max(cap, bodyLen + NdCrypto.TAIL_SIZE)];
            PutHeader(b, bodyLen);
            b[2] = Msg.LOGIN;                 // ucMessage
            // uiLMBC_Code@3(4)=0, sVersionIndex@7(2)=0, dwClientVer@9(4)=0, cForestSelectNum@13(1)=0
            Array.Copy(passport, 0, b, 14, 200); // passport@14
            return b;
        }

        // MSG_SV_ENTER_GSERVER (70-byte body).
        public static byte[] EnterGServer(string id, ushort uid)
        {
            int bodyLen = 70;
            byte[] b = new byte[bodyLen + NdCrypto.TAIL_SIZE];
            PutHeader(b, bodyLen);
            b[2] = Msg.SV_ENTER_GSERVER;
            byte[] idB = Encoding.ASCII.GetBytes(id ?? "");
            int n = Math.Min(idB.Length, 60);
            Array.Copy(idB, 0, b, 3, n);      // ID[61]@3
            // ulEnterCheck@64(4)=0
            b[68] = (byte)(uid & 0xFF);       // usUniqueID@68
            b[69] = (byte)((uid >> 8) & 0xFF);
            return b;
        }

        // MSG_CHARAC_REQ_LIST (3-byte body).
        public static byte[] CharacReqList()
        {
            int bodyLen = 3;
            byte[] b = new byte[bodyLen + NdCrypto.TAIL_SIZE];
            PutHeader(b, bodyLen);
            b[2] = Msg.CHARAC_REQ_LIST;
            return b;
        }

        // MSG_CHARAC_SELECT (16-byte body).
        public static byte[] CharacSelect(string name)
        {
            int bodyLen = 16;
            byte[] b = new byte[bodyLen + NdCrypto.TAIL_SIZE];
            PutHeader(b, bodyLen);
            b[2] = Msg.CHARAC_SELECT;
            byte[] nm = Encoding.ASCII.GetBytes(name ?? "");
            int n = Math.Min(nm.Length, 12);
            Array.Copy(nm, 0, b, 3, n);       // cSelectName[13]@3
            return b;
        }

        // MSG_CHARAC_GAMESTARTSIGNAL (3-byte body; server only checks msg id).
        public static byte[] GameStartSignal()
        {
            int bodyLen = 3;
            byte[] b = new byte[bodyLen + NdCrypto.TAIL_SIZE];
            PutHeader(b, bodyLen);
            b[2] = Msg.GAMESTARTSIGNAL;
            return b;
        }
    }

    // ---- little-endian readers ----
    internal static class LE
    {
        public static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        public static short S16(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8));
        public static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        public static int S32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        public static float F32(byte[] b, int o) => BitConverter.ToSingle(b, o);
        public static string Str(byte[] b, int o, int max)
        {
            int n = 0; while (n < max && b[o + n] != 0) n++;
            return Encoding.ASCII.GetString(b, o, n);
        }
    }

    // MSG_LOGIN_SUCCESS (pack(1)): ucMessage@2, usServerPort@3, ulServerIP@5,
    //  usMessengerPort@9, ulMessengerIP@11, usUniqueID@15, cNDAccount[25]@17.
    public sealed class LoginSuccess
    {
        public ushort ServerPort;
        public uint ServerIP;
        public ushort Uid;
        public string Account;

        public static LoginSuccess Parse(byte[] b)
        {
            return new LoginSuccess
            {
                ServerPort = LE.U16(b, 3),
                ServerIP = LE.U32(b, 5),
                Uid = LE.U16(b, 15),
                Account = LE.Str(b, 17, 25),
            };
        }

        public string IpString()
        {
            return string.Format("{0}.{1}.{2}.{3}",
                ServerIP & 0xFF, (ServerIP >> 8) & 0xFF, (ServerIP >> 16) & 0xFF, (ServerIP >> 24) & 0xFF);
        }
    }

    // One entry of MSG_CHARAC_LIST (GS_CharacList, 88 bytes).
    public sealed class CharacListEntry
    {
        public int UniqueID;
        public string Name;
        public string Nick;
        public sbyte Zone;
        public sbyte Sex, Face, Hair, Group;   // model appearance (offsets 31-34)
        public short Amor, Pants, Cap;          // equipped cloth item ids (offsets 43/45/47)
        public short Adae, Shoes;                // 아대 gloves (offset 49) + giày shoes (offset 51)
        public short Weapon;                     // SelectWeaponId (offset 55)
        public short Str, Zen, Intelligence, Constitution, Dexterity;
        public short InnerLevel;
        public sbyte Class, ClassGrade, Gm;
        public sbyte WeaponType;   // object-type vũ khí đang mặc (offset 41): 0 thường / 15 cash / 19 ngoại trang
        public float PosX, PosZ;

        public static CharacListEntry Parse(byte[] b, int o)
        {
            return new CharacListEntry
            {
                UniqueID = LE.S32(b, o + 0),
                Name = LE.Str(b, o + 4, 13),
                Nick = LE.Str(b, o + 17, 13),
                Zone = (sbyte)b[o + 30],
                Sex = (sbyte)b[o + 31],
                Face = (sbyte)b[o + 32],
                Hair = (sbyte)b[o + 33],
                Group = (sbyte)b[o + 34],
                Amor = LE.S16(b, o + 43),
                Pants = LE.S16(b, o + 45),
                Cap = LE.S16(b, o + 47),
                Adae = LE.S16(b, o + 49),
                Shoes = LE.S16(b, o + 51),
                WeaponType = (sbyte)b[o + 41],   // type vũ khí (GsCharacList offset 41) — cần để hiện đúng 15/19
                Weapon = LE.S16(b, o + 55),
                Str = LE.S16(b, o + 60),
                Zen = LE.S16(b, o + 62),
                Intelligence = LE.S16(b, o + 64),
                Constitution = LE.S16(b, o + 66),
                Dexterity = LE.S16(b, o + 68),
                InnerLevel = LE.S16(b, o + 70),
                Class = (sbyte)b[o + 72],
                ClassGrade = (sbyte)b[o + 73],
                Gm = (sbyte)b[o + 74],
                PosX = LE.F32(b, o + 75),
                PosZ = LE.F32(b, o + 79),
            };
        }
    }

    // MSG_CHARAC_LIST (S->C).
    public sealed class CharacList
    {
        public int Count;
        public CharacListEntry[] Entries;
        public uint StateFlag;

        public static CharacList Parse(byte[] b)
        {
            int n = (sbyte)b[3];
            if (n < 0) n = 0;
            if (n > 3) n = 3;
            var e = new CharacListEntry[n];
            for (int i = 0; i < n; i++) e[i] = CharacListEntry.Parse(b, 4 + i * 88);
            return new CharacList { Count = n, Entries = e, StateFlag = LE.U32(b, 4 + 3 * 88) };
        }
    }

    // MSG_CHARAC_INFO (S->C, 133 bytes) - the M1 success payload.
    public sealed class CharacInfo
    {
        public sbyte Zone;
        public int CurLife, CurForce, CurConc;
        public int MaxLife, MaxForce, MaxConc;
        public short LeftPoint, LeftMasteryPoint;
        public uint LeftJin;   // EXP
        public uint LeftGong;
        public short Retribution;
        public int Honor;
        public short Showdown;
        public float InitPosX, InitPosZ;
        public int TimeCode;
        public short StandingOBB;
        public int WoundRate, InsideWoundRate;
        public ushort FatigueRate;
        public short FuryParameter;
        public sbyte RespawnServerNo;
        public string RespawnPosName;
        public sbyte Class, ClassGrade, Gm;
        public int Contribution;
        public int LeftLevelupLimit;
        public byte ModeBits;

        public static CharacInfo Parse(byte[] b)
        {
            return new CharacInfo
            {
                Zone = (sbyte)b[3],
                CurLife = LE.S32(b, 4),
                CurForce = LE.S32(b, 8),
                CurConc = LE.S32(b, 12),
                MaxLife = LE.S32(b, 16),
                MaxForce = LE.S32(b, 20),
                MaxConc = LE.S32(b, 24),
                LeftPoint = LE.S16(b, 28),
                LeftMasteryPoint = LE.S16(b, 30),
                LeftJin = LE.U32(b, 32),
                LeftGong = LE.U32(b, 36),
                Retribution = LE.S16(b, 40),
                Honor = LE.S32(b, 42),
                Showdown = LE.S16(b, 46),
                InitPosX = LE.F32(b, 48),
                InitPosZ = LE.F32(b, 52),
                TimeCode = LE.S32(b, 56),
                StandingOBB = LE.S16(b, 60),
                WoundRate = LE.S32(b, 62),
                InsideWoundRate = LE.S32(b, 66),
                FatigueRate = LE.U16(b, 70),
                FuryParameter = LE.S16(b, 72),
                RespawnServerNo = (sbyte)b[74],
                RespawnPosName = LE.Str(b, 75, 13),
                Class = (sbyte)b[117],
                ClassGrade = (sbyte)b[118],
                Gm = (sbyte)b[123],
                Contribution = LE.S32(b, 119),
                LeftLevelupLimit = LE.S32(b, 124),
                ModeBits = b[128],
            };
        }
    }
}
