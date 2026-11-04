// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "WireCompression.cs"
// ============================================================================
using UnityEngine;

namespace MidManStudio.Projectiles.Network
{
    /// <summary>
    /// Wire-format compression helpers for the projectile netcode structs.
    /// These are pure encode/decode pairs used inside NetworkSerialize methods.
    /// Code outside NetworkSerialize works with plain Vector3 and float values,
    /// and only the bytes sent over the wire shrink. The values that come out of
    /// a decode are the quantized ones, so they can differ slightly from what the
    /// sender held.
    /// </summary>
    public static class WireCompression
    {
        // ── Direction: octahedral unit-vector encoding, 12 bytes -> 4 ──────────
        //
        // Octahedral unit-vector encoding, a standard technique (see Cigolle et
        // al., "A Survey of Efficient Representations for Independent Unit
        // Vectors"). A unit vector has only 2 degrees of freedom, so storing all 3
        // components wastes space. This projects the vector onto an octahedron,
        // unfolds it to a 2D square in [-1, 1], then quantizes each axis to a
        // signed 16-bit int (32767 = full range). At 16 bits per axis the
        // reconstruction error is a small fraction of a degree. A zero-length
        // input encodes as forward (+Z). 8 bits per axis would halve it again at
        // the cost of roughly 1 degree of error, and is not used.

        public static void EncodeDirection(Vector3 dir, out short ex, out short ey)
        {
            Vector3 n = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;

            float sum = Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
            float px = n.x / sum;
            float py = n.y / sum;

            if (n.z < 0f)
            {
                float ox = (1f - Mathf.Abs(py)) * (px >= 0f ? 1f : -1f);
                float oy = (1f - Mathf.Abs(px)) * (py >= 0f ? 1f : -1f);
                px = ox;
                py = oy;
            }

            ex = (short)Mathf.RoundToInt(Mathf.Clamp(px, -1f, 1f) * 32767f);
            ey = (short)Mathf.RoundToInt(Mathf.Clamp(py, -1f, 1f) * 32767f);
        }

        public static Vector3 DecodeDirection(short ex, short ey)
        {
            float px = ex / 32767f;
            float py = ey / 32767f;

            Vector3 n = new Vector3(px, py, 1f - Mathf.Abs(px) - Mathf.Abs(py));
            float t = Mathf.Clamp01(-n.z);
            n.x += n.x >= 0f ? -t : t;
            n.y += n.y >= 0f ? -t : t;

            return n.sqrMagnitude > 0.0001f ? n.normalized : Vector3.forward;
        }

        // ── Position: half-precision per axis, 12 bytes -> 6 ───────────────────
        //
        // IEEE half precision per axis (Mathf.FloatToHalf), used by the fire-request
        // and confirmation structs. The snapshot structs in ServerProjectileAuthority
        // apply the same conversion directly instead of calling this class, so they
        // share its precision. Precision depends on the
        // magnitude: the spacing between values is 2^(e-10) for values in
        // [2^e, 2^(e+1)), so the largest rounding error is about 0.001 below 4,
        // 0.0625 between 128 and 256, 0.125 between 256 and 512 and 0.25 between
        // 512 and 1024 world units. Reconciliation skips errors below 0.08, so keep
        // coordinates under about 256 units from the origin. Values above 65504
        // become infinity.

        public static void EncodePosition(Vector3 pos, out ushort hx, out ushort hy, out ushort hz)
        {
            hx = Mathf.FloatToHalf(pos.x);
            hy = Mathf.FloatToHalf(pos.y);
            hz = Mathf.FloatToHalf(pos.z);
        }

        public static Vector3 DecodePosition(ushort hx, ushort hy, ushort hz)
        {
            return new Vector3(Mathf.HalfToFloat(hx), Mathf.HalfToFloat(hy), Mathf.HalfToFloat(hz));
        }

        // ── Angle: byte-quantized degrees, 4 bytes -> 1 ─────────────────────────
        //
        // SpreadDeg is a spread-arc angle in [0, 360). A byte gives 360/255, about
        // 1.41 degrees per step, and values wrap into 0 to 360 before encoding. The
        // spread only sets the cone the pellets fan across.

        public static byte EncodeDegrees0to360(float deg)
        {
            float wrapped = Mathf.Repeat(deg, 360f);
            return (byte)Mathf.RoundToInt(wrapped / 360f * 255f);
        }

        public static float DecodeDegrees0to360(byte packed)
        {
            return packed / 255f * 360f;
        }
    }
}
