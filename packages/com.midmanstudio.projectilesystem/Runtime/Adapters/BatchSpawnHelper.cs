// BatchSpawnHelper.cs
// Spawn path decider and batch builder.
//
// Problem: Rust spawn_pattern has ~928µs per-call FFI overhead.
// Spawning 8 shotgun pellets individually = 8 × 928µs = 7.4ms wasted on FFI.
//
// Solution:
//   1. C# or Burst builds ALL spawn structs in one array.
//   2. Call spawn_batch ONCE — single FFI crossing for any number of projectiles.
//   3. For 8+ projectiles, BurstSpawnJob fills the array in parallel.
//   4. For < 8 projectiles, a C# loop fills the array (Burst overhead > gain).

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
    // ─────────────────────────────────────────────────────────────────────────
    //  Spawn point — output of pattern library, input to batch builder
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A single spawn point produced by a shot pattern calculation.
    /// The pattern library fills an array of these; BatchSpawnHelper maps
    /// them to NativeProjectile structs.
    /// </summary>
    public struct SpawnPoint
    {
        /// <summary>World-space spawn origin.</summary>
        public Vector3 Origin;

        /// <summary>Normalised travel direction.</summary>
        public Vector3 Direction;

        /// <summary>Speed for this specific projectile (may vary per pellet).</summary>
        public float Speed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  BatchSpawnHelper
    // ─────────────────────────────────────────────────────────────────────────

    public static class BatchSpawnHelper
    {
        /// <summary>
        /// Minimum projectile count to justify BurstSpawnJob scheduling overhead.
        /// Below this threshold, a C# loop is faster.
        /// </summary>
        public const int BurstThreshold = 8;

        private static readonly NativeProjectile[]   _temp2D = new NativeProjectile[256];
        private static readonly NativeProjectile3D[] _temp3D = new NativeProjectile3D[256];
        private static GCHandle _pin2D;
        private static GCHandle _pin3D;
        private static bool     _pinsAllocated;

        // ── Initialisation ────────────────────────────────────────────────────

        /// <summary>Pin temp buffers. Call before any spawn.</summary>
        public static void Initialise()
        {
            if (_pinsAllocated) return;
            _pin2D = GCHandle.Alloc(_temp2D, GCHandleType.Pinned);
            _pin3D = GCHandle.Alloc(_temp3D, GCHandleType.Pinned);
            _pinsAllocated = true;
        }

        /// <summary>Unpin temp buffers. Call on shutdown.</summary>
        public static void Shutdown()
        {
            if (!_pinsAllocated) return;
            if (_pin2D.IsAllocated) _pin2D.Free();
            if (_pin3D.IsAllocated) _pin3D.Free();
            _pinsAllocated = false;
        }

        // ── 2D Spawn ──────────────────────────────────────────────────────────

        /// <summary>
        /// Spawn a batch of 2D projectiles from pre-computed spawn points.
        /// config parameter is optional context; all sim params come from rustParams.
        /// </summary>
        public static int SpawnBatch2D(
            SpawnPoint[]     spawnPoints,
            int              count,
            ProjectileConfigSO config,      // optional; params come from rustParams
            RustSpawnParams  rustParams,
            ushort           configId,
            ushort           ownerId,
            uint             nextProjId,
            IntPtr           projsOutPtr,
            int              bufferRemaining,
            float            latencyCompensation = 0f)
        {
            if (!_pinsAllocated)
            {
                Debug.LogError("[BatchSpawnHelper] Not initialised — call Initialise() first.");
                return 0;
            }

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
                    p.X        += p.Vx * latencyCompensation;
                    p.Y        += p.Vy * latencyCompensation;
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
            if (!_pinsAllocated)
            {
                Debug.LogError("[BatchSpawnHelper] Not initialised — call Initialise() first.");
                return 0;
            }

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
                    p.X        += p.Vx * latencyCompensation;
                    p.Y        += p.Vy * latencyCompensation;
                    p.Z        += p.Vz * latencyCompensation;
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

        // ── Managed fill (count < BurstThreshold) ─────────────────────────────

        private static void FillManaged2D(
            SpawnPoint[] pts, int n, RustSpawnParams p,
            ushort configId, ushort ownerId, uint baseId)
        {
            for (int i = 0; i < n; i++)
            {
                float speed = pts[i].Speed > 0f ? pts[i].Speed : p.Speed;
                _temp2D[i] = new NativeProjectile
                {
                    X              = pts[i].Origin.x,
                    Y              = pts[i].Origin.y,
                    Vx             = pts[i].Direction.x * speed,
                    Vy             = pts[i].Direction.y * speed,
                    Ax             = 0f,
                    Ay             = p.GravityAy,
                    AngleDeg       = Mathf.Atan2(pts[i].Direction.y, pts[i].Direction.x) * Mathf.Rad2Deg,
                    CurveT         = 0f,
                    ScaleX         = p.ScaleStart,
                    ScaleY         = p.ScaleStart,
                    ScaleTarget    = p.ScaleTarget,
                    ScaleSpeed     = p.ScaleSpeed,
                    Lifetime       = p.Lifetime,
                    MaxLifetime    = p.Lifetime,
                    TravelDist     = 0f,
                    ConfigId       = configId,
                    OwnerId        = ownerId,
                    ProjId         = baseId + (uint)i,
                    CollisionCount = 0,
                    MovementType   = p.MovementType,
                    PiercingType   = p.PiercingType,
                    Alive          = 1
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
                _temp3D[i] = new NativeProjectile3D
                {
                    X              = pts[i].Origin.x,
                    Y              = pts[i].Origin.y,
                    Z              = pts[i].Origin.z,
                    Vx             = pts[i].Direction.x * speed,
                    Vy             = pts[i].Direction.y * speed,
                    Vz             = pts[i].Direction.z * speed,
                    Ax             = 0f,
                    Ay             = p.GravityAy,
                    Az             = 0f,
                    ScaleX         = p.ScaleStart,
                    ScaleY         = p.ScaleStart,
                    ScaleZ         = p.ScaleStart,
                    ScaleTarget    = p.ScaleTarget,
                    ScaleSpeed     = p.ScaleSpeed,
                    Lifetime       = p.Lifetime,
                    MaxLifetime    = p.Lifetime,
                    TravelDist     = 0f,
                    TimerT         = 0f,
                    ConfigId       = configId,
                    OwnerId        = ownerId,
                    ProjId         = baseId + (uint)i,
                    CollisionCount = 0,
                    MovementType   = p.MovementType,
                    PiercingType   = p.PiercingType,
                    Alive          = 1
                };
            }
        }

        // ── Burst fill (count >= BurstThreshold) ──────────────────────────────

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

            nativeOut.CopyTo(_temp2D);
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

            nativeOut.CopyTo(_temp3D);
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

        [BurstCompile]
        public void Execute(int i)
        {
            var   pt  = SpawnPoints[i];
            float spd = pt.Speed > 0f ? pt.Speed : DefaultSpeed;
            float ang = math.atan2(pt.Direction.y, pt.Direction.x) * math.degrees(1f);

            Out[i] = new NativeProjectile
            {
                X              = pt.Origin.x,
                Y              = pt.Origin.y,
                Vx             = pt.Direction.x * spd,
                Vy             = pt.Direction.y * spd,
                Ax             = 0f,
                Ay             = GravityAy,
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

        [BurstCompile]
        public void Execute(int i)
        {
            var   pt  = SpawnPoints[i];
            float spd = pt.Speed > 0f ? pt.Speed : DefaultSpeed;

            Out[i] = new NativeProjectile3D
            {
                X              = pt.Origin.x,
                Y              = pt.Origin.y,
                Z              = pt.Origin.z,
                Vx             = pt.Direction.x * spd,
                Vy             = pt.Direction.y * spd,
                Vz             = pt.Direction.z * spd,
                Ax             = 0f,
                Ay             = GravityAy,
                Az             = 0f,
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
