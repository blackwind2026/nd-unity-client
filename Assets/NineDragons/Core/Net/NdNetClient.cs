// ============================================================================
//  NdNetClient.cs - M1 public interface. Speaks the ninedragons-server wire
//  protocol end to end: login (48300) -> GS (25019) -> char list -> select ->
//  CHARAC_INFO. Server always replies PLAIN (no tail, no crypto), so the client
//  only encrypts what it SENDS.
//
//  Public surface for M2/M5:
//    LoadLump(path)
//    LoginSuccess Login(host, port, id, pw)
//    bool        EnterGameServer(host, port, id, uid)
//    CharacList  RequestCharacterList()
//    CharacInfo  SelectCharacter(name)
//    void        SendGameStartSignal()
// ============================================================================
using System;
using System.IO;
using System.Net.Sockets;

namespace Nd.Net
{
    public sealed class NdNetClient : IDisposable
    {
        private NdCrypto _master;

        // GS connection kept open after EnterGameServer for list/select.
        private TcpClient _gs;
        private NetworkStream _gsStream;
        private NdCrypto _gsCrypto;

        public LoginSuccess LastLogin { get; private set; }
        public Action<string> Log = _ => { };

        public void LoadLump(string path)
        {
            _master = new NdCrypto();
            if (!_master.LoadLump(path))
                throw new IOException("failed to load lump.dat: " + path);
        }

        // -------------------------------------------------- login (48300) -----
        public LoginSuccess Login(string host, int port, string id, string pw)
        {
            if (_master == null) throw new InvalidOperationException("call LoadLump first");
            using (var tc = new TcpClient())
            {
                tc.NoDelay = true;
                tc.Connect(host, port);
                var st = tc.GetStream();
                st.ReadTimeout = 15000;

                // S->C MSG_SEQUENCE_NUMBER: 20 bytes (msg@2=0, startSeq@3, challenge@4..19)
                byte[] seq = ReadPacket(st);
                if (seq.Length < 20 || seq[2] != Msg.SEQUENCE_NUMBER)
                    throw new IOException("login: unexpected seq packet (len=" + seq.Length + " msg=" + (seq.Length > 2 ? seq[2] : -1) + ")");
                byte startSeq = seq[3];
                byte[] challenge = new byte[16];
                Array.Copy(seq, 4, challenge, 0, 16);
                Log($"login: startSeq={startSeq} challenge={Hex(challenge, 16)}");

                var cr = _master.Clone();
                cr.SetSeq(startSeq);
                cr.SetPattern(startSeq);

                // C->S MSG_LOGIN (encrypted, forceCrypto like the real client).
                byte[] passport = NdPassport.Encode(challenge, id, pw);
                byte[] login = PacketBuilder.Login(passport);
                int total = cr.Encapsulate(login, 214, forceCrypto: true);
                st.Write(login, 0, total);
                Log($"login: sent MSG_LOGIN ({total} bytes) id='{id}'");

                // S->C response: LOGIN_SUCCESS(3) or LOGIN_FAIL(4).
                byte[] resp = ReadPacket(st);
                byte msg = resp[2];
                if (msg == Msg.LOGIN_FAIL)
                    throw new IOException("LOGIN_FAIL errorCode=" + (resp.Length > 3 ? resp[3] : -1));
                if (msg != Msg.LOGIN_SUCCESS)
                    throw new IOException("login: unexpected response msg=" + msg);

                var ok = LoginSuccess.Parse(resp);
                LastLogin = ok;
                Log($"login: SUCCESS uid={ok.Uid} account='{ok.Account}' GS={ok.IpString()}:{ok.ServerPort}");
                return ok;
            }
        }

