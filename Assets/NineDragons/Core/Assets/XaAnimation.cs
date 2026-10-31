using System;
using System.Collections.Generic;
using System.Numerics;

namespace NineDragons.Assets
{
    /// <summary>
    /// Parser for the client's <c>.XA</c> animation-key files (package <c>AR_BASEANIMATION.XP</c>).
    ///
    /// Ground truth: <c>MeshControl\XBipedController.cpp</c> <c>_XBipedController::LoadKey(FILE*)</c>
    /// + structs in <c>XKernel\XModelContainerDef.h</c> (<c>_XAHEADER</c>, <c>_XM_POSITIONKEY</c>,
    /// <c>_XM_ROTATIONKEY</c>).
    ///
    /// On-disk layout (little-endian):
    /// <code>
    ///   _XAHEADER (24 B):
    ///     u32 indicator  = 0x0000AE00
    ///     f32 version    = 0.6
    ///     i32 firstFrame, lastFrame, frameSpeed, tickSpeed
    ///   i32 trackCount                       (bones animated by this motion)
    ///   track * trackCount:
    ///     i32 id
    ///     i32 nameLen,  u8 name[nameLen]     (client matches the .xbp skeleton BY NAME, not id)
    ///     i32 parentId
    ///     i32 pnameLen, u8 parentName[pnameLen]
    ///     f32x16 matrix                      (object matrix; client reads and ignores it)
    ///     i32 posKeyCount
    ///       key * posKeyCount: f32 time, f32x3 value          (16 B, _XM_POSITIONKEY)
    ///     i32 rotKeyCount
    ///       key * rotKeyCount: f32 time, f32x4 x,y,z,w        (20 B, _XM_ROTATIONKEY)
    /// </code>
    ///
    /// Rotation keys are NOT quaternions on disk: (x,y,z) is a rotation axis and w the angle
    /// (radians). Each key is a DELTA from the previous key. The client converts with
    /// <code>
    ///   angle = -w;  q = (sin(angle/2)·x, sin(angle/2)·y, sin(angle/2)·z, cos(angle/2))
    /// </code>
    /// and then accumulates <c>q[j] = q[j-1] ⊗ q[j]</c> (Hamilton product,
    /// <c>_XMeshMath_QuaternionMultiply</c>) to obtain absolute rotations —
    /// <see cref="XaTrack.AccumulatedRotations"/> reproduces exactly that.
    /// (When the first key's time != 0 the client additionally prepends a frame-0 key derived
    /// from the skeleton's local TM; that needs the .xbp and is left to the scene layer.)
    /// The client's temp buffer holds 1024 keys per track (_XDEF_XKEYBUFFERSIZE) — real files
    /// stay far below that.
    /// </summary>
    public sealed class XaAnimation
    {
        public const uint Indicator = 0x0000AE00;   // _XDEF_XAFILEINDICATOR

        public float Version { get; private set; }  // 0.6
        public int FirstFrame { get; private set; }
        public int LastFrame { get; private set; }
        public int FrameSpeed { get; private set; }
        public int TickSpeed { get; private set; }

        public IReadOnlyList<XaTrack> Tracks => _tracks;
        private XaTrack[] _tracks;

        /// <summary>Bytes left unread after the last track (0 for every sampled real file).</summary>
        public int TrailingBytes { get; private set; }

