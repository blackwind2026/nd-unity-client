using UnityEngine;
using NumMatrix = System.Numerics.Matrix4x4;
using NumVector3 = System.Numerics.Vector3;

namespace NineDragons.Client
{
    /// <summary>
    /// D3D ↔ Unity conversions. Both are left-handed Y-up, so positions/normals carry over
    /// directly. D3D matrices are row-vector (v·M) row-major; Unity's are column-vector (M·v),
    /// so a D3D matrix becomes the Unity matrix by transposition.
    ///
    /// UVs: mesh UVs are kept EXACTLY as stored (D3D convention, v grows downward) because
    /// every texture is uploaded to Unity as raw top-down data (see TextureFactory) — the two
    /// flips cancel, so nothing is ever flipped anywhere.
    /// </summary>
    public static class UnityConvert
    {
        public static Vector3 V3(NumVector3 v) => new Vector3(v.X, v.Y, v.Z);

        public static Matrix4x4 ToUnity(NumMatrix m)
        {
            // transpose: D3D rows become Unity columns
            var u = new Matrix4x4();
            u.SetColumn(0, new Vector4(m.M11, m.M12, m.M13, m.M14));
            u.SetColumn(1, new Vector4(m.M21, m.M22, m.M23, m.M24));
            u.SetColumn(2, new Vector4(m.M31, m.M32, m.M33, m.M34));
            u.SetColumn(3, new Vector4(m.M41, m.M42, m.M43, m.M44));
            return u;
        }

        /// <summary>
        /// The owner's measured .xtv 0.91 quirk: matrix cells _12/_21/_23 are NOT matrix
        /// elements but stored scale factors (already multiplied into _11/_13/_22/_31/_33).
        /// Using them as matrix cells shears objects into the sky; zero them (client fix).
        /// </summary>
        public static NumMatrix CleanScaleSlots(NumMatrix m)
        {
            if (m.M12 != 0f || m.M21 != 0f || m.M23 != 0f)
            {
                m.M12 = 0f;
                m.M21 = 0f;
                m.M23 = 0f;
            }
            return m;
        }

        /// <summary>Apply a D3D placement matrix to a transform as local TRS.</summary>
        public static void ApplyPlacement(Transform t, NumMatrix d3d, bool cleanScaleSlots = true)
        {
            if (cleanScaleSlots) d3d = CleanScaleSlots(d3d);
            var m = ToUnity(d3d);

            var pos = new Vector3(m.m03, m.m13, m.m23);
            Vector3 c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2);
            var scale = new Vector3(c0.magnitude, c1.magnitude, c2.magnitude);
            if (scale.x < 1e-6f) scale.x = 1f;
            if (scale.y < 1e-6f) scale.y = 1f;
            if (scale.z < 1e-6f) scale.z = 1f;

            var rot = Quaternion.LookRotation(c2 / scale.z, c1 / scale.y);
            t.localPosition = pos;
            t.localRotation = rot;
            t.localScale = scale;
        }
    }
}
