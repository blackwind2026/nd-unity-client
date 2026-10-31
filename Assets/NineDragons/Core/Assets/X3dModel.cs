using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace NineDragons.Assets
{
    /// <summary>
    /// Parser for the client's <c>.X3D</c> mesh container and <c>.XBP</c> biped-skeleton files
    /// (both live in <c>MR_BASEMODEL.XP</c>; a few also in <c>AR_BASEANIMATION.XP</c>).
    ///
    /// Ground truth: <c>MeshControl\XMeshContainer.cpp</c> <c>_XMeshContainer::Load(FILE*)</c>
    /// (static, versions 0.72 + 0.73/bakemap), <c>XAnimMeshContainer.cpp</c>
    /// <c>_XVSAnimMeshContainer::Load</c> (skinned, 0.72 only — same bytes on disk) and
    /// <c>XBipedController.cpp</c> <c>_XBipedController::Load</c> (.xbp — same container whose
    /// nodes are all "Bip*", i.e. no geometry blocks). Structs: <c>XKernel\XModelContainerDef.h</c>
    /// (<c>_XMESHHEADER</c>), <c>XKernel\XMeshMath.h</c> (<c>_XMESH_VERTEX[_UV2]</c>).
    ///
    /// On-disk layout (little-endian, byte-packed except where noted):
    /// <code>
    ///   _XMESHHEADER (68 B):
    ///     u32   indicator      = 0x0000AE01
    ///     f32   version        = 0.72 | 0.73          (0.73 adds per-node bakemap name)
    ///     f32x3 obbCenter
    ///     f32x9 obbAxis[3]
    ///     f32x3 obbExtent
    ///   i32 nodeCount
    ///   node * nodeCount:
    ///     i32 id
    ///     i32 nameLen,   u8 name[nameLen]             (no NUL; CP949 possible)
    ///     i32 parentId
    ///     i32 pnameLen,  u8 parentName[pnameLen]      (client skips it)
    ///     f32x16 matrix                                (D3DXMATRIX, row-major)
    ///     i32 texLen,    u8 textureName[texLen]
    ///     [version >= 0.73] i32 bakeLen, u8 bakemapName[bakeLen]
    ///     — geometry only when name does NOT start with "Bip" (strncmp, case-sensitive):
    ///     i32 vertexCount
    ///       vertex * vertexCount: pos f32x3, normal f32x3, uv f32x2  (32 B)
    ///                             + uv2 f32x2 when bakemap name non-empty (40 B)
    ///     u32 indexCount                               (raw WORD count; 3 per triangle)
    ///       u16 * indexCount
    ///     i32 skinCount                                (0 = rigid; else == vertexCount)
    ///       skin * skinCount: i32 factorCnt, factorCnt * _XM_FACTOR
    ///         _XM_FACTOR is 8 B on disk: f32 weight, u16 boneId, u16 pad
    ///         (struct { FLOAT; WORD; } with default MSVC alignment — exporter fwrote the
    ///          padded struct; verified by exact-length consumption over the full archive)
    ///       — only when skinCount > 0:
    ///       i32 boneMapCount, u16 boneMap[boneMapCount]
    /// </code>
    /// </summary>
    public sealed class X3dModel
    {
        public const uint Indicator = 0x0000AE01;   // _XDEF_X3DFILEINDICATOR

        public float Version { get; private set; }
        public Vector3 ObbCenter { get; private set; }
        public Vector3[] ObbAxis { get; private set; }
        public float[] ObbExtent { get; private set; }

        public IReadOnlyList<X3dNode> Nodes => _nodes;
        private X3dNode[] _nodes;

        /// <summary>Bytes left unread after the last node (0 for every sampled real file).</summary>
        public int TrailingBytes { get; private set; }

        /// <summary>True when no node carries geometry (the .xbp skeleton files).</summary>
        public bool IsSkeletonOnly
        {
            get
            {
                foreach (var n in _nodes)
                    if (n.Vertices != null && n.Vertices.Length > 0) return false;
                return true;
            }
        }

        public static X3dModel Parse(byte[] data, string name = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var r = new SpanReader(data, name ?? "X3D");

            uint indicator = r.U32();
            if (indicator != Indicator)
                throw new InvalidDataException($"{r.Source}: bad indicator 0x{indicator:X8} (want 0x{Indicator:X8})");

            var m = new X3dModel { Version = r.F32() };
            // client accepts exactly 0.72f / 0.73f (float equality); tolerate tiny fp noise
            bool v72 = Math.Abs(m.Version - 0.72f) < 1e-4f;
            bool v73 = Math.Abs(m.Version - 0.73f) < 1e-4f;
            if (!v72 && !v73)
                throw new InvalidDataException($"{r.Source}: unsupported version {m.Version}");

            m.ObbCenter = r.Vec3();
            m.ObbAxis = new[] { r.Vec3(), r.Vec3(), r.Vec3() };
            m.ObbExtent = new[] { r.F32(), r.F32(), r.F32() };

            int nodeCount = r.I32();
            if (nodeCount < 0 || nodeCount > 4096)
                throw new InvalidDataException($"{r.Source}: implausible node count {nodeCount}");

            var nodes = new X3dNode[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                var n = new X3dNode();
                n.Id = r.I32();
                n.Name = r.LenString();
                n.ParentId = r.I32();
                n.ParentName = r.LenString();
                n.Matrix = r.Mat4();
                n.TextureName = r.LenString();
                if (v73) n.BakemapName = r.LenString();

                // client: strncmp(name, "Bip", 3) != 0 → geometry follows
                n.IsBiped = n.Name.Length >= 3 && n.Name[0] == 'B' && n.Name[1] == 'i' && n.Name[2] == 'p';
                if (!n.IsBiped)
                {
                    bool uv2 = !string.IsNullOrEmpty(n.BakemapName);

                    int vcnt = r.I32();
                    if (vcnt < 0 || vcnt > 1_000_000)
                        throw new InvalidDataException($"{r.Source}: node {i} implausible vertex count {vcnt}");
                    if (vcnt > 0)
                    {
                        n.Vertices = new X3dVertex[vcnt];
                        for (int v = 0; v < vcnt; v++)
                        {
                            n.Vertices[v].Position = r.Vec3();
                            n.Vertices[v].Normal = r.Vec3();
                            n.Vertices[v].Uv = new Vector2(r.F32(), r.F32());
                            if (uv2) n.Vertices[v].Uv2 = new Vector2(r.F32(), r.F32());
                        }
                    }

                    uint icnt = r.U32();
                    if (icnt > 3_000_000)
                        throw new InvalidDataException($"{r.Source}: node {i} implausible index count {icnt}");
                    n.Indices = new ushort[icnt];
                    for (int k = 0; k < icnt; k++) n.Indices[k] = r.U16();

                    int scnt = r.I32();
                    if (scnt < 0 || scnt > 1_000_000)
                        throw new InvalidDataException($"{r.Source}: node {i} implausible skin count {scnt}");
                    if (scnt > 0)
                    {
                        n.Skin = new X3dSkinFactor[scnt][];
                        for (int s = 0; s < scnt; s++)
                        {
                            int fcnt = r.I32();
                            if (fcnt < 0 || fcnt > 64)
                                throw new InvalidDataException($"{r.Source}: node {i} vertex {s} implausible factor count {fcnt}");
                            var fac = new X3dSkinFactor[fcnt];
                            for (int f = 0; f < fcnt; f++)
                            {
                                fac[f].Weight = r.F32();
                                fac[f].BoneId = r.U16();
                                r.U16();                     // struct padding written by the exporter
                            }
                            n.Skin[s] = fac;
                        }

                        int bcnt = r.I32();
                        if (bcnt < 0 || bcnt > 4096)
                            throw new InvalidDataException($"{r.Source}: node {i} implausible bone map count {bcnt}");
                        n.BoneMap = new ushort[bcnt];
                        for (int b = 0; b < bcnt; b++) n.BoneMap[b] = r.U16();
                    }
                }
                nodes[i] = n;
            }

            m._nodes = nodes;
            m.TrailingBytes = r.Remaining;
            return m;
        }
    }

    /// <summary>One object/bone node in an .X3D/.XBP container.</summary>
    public sealed class X3dNode
    {
        public int Id;
        public int ParentId;                 // -1 = root; index into Nodes
        public string Name = "";
        public string ParentName = "";
        public string TextureName = "";      // looked up in the texture packages (no extension logic here)
        public string BakemapName = "";      // version 0.73 only; non-empty → vertices carry Uv2
        public Matrix4x4 Matrix;             // object matrix as stored (row-major, D3D convention)
        public bool IsBiped;                 // name starts with "Bip" → no geometry stored

        public X3dVertex[] Vertices;         // null for biped nodes / empty meshes
        public ushort[] Indices;             // triangle list (Length % 3 == 0)
        public X3dSkinFactor[][] Skin;       // [vertex][factor]; null = rigid mesh
        public ushort[] BoneMap;             // node ids the shader palette uses (client pBoneIndexArray)

        public int VertexCount => Vertices?.Length ?? 0;
        public int TriangleCount => (Indices?.Length ?? 0) / 3;
        public override string ToString() => $"{Name} (id={Id}, parent={ParentId}, v={VertexCount}, tri={TriangleCount})";
    }

    public struct X3dVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 Uv;
        public Vector2 Uv2;                  // only meaningful when the node has a bakemap
    }

    /// <summary>One skinning influence: <c>_XM_FACTOR</c> = weight + linked bone node id.</summary>
    public struct X3dSkinFactor
    {
        public float Weight;
        public ushort BoneId;
        public override string ToString() => $"bone {BoneId} w={Weight:0.###}";
    }

    /// <summary>Thrown when a buffer is not a valid asset of the expected format.</summary>
    public sealed class InvalidDataException : Exception
    {
        public InvalidDataException(string message) : base(message) { }
    }

    /// <summary>Sequential little-endian reader over a byte[] (mirrors the client's fread stream).</summary>
    internal sealed class SpanReader
    {
        private readonly byte[] _d;
        private int _p;
        public readonly string Source;

        public SpanReader(byte[] data, string source) { _d = data; Source = source; }

        public int Remaining => _d.Length - _p;
        public int Position { get => _p; set => _p = value; }
        public byte PeekByte(int ahead) => _d[_p + ahead];

        private void Need(int n)
        {
            if (_p + n > _d.Length)
                throw new InvalidDataException($"{Source}: truncated (need {n} bytes at {_p}, have {_d.Length - _p})");
        }

        public byte U8() { Need(1); return _d[_p++]; }
        public byte[] Bytes(int n)
        {
            Need(n);
            var b = new byte[n];
            Buffer.BlockCopy(_d, _p, b, 0, n);
            _p += n;
            return b;
        }

        /// <summary>i32 length + raw bytes (undecoded).</summary>
        public byte[] LenBytes()
        {
            int len = I32();
            if (len < 0 || len > 8192)
                throw new InvalidDataException($"{Source}: implausible blob length {len} at {_p - 4}");
            return Bytes(len);
        }

        public uint U32() { Need(4); uint v = (uint)(_d[_p] | _d[_p + 1] << 8 | _d[_p + 2] << 16 | _d[_p + 3] << 24); _p += 4; return v; }
        public int I32() => unchecked((int)U32());
        public ushort U16() { Need(2); ushort v = (ushort)(_d[_p] | _d[_p + 1] << 8); _p += 2; return v; }
        public float F32() { uint v = U32(); return BitConverter.Int32BitsToSingle(unchecked((int)v)); }
        public Vector3 Vec3() => new Vector3(F32(), F32(), F32());

        public Matrix4x4 Mat4()
        {
            return new Matrix4x4(
                F32(), F32(), F32(), F32(),
                F32(), F32(), F32(), F32(),
                F32(), F32(), F32(), F32(),
                F32(), F32(), F32(), F32());
        }

        /// <summary>i32 length + raw bytes, no NUL. Decoded ISO-8859-1 so CP949 bytes round-trip.</summary>
        public string LenString()
        {
            int len = I32();
            if (len < 0 || len > 260)
                throw new InvalidDataException($"{Source}: implausible string length {len} at {_p - 4}");
            if (len == 0) return "";
            Need(len);
            string s = Encoding.GetEncoding(28591).GetString(_d, _p, len);
            _p += len;
            int nul = s.IndexOf('\0');           // some exporters include the terminator
            return nul >= 0 ? s.Substring(0, nul) : s;
        }
    }
}
