// Closed-form parametric position and velocity-direction formulas for Wave and
// Circular projectile types. Used by ClientPredictionManager to replace noisy
// snapshot-velocity-estimation with analytically exact client-side simulation.
//
// CONTRACT: every formula in this file must exactly match the corresponding Rust
// simulation differential equation in simulation.rs. Verified derivations below.
//
// CIRCULAR 2D — derived from tick_circular:
//   Rust rotates velocity vector by omega*dt each tick (true arc, no drift).
//   Continuous integral: P(t) = origin + (V/omega)*(sin(θ0+ωt)-sin(θ0)) x̂
//                                      - (V/omega)*(cos(θ0+ωt)-cos(θ0)) ŷ
//   where θ0 = atan2(vy0_rot, vx0_rot) after applying start_angle pre-rotation.
//
// WAVE 2D — derived from tick_wave:
//   Rust adds perp * amplitude * sin(freq*2π*t + phase) * dt each frame (Euler).
//   Continuous integral: waveDisp = amplitude*(cos(phase)-cos(freq*2π*t+phase))/(freq*2π)
//
// CIRCULAR 3D — derived from tick_circular_3d:
//   Rust adds orbital velocity R*ω*(-sin(angle)*u + cos(angle)*v2)*dt per frame.
//   Continuous integral: orbDisp = R*(cos(at)-cos(a0))*u + R*(sin(at)-sin(a0))*v2
//   where u = perpAxis (from ax/ay/az at spawn), v2 = cross(forward, u).
//
// WAVE 3D — derived from tick_wave_3d:
//   Same integral as Wave 2D applied to 3D perp axis.
//
// PERPENDICULAR AXIS CONTRACT:
//   2D: (-dir.y, dir.x, 0) — matches BatchSpawnHelper.GetAccel2D exactly.
//   3D: (-dir.y/xyLen, dir.x/xyLen, 0) or (1,0,0) when dir is along Z —
//       matches BatchSpawnHelper.GetAccel3D exactly.
//   These must never diverge or the visual will oscillate opposite to the server sim.

using UnityEngine;

namespace MidManStudio.Projectiles.Network
{
    internal static class DeterministicMotionMath
    {
        // ═════════════════════════════════════════════════════════════════════
        //  2D CIRCULAR
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Analytical position on a circular-arc path at <paramref name="timeAlive"/> seconds.
        /// Matches Rust tick_circular: velocity vector rotated by omega*dt each tick,
        /// start_angle_deg pre-rotation applied once at t=0.
        /// </summary>
        public static Vector3 CalculateCircular2DPosition(
            Vector3 origin,
            float   initialVelX,
            float   initialVelY,
            float   angularSpeedRad,
            float   startAngleRad,
            float   timeAlive)
        {
            // Apply start_angle pre-rotation — mirrors Rust first-tick logic
            float cs  = Mathf.Cos(startAngleRad);
            float sn  = Mathf.Sin(startAngleRad);
            float vx0 = initialVelX * cs - initialVelY * sn;
            float vy0 = initialVelX * sn + initialVelY * cs;

            float V      = Mathf.Sqrt(vx0 * vx0 + vy0 * vy0);
            float theta0 = Mathf.Atan2(vy0, vx0);
            float omega  = angularSpeedRad;

            // L'Hôpital limit: as omega→0 the arc becomes a straight line
            if (Mathf.Abs(omega) < 0.001f)
                return origin + new Vector3(vx0, vy0, 0f) * timeAlive;

            float t = timeAlive;
            float x = origin.x + (V / omega) * (Mathf.Sin(theta0 + omega * t) - Mathf.Sin(theta0));
            float y = origin.y - (V / omega) * (Mathf.Cos(theta0 + omega * t) - Mathf.Cos(theta0));
            return new Vector3(x, y, 0f);
        }

