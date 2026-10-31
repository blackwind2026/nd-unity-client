// ============================================================================
//  NdWorldConnection.cs - M5 transport for the in-world phase.
//
//  Reuses the M1 TRANSPORT PRIMITIVES verbatim (do NOT reimplement crypto):
//    - NdCrypto   : keybox/pattern, Encapsulate (send tail), WireLength
//    - NdPassport : HOTFIX9 login passport
//    - PacketBuilder / Msg / LoginSuccess / CharacList / CharacInfo (M1 parsers)
//
//  The byte transport itself is now behind INdTransport (see Nd.Net): TcpTransport
//  for desktop/editor/mobile (unchanged behaviour) and WebSocketTransport for
//  WebGL / the WS<->TCP bridge. NdTransportFactory.Create() picks the right one by
//  platform. Framing (2-byte header + WireLength body) lives in the transport;
//  this class only speaks the handshake and the crypto tail on SEND.
//
//  Framing recap: server->client is PLAIN (read len from the 2-byte header, parse
//  from offset 2, never touch the seq); client->server goes through
//  NdCrypto.Encapsulate (crc+seq tail, seq advances only on SEND).
// ============================================================================
using System;
using System.IO;
using Nd.Net;
using Nd.Packets;

namespace Nd.Session
{
    internal sealed class NdWorldConnection : IDisposable
    {
        private readonly NdCrypto _master;

        private INdTransport _gs;
        private NdCrypto _gsCrypto;

        public Action<string> Log = _ => { };
        public ushort Uid { get; private set; }

        public NdWorldConnection(string lumpPath)
        {
            _master = new NdCrypto();
            if (!_master.LoadLump(lumpPath))
                throw new IOException("failed to load lump.dat: " + lumpPath);
        }

        // -------------------------------------------------- login (48300) -----
        //  Byte-identical to the original (reused primitives), throwaway transport.
        public LoginSuccess Login(string host, int port, string id, string pw)
        {
            using (var tc = NdTransportFactory.Create())
            {
                tc.Connect(host, port);

                byte[] seq = tc.ReadFrame(15000);
                if (seq.Length < 20 || seq[2] != Msg.SEQUENCE_NUMBER)
                    throw new IOException("login: unexpected seq packet (len=" + seq.Length + ")");
                byte startSeq = seq[3];
                var challenge = new byte[16];
                Array.Copy(seq, 4, challenge, 0, 16);

                var cr = _master.Clone();
                cr.SetSeq(startSeq); cr.SetPattern(startSeq);

                byte[] passport = NdPassport.Encode(challenge, id, pw);
                byte[] login = PacketBuilder.Login(passport);
                int total = cr.Encapsulate(login, 214, forceCrypto: true);
                tc.Send(login, total);

                byte[] resp = tc.ReadFrame(15000);
                if (resp[2] == Msg.LOGIN_FAIL)
                    throw new IOException("LOGIN_FAIL errorCode=" + (resp.Length > 3 ? resp[3] : -1)
                                          + " (1=baduser 4=badpw 5=already-online)");
                if (resp[2] != Msg.LOGIN_SUCCESS)
                    throw new IOException("login: unexpected response msg=" + resp[2]);

                var ok = LoginSuccess.Parse(resp);
                Uid = ok.Uid;
                Log($"login OK uid={ok.Uid} account='{ok.Account}' gs={ok.IpString()}:{ok.ServerPort}");
                return ok;
            }
        }

        // -------------------------------------------------- GS (25019) --------
        public bool EnterGameServer(string host, int port, string id, ushort uid)
        {
            _gs = NdTransportFactory.Create();
            _gs.Connect(host, port);

            byte[] seq = _gs.ReadFrame(20000);
            if (seq.Length < 4 || seq[2] != Msg.SEQUENCE_NUMBER)
                throw new IOException("GS: unexpected seq packet (len=" + seq.Length + ")");
            _gsCrypto = _master.Clone();
            _gsCrypto.SetSeq(seq[3]); _gsCrypto.SetPattern(seq[3]);
            Log($"gs startSeq={seq[3]}");

            SendRaw(PacketBuilder.EnterGServer(id, uid), 70);
            byte[] r = _gs.ReadFrame(20000);
            if (r[2] != Msg.SV_ENTER_RESULT)
                throw new IOException("gs: expected ENTER_RESULT, got msg=" + r[2]);
            sbyte res = (sbyte)r[3];
            Log($"gs ENTER_RESULT cResult={res}");
            return res == 0;
        }

        public CharacList RequestCharacterList()
        {
            RequireGs();
            SendRaw(PacketBuilder.CharacReqList(), 3);
            byte[] r = _gs.ReadFrame(20000);
            if (r[2] != Msg.CHARAC_LIST)
                throw new IOException("gs: expected CHARAC_LIST, got msg=" + r[2]);
            return CharacList.Parse(r);
        }

        // Select + return CHARAC_INFO. Any interleaved frames before it are handed
        // to `onOther` (so an early ZONE_POSITIONING etc. is not lost).
        public CharacInfo SelectCharacter(string name, Action<byte[]> onOther)
        {
            RequireGs();
            SendRaw(PacketBuilder.CharacSelect(name), 16);
            for (int i = 0; i < 16; i++)
            {
                byte[] r = _gs.ReadFrame(20000);
                if (r[2] == Msg.CHARAC_INFO) return CharacInfo.Parse(r);
                onOther?.Invoke(r);
            }
            throw new IOException("gs: CHARAC_INFO not received after CHARAC_SELECT");
        }

        public void SendGameStartSignal()
        {
            RequireGs();
            SendRaw(PacketBuilder.GameStartSignal(), 3);
        }

        // ---- generic typed send (ZONE_MOVE, STATE_CHANGE, MARTIAL_FIRE, ...) ----
        public void Send(INdPacket p)
        {
            RequireGs();
            int wire = p.WireSize;
            byte[] buf = new byte[wire + NdCrypto.TAIL_SIZE];
            p.Write(buf);
            int total = _gsCrypto.Encapsulate(buf, wire, forceCrypto: false);
            _gs.Send(buf, total);
        }

        // ---- receive one PLAIN frame if the GS socket is readable within timeoutMs.
        //      Returns null on timeout, throws on close. ----
        public byte[] TryReadFrame(int timeoutMs)
        {
            RequireGs();
            return _gs.TryReadFrame(timeoutMs);
        }

        // -------------------------------------------------- internals ---------
        private void RequireGs()
        {
            if (_gs == null) throw new InvalidOperationException("not connected to GS");
        }

        private void SendRaw(byte[] body, int bodyLen)
        {
            int total = _gsCrypto.Encapsulate(body, bodyLen, forceCrypto: false);
            _gs.Send(body, total);
        }

        public void Dispose()
        {
            try { _gs?.Dispose(); } catch { }
        }
    }
}