        // -------------------------------------------------- GS (25019) --------
        public bool EnterGameServer(string host, int port, string id, ushort uid)
        {
            if (_master == null) throw new InvalidOperationException("call LoadLump first");
            _gs = new TcpClient { NoDelay = true };
            _gs.Connect(host, port);
            _gsStream = _gs.GetStream();
            _gsStream.ReadTimeout = 20000;

            // S->C MSG_SEQ_GS: 4 bytes (msg@2=0, startSeq@3)
            byte[] seq = ReadPacket(_gsStream);
            if (seq.Length < 4 || seq[2] != Msg.SEQUENCE_NUMBER)
                throw new IOException("GS: unexpected seq packet (len=" + seq.Length + ")");
            byte startSeq = seq[3];
            _gsCrypto = _master.Clone();
            _gsCrypto.SetSeq(startSeq);
            _gsCrypto.SetPattern(startSeq);
            Log($"gs: startSeq={startSeq}");

            // C->S MSG_SV_ENTER_GSERVER
            SendGs(PacketBuilder.EnterGServer(id, uid), 70);
            Log($"gs: sent SV_ENTER_GSERVER id='{id}' uid={uid}");

            // S->C MSG_SV_ENTER_RESULT (154): cResult@3 (0 = accepted)
            byte[] r = ReadPacket(_gsStream);
            if (r[2] != Msg.SV_ENTER_RESULT)
                throw new IOException("gs: expected ENTER_RESULT, got msg=" + r[2]);
            sbyte result = (sbyte)r[3];
            Log($"gs: ENTER_RESULT cResult={result}");
            return result == 0;
        }

        public CharacList RequestCharacterList()
        {
            RequireGs();
            SendGs(PacketBuilder.CharacReqList(), 3);
            Log("gs: sent CHARAC_REQ_LIST");
            byte[] r = ReadPacket(_gsStream);
            if (r[2] != Msg.CHARAC_LIST)
                throw new IOException("gs: expected CHARAC_LIST, got msg=" + r[2]);
            var list = CharacList.Parse(r);
            Log($"gs: CHARAC_LIST count={list.Count}");
            return list;
        }

        public CharacInfo SelectCharacter(string name)
        {
            RequireGs();
            SendGs(PacketBuilder.CharacSelect(name), 16);
            Log($"gs: sent CHARAC_SELECT '{name}'");

            // Expect CHARAC_INFO(16). Skip any interleaved ZONE_POSITIONING(25).
            for (int i = 0; i < 8; i++)
            {
                byte[] r = ReadPacket(_gsStream);
                if (r[2] == Msg.CHARAC_INFO)
                {
                    var info = CharacInfo.Parse(r);
                    Log($"gs: CHARAC_INFO received ({r.Length} bytes)");
                    return info;
                }
                Log($"gs: (skipping msg={r[2]} len={r.Length} while waiting for CHARAC_INFO)");
            }
            throw new IOException("gs: CHARAC_INFO not received after CHARAC_SELECT");
        }

        public void SendGameStartSignal()
        {
            RequireGs();
            SendGs(PacketBuilder.GameStartSignal(), 3);
            Log("gs: sent GAMESTARTSIGNAL");
        }

        // -------------------------------------------------- internals ---------
        private void RequireGs()
        {
            if (_gsStream == null) throw new InvalidOperationException("not connected to GS");
        }

        private void SendGs(byte[] body, int bodyLen)
        {
            int total = _gsCrypto.Encapsulate(body, bodyLen, forceCrypto: false);
            _gsStream.Write(body, 0, total);
        }

        // Read one PLAIN framed packet: 2-byte header + (wireLength-2) body.
        private static byte[] ReadPacket(NetworkStream st)
        {
            byte[] head = ReadExact(st, 2);
            int total = NdCrypto.WireLength(head);
            if (total < 2 || total > 8192) throw new IOException("bad wire length " + total);
            byte[] buf = new byte[total];
            buf[0] = head[0]; buf[1] = head[1];
            if (total > 2)
            {
                byte[] rest = ReadExact(st, total - 2);
                Array.Copy(rest, 0, buf, 2, total - 2);
            }
            return buf;
        }

        private static byte[] ReadExact(NetworkStream st, int n)
        {
            byte[] b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = st.Read(b, got, n - got);
                if (r <= 0) throw new IOException("connection closed (wanted " + n + ", got " + got + ")");
                got += r;
            }
            return b;
        }

        private static string Hex(byte[] b, int n)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n && i < b.Length; i++) sb.Append(b[i].ToString("X2"));
            return sb.ToString();
        }

        public void Dispose()
        {
            try { _gsStream?.Dispose(); } catch { }
            try { _gs?.Close(); } catch { }
        }
    }
}