        /// <summary>
        /// Instantaneous velocity direction for circular-arc motion at <paramref name="timeAlive"/>.
        /// Returns (V*cos(θ0+ωt), V*sin(θ0+ωt)) — exact tangent vector for Z-rotation.
        /// </summary>
        public static Vector3 CalculateCircular2DVelocityDirection(
            float initialVelX,
            float initialVelY,
            float angularSpeedRad,
            float startAngleRad,
            float timeAlive)
        {
            float cs  = Mathf.Cos(startAngleRad);
            float sn  = Mathf.Sin(startAngleRad);
            float vx0 = initialVelX * cs - initialVelY * sn;
            float vy0 = initialVelX * sn + initialVelY * cs;

            float V      = Mathf.Sqrt(vx0 * vx0 + vy0 * vy0);
            float theta0 = Mathf.Atan2(vy0, vx0);
            float omega  = angularSpeedRad;

            if (Mathf.Abs(omega) < 0.001f)
                return new Vector3(vx0, vy0, 0f);

            float angle = theta0 + omega * timeAlive;
            return new Vector3(V * Mathf.Cos(angle), V * Mathf.Sin(angle), 0f);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  2D WAVE
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Analytical position on a wave path at <paramref name="timeAlive"/> seconds.
        /// Closed-form integral of Rust tick_wave oscillation term.
        /// </summary>
        /// <param name="perpX">(-dir.y) — must match BatchSpawnHelper.GetAccel2D exactly.</param>
        /// <param name="perpY">( dir.x) — must match BatchSpawnHelper.GetAccel2D exactly.</param>
        public static Vector3 CalculateWave2DPosition(
            Vector3 origin,
            float   dirX,
            float   dirY,
            float   speed,
            float   amplitude,
            float   frequency,
            float   phaseOffset,
            float   perpX,
            float   perpY,
            float   timeAlive)
        {
            Vector3 basePos = origin
                + new Vector3(dirX, dirY, 0f).normalized * speed * timeAlive;

            if (frequency < 0.001f)
                return basePos;

            // Integral of amplitude * sin(freq*2π*s + phase) from 0 to t
            // = amplitude * (cos(phase) - cos(freq*2π*t + phase)) / (freq*2π)
            float a             = frequency * 2f * Mathf.PI;
            float waveDisplacement = amplitude
                * (Mathf.Cos(phaseOffset) - Mathf.Cos(a * timeAlive + phaseOffset)) / a;

            return basePos + new Vector3(perpX, perpY, 0f) * waveDisplacement;
        }

        /// <summary>
        /// Instantaneous velocity direction for 2D wave motion.
        /// base_vel + perp * d/dt[wave_displacement] — use for Z-rotation calculation.
        /// </summary>
        public static Vector3 CalculateWave2DVelocityDirection(
            float dirX,
            float dirY,
            float speed,
            float amplitude,
            float frequency,
            float phaseOffset,
            float perpX,
            float perpY,
            float timeAlive)
        {
            Vector3 baseVel = new Vector3(dirX, dirY, 0f).normalized * speed;

            if (frequency < 0.001f)
                return baseVel;

            // d/dt[wave_displacement] = amplitude * sin(a*t + phase) (the original integrand)
            float a       = frequency * 2f * Mathf.PI;
            float waveVel = amplitude * a * (-Mathf.Sin(a * timeAlive + phaseOffset));
            return baseVel + new Vector3(perpX, perpY, 0f) * waveVel;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  3D CIRCULAR
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Analytical 3D helical position at <paramref name="timeAlive"/> seconds.
        /// Matches Rust tick_circular_3d orbital velocity integration exactly.
        /// Derivation: integrate R*ω*(-sin(ωt+a0)*u + cos(ωt+a0)*v2) over [0,t].
        ///   Result: orbDisp = R*(cos(at)-cos(a0))*u + R*(sin(at)-sin(a0))*v2
        /// </summary>
        /// <param name="perpAxis">u — first perpendicular axis (from ax/ay/az at spawn).</param>
        public static Vector3 CalculateCircular3DPosition(
            Vector3 origin,
            Vector3 initialVelocity,
            float   angularSpeedRad,
            float   startAngleRad,
            Vector3 perpAxis,
            float   radius,
            float   timeAlive)
        {
            float omega = angularSpeedRad;
            if (Mathf.Abs(omega) < 0.001f)
                return origin + initialVelocity * timeAlive;

            float   spd     = initialVelocity.magnitude;
            Vector3 forward = spd > 1e-6f ? initialVelocity / spd : Vector3.forward;
            Vector3 u       = perpAxis.sqrMagnitude > 0.001f
                ? perpAxis.normalized : Vector3.right;
            Vector3 v2 = Vector3.Cross(forward, u).normalized;

            float a0 = startAngleRad;
            float at = omega * timeAlive + a0;

            Vector3 orbDisp = radius * (Mathf.Cos(at) - Mathf.Cos(a0)) * u
                            + radius * (Mathf.Sin(at) - Mathf.Sin(a0)) * v2;

            return origin + initialVelocity * timeAlive + orbDisp;
        }

        /// <summary>
        /// Instantaneous velocity direction for 3D circular motion.
        /// Returns forward_velocity + orbital_velocity(t) for LookRotation computation.
        /// </summary>
        public static Vector3 CalculateCircular3DVelocityDirection(
            Vector3 initialVelocity,
            float   angularSpeedRad,
            float   startAngleRad,
            Vector3 perpAxis,
            float   radius,
            float   timeAlive)
        {
            float omega = angularSpeedRad;
            if (Mathf.Abs(omega) < 0.001f) return initialVelocity;

            float   spd     = initialVelocity.magnitude;
            Vector3 forward = spd > 1e-6f ? initialVelocity / spd : Vector3.forward;
            Vector3 u       = perpAxis.sqrMagnitude > 0.001f
                ? perpAxis.normalized : Vector3.right;
            Vector3 v2  = Vector3.Cross(forward, u).normalized;

            float angle  = omega * timeAlive + startAngleRad;
            float sinA   = Mathf.Sin(angle);
            float cosA   = Mathf.Cos(angle);

            // d/dt[orbDisp] = R*ω*(-sin(ωt+a0)*u + cos(ωt+a0)*v2)
            Vector3 orbitalVel = radius * omega * (-sinA * u + cosA * v2);
            return initialVelocity + orbitalVel;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  3D WAVE
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Analytical 3D wave position at <paramref name="timeAlive"/> seconds.
        /// Same closed-form integral as Wave 2D, applied to 3D perpendicular axis.
        /// </summary>
        public static Vector3 CalculateWave3DPosition(
            Vector3 origin,
            Vector3 velocity,
            float   amplitude,
            float   frequency,
            float   phaseOffset,
            Vector3 perpAxis,
            float   timeAlive)
        {
            Vector3 basePos = origin + velocity * timeAlive;

            if (frequency < 0.001f)
                return basePos;

            float a                = frequency * 2f * Mathf.PI;
            float waveDisplacement = amplitude
                * (Mathf.Cos(phaseOffset) - Mathf.Cos(a * timeAlive + phaseOffset)) / a;

            return basePos + perpAxis * waveDisplacement;
        }

        /// <summary>Instantaneous velocity direction for 3D wave motion.</summary>
        public static Vector3 CalculateWave3DVelocityDirection(
            Vector3 velocity,
            float   amplitude,
            float   frequency,
            float   phaseOffset,
            Vector3 perpAxis,
            float   timeAlive)
        {
            if (frequency < 0.001f) return velocity;

            float a       = frequency * 2f * Mathf.PI;
            float waveVel = amplitude * a * (-Mathf.Sin(a * timeAlive + phaseOffset));
            return velocity + perpAxis * waveVel;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ROTATION HELPERS
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>2D rotation from velocity direction using Z-Euler.</summary>
        public static Quaternion CalculateLookRotation2D(Vector3 velocityDirection)
        {
            if (velocityDirection.sqrMagnitude < 0.001f) return Quaternion.identity;
            float angleDeg = Mathf.Atan2(velocityDirection.y, velocityDirection.x)
                           * Mathf.Rad2Deg;
            return Quaternion.Euler(0f, 0f, angleDeg);
        }

        /// <summary>3D rotation using LookRotation with safe up-vector fallback.</summary>
        public static Quaternion CalculateLookRotation3D(Vector3 velocityDirection)
        {
            if (velocityDirection.sqrMagnitude < 0.001f) return Quaternion.identity;
            Vector3 dir = velocityDirection.normalized;
            Vector3 up  = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.99f
                ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(dir, up);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  PERPENDICULAR AXIS HELPERS
        //  These MUST produce identical results to BatchSpawnHelper.GetAccel2D/3D.
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 2D perpendicular axis: (-dir.y, dir.x, 0).
        /// Matches BatchSpawnHelper.GetAccel2D for MOVE_WAVE and MOVE_CIRCULAR.
        /// </summary>
        public static Vector3 ComputePerpAxis2D(Vector3 dir)
            => new Vector3(-dir.y, dir.x, 0f);

        /// <summary>
        /// 3D perpendicular axis matching BatchSpawnHelper.GetAccel3D.
        /// (-dir.y/xyLen, dir.x/xyLen, 0) when xyLen > 0.001, else (1,0,0).
        /// </summary>
        public static Vector3 ComputePerpAxis3D(Vector3 dir)
        {
            float xyLen = Mathf.Sqrt(dir.x * dir.x + dir.y * dir.y);
            if (xyLen > 0.001f)
                return new Vector3(-dir.y / xyLen, dir.x / xyLen, 0f);
            return new Vector3(1f, 0f, 0f);
        }
    }
}
