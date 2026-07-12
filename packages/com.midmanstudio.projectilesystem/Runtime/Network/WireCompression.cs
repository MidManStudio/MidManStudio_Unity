using UnityEngine;

namespace MidManStudio.Projectiles.Network
{
    /// <summary>
    /// Wire-format compression helpers for the projectile netcode structs.
    /// Nothing here changes any gameplay logic — these are pure encode/decode
    /// pairs used only inside NetworkSerialize methods. Every C# call site
    /// outside NetworkSerialize keeps working with plain Vector3/float, same
    /// as before; only the bytes actually sent over the wire shrink.
    /// </summary>
    public static class WireCompression
    {
        // ── Direction: octahedral unit-vector encoding, 12 bytes -> 4 ──────────
        //
        // Standard technique for compressing a normalized direction (Meyer et al.
        // "On Floating-Point Normal Vectors" / the octahedral-normal-vector family
        // used throughout graphics for exactly this problem — see e.g. Cigolle et
        // al.'s survey of unit vector encodings). A unit vector only has 2 degrees
        // of freedom, so storing all 3 raw components is wasteful. This projects
        // the vector onto an octahedron and unfolds it to a 2D square in [-1, 1],
        // then quantizes each axis to a signed 16-bit int (32767 = full range).
        // At 16 bits/axis the reconstruction error is a small fraction of a
        // degree — nowhere close to visible for a projectile's flight direction.
        // Going to 8 bits/axis (2 bytes total instead of 4) is also a documented,
        // working option if bandwidth ever needs to shrink further, at the cost
        // of a coarser but still generally acceptable ~1° class of error — not
        // used here, kept at 16-bit for headroom since 4 bytes vs 12 is already
        // a 3x win with negligible precision cost.

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
        // Same technique already applied to ProjectileSnapshot2D/3D — see that
        // change for the precision reasoning (well under the reconciliation
        // system's own kSkip=0.08 tolerance, not a visible tradeoff for this use
        // case). Pulled out here so the fire-request/confirmation structs use the
        // exact same packing instead of a second copy of the same three lines.

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
        // SpreadDeg is a spread-arc angle, always in [0, 360). A byte gives
        // ~1.4°/step resolution — not something a fan-spread visual will ever
        // show as "stepped." Not used for anything that affects hit detection
        // math beyond the visual cone the pellets fan across.

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