        public static XaAnimation Parse(byte[] data, string name = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var r = new SpanReader(data, name ?? "XA");

            uint indicator = r.U32();
            if (indicator != Indicator)
                throw new InvalidDataException($"{r.Source}: bad indicator 0x{indicator:X8} (want 0x{Indicator:X8})");

            var a = new XaAnimation { Version = r.F32() };
            if (Math.Abs(a.Version - 0.6f) > 1e-4f)     // client: != _XDEF_XAFILEVERSION → reject
                throw new InvalidDataException($"{r.Source}: unsupported version {a.Version}");

            a.FirstFrame = r.I32();
            a.LastFrame = r.I32();
            a.FrameSpeed = r.I32();
            a.TickSpeed = r.I32();

            int trackCount = r.I32();
            if (trackCount < 0 || trackCount > 4096)
                throw new InvalidDataException($"{r.Source}: implausible track count {trackCount}");

            var tracks = new XaTrack[trackCount];
            for (int i = 0; i < trackCount; i++)
            {
                var t = new XaTrack();
                t.Id = r.I32();
                t.Name = r.LenString();
                t.ParentId = r.I32();
                t.ParentName = r.LenString();
                t.Matrix = r.Mat4();

                int pcnt = r.I32();
                if (pcnt < 0 || pcnt > 65536)
                    throw new InvalidDataException($"{r.Source}: track {i} implausible pos-key count {pcnt}");
                t.PosKeys = new XaPosKey[pcnt];
                for (int k = 0; k < pcnt; k++)
                {
                    t.PosKeys[k].Time = r.F32();
                    t.PosKeys[k].Value = r.Vec3();
                }

                int rcnt = r.I32();
                if (rcnt < 0 || rcnt > 65536)
                    throw new InvalidDataException($"{r.Source}: track {i} implausible rot-key count {rcnt}");
                t.RotKeys = new XaRotKey[rcnt];
                for (int k = 0; k < rcnt; k++)
                {
                    t.RotKeys[k].Time = r.F32();
                    t.RotKeys[k].AxisX = r.F32();
                    t.RotKeys[k].AxisY = r.F32();
                    t.RotKeys[k].AxisZ = r.F32();
                    t.RotKeys[k].Angle = r.F32();
                }

                tracks[i] = t;
            }

            a._tracks = tracks;
            a.TrailingBytes = r.Remaining;
            return a;
        }
    }

    /// <summary>Key data for one bone (matched to the .xbp skeleton by <see cref="Name"/>).</summary>
    public sealed class XaTrack
    {
        public int Id;
        public int ParentId;
        public string Name = "";
        public string ParentName = "";
        public Matrix4x4 Matrix;             // stored object matrix (client ignores it on load)

        public XaPosKey[] PosKeys = Array.Empty<XaPosKey>();
        public XaRotKey[] RotKeys = Array.Empty<XaRotKey>();

        /// <summary>
        /// Absolute rotation per key, exactly as the client computes it for the common case
        /// (first key at time 0): negate the stored angle, build the quaternion from the
        /// axis-angle, then multiply cumulatively onto the previous key.
        /// </summary>
        public Quaternion[] AccumulatedRotations()
        {
            var q = new Quaternion[RotKeys.Length];
            for (int j = 0; j < RotKeys.Length; j++)
            {
                q[j] = RotKeys[j].ToDeltaQuaternion();
                if (j > 0) q[j] = Multiply(q[j - 1], q[j]);
            }
            return q;
        }

        /// <summary>Hamilton product q = a ⊗ b, the exact <c>_XMeshMath_QuaternionMultiply</c>.</summary>
        internal static Quaternion Multiply(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.X * b.W + a.Y * b.Z - a.Z * b.Y + a.W * b.X,
                -a.X * b.Z + a.Y * b.W + a.Z * b.X + a.W * b.Y,
                a.X * b.Y - a.Y * b.X + a.Z * b.W + a.W * b.Z,
                -a.X * b.X - a.Y * b.Y - a.Z * b.Z + a.W * b.W);
        }

        public override string ToString() => $"{Name} (pos={PosKeys.Length}, rot={RotKeys.Length})";
    }

    public struct XaPosKey
    {
        public float Time;                   // frame number (float on disk)
        public Vector3 Value;
    }

    /// <summary>Raw on-disk rotation key: axis + angle DELTA (see <see cref="XaAnimation"/>).</summary>
    public struct XaRotKey
    {
        public float Time;
        public float AxisX, AxisY, AxisZ;
        public float Angle;                  // radians; client negates it before use

        /// <summary>The client's per-key conversion (before accumulation).</summary>
        public Quaternion ToDeltaQuaternion()
        {
            float half = -Angle / 2f;
            float s = (float)Math.Sin(half);
            return new Quaternion(s * AxisX, s * AxisY, s * AxisZ, (float)Math.Cos(half));
        }
    }
}
