using UnityEngine;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Adapters;

namespace MidManStudio.Projectiles.Network
{
    /// <summary>
    /// Single source of truth for expanding a fire event into a concrete
    /// SpawnPoint[] (direction + per-pellet speed). Called independently by
    /// the firing client (local prediction), the server (authoritative spawn
    /// in MID_ProjectileNetworkBridge.FireServerRpc), and every other client
    /// (SpawnConfirmedClientRpc → LocalProjectileManager) — always against the
    /// same PatternId → ProjectilePatternSO resolution, or the same
    /// PelletCount/SpreadDeg scalars for non-pattern fire. Must stay a pure
    /// function of its inputs: no Random, no Time, no per-call state, so every
    /// caller reproduces bit-identical results without any direction data ever
    /// crossing the network. The one varying input is the 16-bit fire seed that
    /// travels with the fire request; it makes random shapes and speed variance
    /// differ from shot to shot while every peer still gets the same result.
    ///
    /// Ports the exact math that used to live only in
    /// NetworkedDimensionPlayer.BuildSpawnPointsFromPattern / BuildSpawnPointsSpread
    /// (client-only, pre-refactor) — unchanged, just centralised so the server and
    /// other clients can run it too instead of trusting whatever the firing client sent.
    /// </summary>
    public static class ProjectileDirectionResolver
    {
        /// <param name="patternId">0 = no pattern (simple spread / single direction).</param>
        /// <param name="pelletCount">
        /// Requested pellet count. Ignored when a pattern resolves successfully —
        /// the pattern asset's own ProjectileCount is authoritative in that case,
        /// exactly matching what the firing client used. Only drives pellet count
        /// for the no-pattern spread fallback (and the pattern-lookup-failed
        /// single-shot failsafe).
        /// </param>
        /// <param name="is3D">
        /// From ProjectileConfigSO.Is3D, resolved via ProjectileId — the server's
        /// existing, already-authoritative source for 2D/3D routing (it already
        /// commits to this for the Rust write-head selection), reused here for the
        /// angle-rotation convention too.
        /// </param>
        /// <param name="fireSeed">
        /// Per-shot seed from <see cref="NewFireSeed"/>. 0 uses the pattern asset's
        /// own fixed seed, which keeps every shot identical.
        /// </param>
        public static SpawnPoint[] Resolve(
            ushort patternId, Vector3 origin, Vector3 baseDirection,
            int pelletCount, float spreadDeg, float baseSpeed, bool is3D,
            ushort fireSeed = 0)
        {
            if (patternId != 0)
            {
                var pattern = ProjectilePatternRegistry.HasInstance
                    ? ProjectilePatternRegistry.Instance.Get(patternId)
                    : null;

                if (pattern != null)
                    return ResolvePattern(pattern, origin, baseDirection, baseSpeed, is3D, fireSeed);

                // Unknown/unregistered pattern id (stale build, registry order
                // mismatch, etc.) — fail safe to a single shot along BaseDirection
                // rather than trusting anything else about the request.
            }

            return pelletCount > 1
                ? ResolveSpread(origin, baseDirection, pelletCount, spreadDeg, baseSpeed, is3D)
                : new[]
                {
                    new SpawnPoint
                    {
                        Origin    = origin,
                        Direction = baseDirection.sqrMagnitude > 0.001f ? baseDirection.normalized : Vector3.forward,
                        Speed     = baseSpeed
                    }
                };
        }

        // ── Pattern-based (Fan/Ring/VShape/Shotgun/Star/Spiral/Formula/Spline) ──

        /// <summary>
        /// Picks the seed for one shot. Called once by the firing side, never by
        /// <see cref="Resolve"/>. Never returns 0, because 0 means "no seed".
        /// </summary>
        public static ushort NewFireSeed() => (ushort)Random.Range(1, 65536);

        private static SpawnPoint[] ResolvePattern(
            ProjectilePatternSO pattern, Vector3 origin, Vector3 baseDir, float baseSpeed, bool is3D,
            ushort fireSeed)
        {
            // No count override: call exactly as the client does, so both sides
            // read the pattern asset's own ProjectileCount rather than trusting
            // a wire-transmitted number that could theoretically drift from it.
            uint seed = pattern.MixSeed(fireSeed);
            Vector2[] angleDirs = pattern.SampleDirections(-1, seed);
            var pts = new SpawnPoint[angleDirs.Length];

            Vector3 localRight, localUp;
            if (is3D)
            {
                Vector3 fwd     = baseDir.sqrMagnitude > 0.001f ? baseDir.normalized : Vector3.forward;
                Vector3 worldUp = Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) > 0.98f ? Vector3.forward : Vector3.up;
                localRight = Vector3.Cross(worldUp, fwd).normalized;
                localUp    = Vector3.Cross(fwd, localRight).normalized;
            }
            else
            {
                localRight = Vector3.Cross(baseDir, Vector3.forward).normalized;
                localUp    = Vector3.forward;
            }

            for (int i = 0; i < angleDirs.Length; i++)
            {
                Vector2 a = angleDirs[i];
                Vector3 sDir = is3D
                    ? Quaternion.AngleAxis(-a.y, localRight) * Quaternion.AngleAxis(a.x, localUp) * baseDir
                    : Quaternion.Euler(0f, 0f, a.x) * baseDir;

                if (sDir.sqrMagnitude < 0.001f) sDir = baseDir;

                // Speed and start delay come from the same per-shot seed the
                // directions used, so every peer rebuilds identical values.
                float mul = pattern.GetSpeedMultiplier(i, seed);

                pts[i] = new SpawnPoint
                {
                    Origin     = origin,
                    Direction  = sDir.normalized,
                    Speed      = baseSpeed * mul,
                    StartDelay = pattern.GetStartDelay(i, angleDirs.Length, seed)
                };
            }
            return pts;
        }

        // ── No pattern: simple parametric fan ───────────────────────────────────

        private static SpawnPoint[] ResolveSpread(
            Vector3 origin, Vector3 dir, int n, float spreadDeg, float baseSpeed, bool is3D)
        {
            var pts = new SpawnPoint[n];
            for (int i = 0; i < n; i++)
            {
                float frac = n == 1 ? 0f : (i / (float)(n - 1) - 0.5f);
                Vector3 sDir = is3D
                    ? Quaternion.Euler(0f, frac * spreadDeg, 0f) * dir
                    : Quaternion.Euler(0f, 0f, frac * spreadDeg) * dir;
                pts[i] = new SpawnPoint
                {
                    Origin    = origin,
                    Direction = sDir.normalized,
                    Speed     = baseSpeed
                };
            }
            return pts;
        }
    }
}
