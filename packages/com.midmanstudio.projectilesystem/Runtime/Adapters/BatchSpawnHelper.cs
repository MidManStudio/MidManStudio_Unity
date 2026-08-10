//  Ax/Ay for Wave and Circular movement now set to perpendicular of the
// fire direction instead of (0, GravityAy). The Rust simulation expects:
//   MOVE_WAVE     → (Ax,Ay) = perpendicular axis to oscillate along
//   MOVE_CIRCULAR → (Ax,Ay) = first perpendicular axis for orbit plane
//   All others    → (Ax,Ay) = (0, GravityAy) as before
//
//   Latency compensation no longer advances the projectile's X/Y/Z position.
//   Previously: p.X += p.Vx * latencyCompensation  (etc.) moved the bullet
//   to its "time-compensated" location BEFORE inserting it into the Rust buffer.
//   The host renders directly from that buffer, so it saw the bullet already
//   vel × latencyComp units ahead of the barrel — visually wrong.
//    only reduce Lifetime by latencyCompensation. The DeterministicMath
//   client-prediction path + ServerNetworkTime naturally account for the clock
//   offset without needing the position to be pre-advanced. Collision accuracy
//   loss is negligible for typical LAN/online latencies (< 100 ms).

using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Adapters
{
    public struct SpawnPoint
    {
        public Vector3 Origin;
        public Vector3 Direction;
        public float   Speed;
    }

    public static class BatchSpawnHelper
    {
        // Mirror of Rust movement type constants — must stay in sync with simulation.rs
        private const byte MOVE_STRAIGHT = 0;
        // MOVE_ARCHING (was 1) removed — see rust_lib's simulation.rs file header
        // "REMOVED" note. It was dead here too: never actually referenced in this
        // file, only MOVE_WAVE/MOVE_CIRCULAR are used below for perpendicular-axis
        // spawn-time setup.
        private const byte MOVE_GUIDED   = 2;
        private const byte MOVE_TELEPORT = 3;
        private const byte MOVE_WAVE     = 4;
        private const byte MOVE_CIRCULAR = 5;

        public const int BurstThreshold = 8;

        private static readonly NativeProjectile[]   _temp2D = new NativeProjectile[256];
        private static readonly NativeProjectile3D[] _temp3D = new NativeProjectile3D[256];
        private static GCHandle _pin2D;
        private static GCHandle _pin3D;
        private static bool     _pinsAllocated;

        // ── Initialisation ────────────────────────────────────────────────────

        public static void Initialise()
        {
            if (_pinsAllocated) return;
            _pin2D = GCHandle.Alloc(_temp2D, GCHandleType.Pinned);
            _pin3D = GCHandle.Alloc(_temp3D, GCHandleType.Pinned);
            _pinsAllocated = true;
        }

        public static void Shutdown()
        {
            if (!_pinsAllocated) return;
            if (_pin2D.IsAllocated) _pin2D.Free();
            if (_pin3D.IsAllocated) _pin3D.Free();
            _pinsAllocated = false;
        }

        // ── Perpendicular axis helpers ─────────────────────────────────────────

        /// <summary>
        /// Returns (Ax, Ay) for a 2D projectile given its direction and movement type.
        /// Wave/Circular: perpendicular to direction in the XY plane.
        /// Others: (0, gravityAy).
        /// </summary>
        private static (float ax, float ay) GetAccel2D(
            Vector3 dir, byte movementType, float gravityAy)
        {
            if (movementType == MOVE_WAVE || movementType == MOVE_CIRCULAR)
            {
                // 2D perpendicular (CCW rotation of the direction by 90°)
                return (-dir.y, dir.x);
            }
            return (0f, gravityAy);
        }

        /// <summary>
        /// Returns (Ax, Ay, Az) for a 3D projectile.
        /// Wave/Circular: perpendicular axis in XY plane, zero Z component.
        /// Others: (0, gravityAy, 0).
        /// </summary>
        private static (float ax, float ay, float az) GetAccel3D(
            Vector3 dir, byte movementType, float gravityAy)
        {
            if (movementType == MOVE_WAVE || movementType == MOVE_CIRCULAR)
            {
                float len = Mathf.Sqrt(dir.x * dir.x + dir.y * dir.y);
                if (len > 0.001f)
                    return (-dir.y / len, dir.x / len, 0f);
                // Forward is along Z — pick X as perpendicular
                return (1f, 0f, 0f);
            }
            return (0f, gravityAy, 0f);
        }

        // ── 2D Spawn ──────────────────────────────────────────────────────────

        public static int SpawnBatch2D(
            SpawnPoint[]    spawnPoints,
            int             count,
            ProjectileConfigSO config,
            RustSpawnParams rustParams,
            ushort          configId,
            ushort          ownerId,
            uint            nextProjId,
            IntPtr          projsOutPtr,
            int             bufferRemaining,
            float           latencyCompensation = 0f)
        {
            if (!_pinsAllocated) Initialise();

            int n = Mathf.Min(count, Mathf.Min(_temp2D.Length, bufferRemaining));
            if (n <= 0) return 0;

            if (n >= BurstThreshold)
                FillBurst2D(spawnPoints, n, rustParams, configId, ownerId, nextProjId);
            else
                FillManaged2D(spawnPoints, n, rustParams, configId, ownerId, nextProjId);

            if (latencyCompensation > 0f)
            {
                for (int i = 0; i < n; i++)
                {
                    ref var p = ref _temp2D[i];
                    if (p.Alive == 0) continue;

                    // FIX: Position is NOT advanced — bullet spawns at the original fire origin
                    // on all rendering clients (including the host). The old behaviour
                    // (p.X += p.Vx * latencyCompensation; p.Y += p.Vy * latencyCompensation)
                    // caused the host to see bullets pop in vel × latencyComp units ahead.
                    // Lifetime is still reduced so the projectile expires at the correct time.
                    p.Lifetime -= latencyCompensation;
                    if (p.Lifetime <= 0f) p.Alive = 0;
                }
            }

            ProjectileLib.spawn_batch(
                _pin2D.AddrOfPinnedObject(), n,
                projsOutPtr, bufferRemaining,
                out int written);

            return written;
        }

        // ── 3D Spawn ──────────────────────────────────────────────────────────

        public static int SpawnBatch3D(
            SpawnPoint[]    spawnPoints,
            int             count,
            RustSpawnParams rustParams,
            ushort          configId,
            ushort          ownerId,
            uint            nextProjId,
            IntPtr          projsOutPtr,
            int             bufferRemaining,
            float           latencyCompensation = 0f)
        {
            if (!_pinsAllocated) Initialise();

            int n = Mathf.Min(count, Mathf.Min(_temp3D.Length, bufferRemaining));
            if (n <= 0) return 0;

            if (n >= BurstThreshold)
                FillBurst3D(spawnPoints, n, rustParams, configId, ownerId, nextProjId);
            else
                FillManaged3D(spawnPoints, n, rustParams, configId, ownerId, nextProjId);

            if (latencyCompensation > 0f)
            {
                for (int i = 0; i < n; i++)
                {
                    ref var p = ref _temp3D[i];
                    if (p.Alive == 0) continue;

                    // FIX: Same as 2D — position NOT advanced, only lifetime reduced.
                    p.Lifetime -= latencyCompensation;
                    if (p.Lifetime <= 0f) p.Alive = 0;
                }
            }

            ProjectileLib.spawn_batch_3d(
                _pin3D.AddrOfPinnedObject(), n,
                projsOutPtr, bufferRemaining,
                out int written);

            return written;
        }

        // ── Managed fill ──────────────────────────────────────────────────────

        private static void FillManaged2D(
            SpawnPoint[] pts, int n, RustSpawnParams p,
            ushort configId, ushort ownerId, uint baseId)
        {
            for (int i = 0; i < n; i++)
            {
                float speed = pts[i].Speed > 0f ? pts[i].Speed : p.Speed;
                var (ax, ay) = GetAccel2D(pts[i].Direction, p.MovementType, p.GravityAy);

                _temp2D[i] = new NativeProjectile
                {
                    X            = pts[i].Origin.x,
                    Y            = pts[i].Origin.y,
                    Vx           = pts[i].Direction.x * speed,
                    Vy           = pts[i].Direction.y * speed,
                    Ax           = ax,
                    Ay           = ay,
                    AngleDeg     = Mathf.Atan2(pts[i].Direction.y, pts[i].Direction.x) * Mathf.Rad2Deg,
                    CurveT       = 0f,
                    ScaleX       = p.ScaleStart,
                    ScaleY       = p.ScaleStart,
                    ScaleTarget  = p.ScaleTarget,
                    ScaleSpeed   = p.ScaleSpeed,
                    Lifetime     = p.Lifetime,
                    MaxLifetime  = p.Lifetime,
                    TravelDist   = 0f,
                    ConfigId     = configId,
                    OwnerId      = ownerId,
                    ProjId       = baseId + (uint)i,
                    CollisionCount = 0,
                    MovementType = p.MovementType,
                    PiercingType = p.PiercingType,
                    Alive        = 1
                };
            }
        }

        private static void FillManaged3D(
            SpawnPoint[] pts, int n, RustSpawnParams p,
            ushort configId, ushort ownerId, uint baseId)
        {
            for (int i = 0; i < n; i++)
            {
                float speed = pts[i].Speed > 0f ? pts[i].Speed : p.Speed;
                var (ax, ay, az) = GetAccel3D(pts[i].Direction, p.MovementType, p.GravityAy);

                _temp3D[i] = new NativeProjectile3D
                {
                    X            = pts[i].Origin.x,
                    Y            = pts[i].Origin.y,
                    Z            = pts[i].Origin.z,
                    Vx           = pts[i].Direction.x * speed,
                    Vy           = pts[i].Direction.y * speed,
                    Vz           = pts[i].Direction.z * speed,
                    Ax           = ax,
                    Ay           = ay,
                    Az           = az,
                    ScaleX       = p.ScaleStart,
                    ScaleY       = p.ScaleStart,
                    ScaleZ       = p.ScaleStart,
                    ScaleTarget  = p.ScaleTarget,
                    ScaleSpeed   = p.ScaleSpeed,
                    Lifetime     = p.Lifetime,
                    MaxLifetime  = p.Lifetime,
                    TravelDist   = 0f,
                    TimerT       = 0f,
                    ConfigId     = configId,
                    OwnerId      = ownerId,
                    ProjId       = baseId + (uint)i,
                    CollisionCount = 0,
                    MovementType = p.MovementType,
                    PiercingType = p.PiercingType,
                    Alive        = 1
                };
            }
        }

        // ── Burst fill ────────────────────────────────────────────────────────

        private static void FillBurst2D(
            SpawnPoint[] pts, int n, RustSpawnParams p,
            ushort configId, ushort ownerId, uint baseId)
        {
            using var nativePts = new NativeArray<SpawnPoint>(pts, Allocator.TempJob);
            using var nativeOut = new NativeArray<NativeProjectile>(n, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            new BurstFill2DJob
            {
                SpawnPoints  = nativePts,
                Out          = nativeOut,
                DefaultSpeed = p.Speed,
                MovementType = p.MovementType,
                PiercingType = p.PiercingType,
                GravityAy    = p.GravityAy,
                Lifetime     = p.Lifetime,
                ScaleStart   = p.ScaleStart,
                ScaleTarget  = p.ScaleTarget,
                ScaleSpeed   = p.ScaleSpeed,
                ConfigId     = configId,
                OwnerId      = ownerId,
                BaseId       = baseId
            }.Schedule(n, 64).Complete();

            NativeArray<NativeProjectile>.Copy(nativeOut, 0, _temp2D, 0, n);
        }

        private static void FillBurst3D(
            SpawnPoint[] pts, int n, RustSpawnParams p,
            ushort configId, ushort ownerId, uint baseId)
        {
            using var nativePts = new NativeArray<SpawnPoint>(pts, Allocator.TempJob);
            using var nativeOut = new NativeArray<NativeProjectile3D>(n, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            new BurstFill3DJob
            {
                SpawnPoints  = nativePts,
                Out          = nativeOut,
                DefaultSpeed = p.Speed,
                MovementType = p.MovementType,
                PiercingType = p.PiercingType,
                GravityAy    = p.GravityAy,
                Lifetime     = p.Lifetime,
                ScaleStart   = p.ScaleStart,
                ScaleTarget  = p.ScaleTarget,
                ScaleSpeed   = p.ScaleSpeed,
                ConfigId     = configId,
                OwnerId      = ownerId,
                BaseId       = baseId
            }.Schedule(n, 64).Complete();

            NativeArray<NativeProjectile3D>.Copy(nativeOut, 0, _temp3D, 0, n);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Burst jobs
    // ─────────────────────────────────────────────────────────────────────────

    [BurstCompile]
    public struct BurstFill2DJob : IJobParallelFor
    {
        [ReadOnly]  public NativeArray<SpawnPoint>       SpawnPoints;
        [WriteOnly] public NativeArray<NativeProjectile> Out;

        public float  DefaultSpeed;
        public byte   MovementType;
        public byte   PiercingType;
        public float  GravityAy;
        public float  Lifetime;
        public float  ScaleStart;
        public float  ScaleTarget;
        public float  ScaleSpeed;
        public ushort ConfigId;
        public ushort OwnerId;
        public uint   BaseId;

        private const byte MOVE_WAVE     = 4;
        private const byte MOVE_CIRCULAR = 5;

        [BurstCompile]
        public void Execute(int i)
        {
            var   pt  = SpawnPoints[i];
            float spd = pt.Speed > 0f ? pt.Speed : DefaultSpeed;
            float ang = math.atan2(pt.Direction.y, pt.Direction.x) * math.degrees(1f);

            float ax, ay;
            if (MovementType == MOVE_WAVE || MovementType == MOVE_CIRCULAR)
            {
                ax = -pt.Direction.y;
                ay =  pt.Direction.x;
            }
            else
            {
                ax = 0f;
                ay = GravityAy;
            }

            Out[i] = new NativeProjectile
            {
                X              = pt.Origin.x,
                Y              = pt.Origin.y,
                Vx             = pt.Direction.x * spd,
                Vy             = pt.Direction.y * spd,
                Ax             = ax,
                Ay             = ay,
                AngleDeg       = ang,
                CurveT         = 0f,
                ScaleX         = ScaleStart,
                ScaleY         = ScaleStart,
                ScaleTarget    = ScaleTarget,
                ScaleSpeed     = ScaleSpeed,
                Lifetime       = Lifetime,
                MaxLifetime    = Lifetime,
                TravelDist     = 0f,
                ConfigId       = ConfigId,
                OwnerId        = OwnerId,
                ProjId         = BaseId + (uint)i,
                CollisionCount = 0,
                MovementType   = MovementType,
                PiercingType   = PiercingType,
                Alive          = 1
            };
        }
    }

    [BurstCompile]
    public struct BurstFill3DJob : IJobParallelFor
    {
        [ReadOnly]  public NativeArray<SpawnPoint>         SpawnPoints;
        [WriteOnly] public NativeArray<NativeProjectile3D> Out;

        public float  DefaultSpeed;
        public byte   MovementType;
        public byte   PiercingType;
        public float  GravityAy;
        public float  Lifetime;
        public float  ScaleStart;
        public float  ScaleTarget;
        public float  ScaleSpeed;
        public ushort ConfigId;
        public ushort OwnerId;
        public uint   BaseId;

        private const byte MOVE_WAVE     = 4;
        private const byte MOVE_CIRCULAR = 5;

        [BurstCompile]
        public void Execute(int i)
        {
            var   pt  = SpawnPoints[i];
            float spd = pt.Speed > 0f ? pt.Speed : DefaultSpeed;

            float ax, ay, az;
            if (MovementType == MOVE_WAVE || MovementType == MOVE_CIRCULAR)
            {
                float xyLen = math.sqrt(pt.Direction.x * pt.Direction.x
                                      + pt.Direction.y * pt.Direction.y);
                if (xyLen > 0.001f)
                { ax = -pt.Direction.y / xyLen; ay = pt.Direction.x / xyLen; az = 0f; }
                else
                { ax = 1f; ay = 0f; az = 0f; }
            }
            else
            {
                ax = 0f; ay = GravityAy; az = 0f;
            }

            Out[i] = new NativeProjectile3D
            {
                X              = pt.Origin.x,
                Y              = pt.Origin.y,
                Z              = pt.Origin.z,
                Vx             = pt.Direction.x * spd,
                Vy             = pt.Direction.y * spd,
                Vz             = pt.Direction.z * spd,
                Ax             = ax,
                Ay             = ay,
                Az             = az,
                ScaleX         = ScaleStart,
                ScaleY         = ScaleStart,
                ScaleZ         = ScaleStart,
                ScaleTarget    = ScaleTarget,
                ScaleSpeed     = ScaleSpeed,
                Lifetime       = Lifetime,
                MaxLifetime    = Lifetime,
                TravelDist     = 0f,
                TimerT         = 0f,
                ConfigId       = ConfigId,
                OwnerId        = OwnerId,
                ProjId         = BaseId + (uint)i,
                CollisionCount = 0,
                MovementType   = MovementType,
                PiercingType   = PiercingType,
                Alive          = 1
            };
        }
    }
}
