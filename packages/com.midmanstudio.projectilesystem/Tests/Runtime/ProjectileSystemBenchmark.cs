// packages/com.midmanstudio.projectilesystem/Tests/Runtime/ProjectileSystemBenchmark.cs
// Runtime benchmark comparing the Rust-native projectile sim against equivalent
// managed C# approaches. Open the companion editor window via:
//   MidManStudio > Utilities > Tests > Projectile System Bench
//
// WHAT IS TESTED:
//   TickBench      — Rust FFI tick_projectiles vs an equivalent C# position loop
//   CollisionBench — Rust spatial-grid check_hits_grid_ex vs Physics2D.OverlapCircleNonAlloc
//   SpawnBench     — BatchSpawnHelper managed path (<8) vs Burst path (>=8)
//
// Attach to any scene GameObject. Does NOT require LocalLobbyManager.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Adapters;

namespace TestGame
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Result types
    // ─────────────────────────────────────────────────────────────────────────

    [Serializable]
    public struct TickBenchResult
    {
        public string Label;
        public int    ProjectileCount;
        public int    Iterations;
        public double AvgMs;
        public double MinMs;
        public double MaxMs;
        public double ThroughputKPerMs;  // thousands of projectiles ticked per ms
        public bool   Valid;

        public string Summary => Valid
            ? $"avg {AvgMs:F4}ms  min {MinMs:F4}ms  max {MaxMs:F4}ms  " +
              $"throughput {ThroughputKPerMs:F1}k/ms"
            : "not run";
    }

    [Serializable]
    public struct CollisionBenchResult
    {
        public string Label;
        public int    ProjectileCount;
        public int    TargetCount;
        public int    Iterations;
        public double AvgMs;
        public double MinMs;
        public double MaxMs;
        public double AvgHits;
        public bool   Valid;

        public string Summary => Valid
            ? $"avg {AvgMs:F4}ms  hits/call {AvgHits:F1}  {Label}"
            : "not run";
    }

    [Serializable]
    public struct SpawnBenchResult
    {
        public string Label;
        public int    SpawnCount;
        public int    Iterations;
        public double AvgMs;
        public double AvgUsPerProjectile;  // microseconds per individual projectile
        public bool   Valid;

        public string Summary => Valid
            ? $"avg {AvgMs:F4}ms  {AvgUsPerProjectile:F2}µs/proj  ({Label})"
            : "not run";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Benchmark MonoBehaviour
    // ─────────────────────────────────────────────────────────────────────────

    public class ProjectileSystemBenchmark : MonoBehaviour
    {
        #region Inspector

        [Header("Tick Benchmark")]
        [SerializeField] private int[] _tickProjectileCounts = { 100, 500, 1000, 2000 };
        [SerializeField] private int   _tickIterations       = 2000;
        [SerializeField] private int   _tickWarmupCount      = 200;

        [Header("Collision Benchmark")]
        [SerializeField] private int[] _collisionProjCounts     = { 256, 512, 1024 };
        [SerializeField] private int[] _collisionTargetCounts   = { 16, 32, 64 };
        [SerializeField] private int   _collisionIterations     = 500;
        [SerializeField] private float _testCellSize            = 4f;

        [Header("Spawn Benchmark")]
        [SerializeField] private int[] _spawnCounts     = { 1, 4, 8, 32, 64, 128 };
        [SerializeField] private int   _spawnIterations = 1000;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        #endregion

        #region Results  (read by editor window)

        public bool IsRunning { get; private set; }
        public string StatusMessage { get; private set; } = "Idle.";
        public float Progress { get; private set; }

        public List<TickBenchResult>      TickResults      = new();
        public List<CollisionBenchResult> CollisionResults = new();
        public List<SpawnBenchResult>     SpawnResults     = new();

        #endregion

        #region Private Buffers

        // Max buffer sizes — allocated once in Awake, freed in OnDestroy
        private const int MaxProjs   = 2048;
        private const int MaxTargets = 128;
        private const int MaxHits    = 512;

        private NativeProjectile[]   _projs2D;
        private CollisionTarget[]    _targets;
        private HitResult[]          _hits;

        private GCHandle _pinProjs;
        private GCHandle _pinTargets;
        private GCHandle _pinHits;
        private bool     _buffersReady;

        private SpawnPoint[]      _spawnPts;
        private NativeProjectile[] _spawnTemp;
        private GCHandle          _pinSpawnTemp;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            AllocateBuffers();
            BatchSpawnHelper.Initialise();
        }

        private void OnDestroy()
        {
            BatchSpawnHelper.Shutdown();
            FreeBuffers();
        }

        #endregion

        #region Public Run API

        public void RunAll()
        {
            if (IsRunning) return;
            TickResults.Clear();
            CollisionResults.Clear();
            SpawnResults.Clear();
            StartCoroutine(RunAllCoroutine());
        }

        public void RunTickBenchOnly()
        {
            if (IsRunning) return;
            TickResults.Clear();
            StartCoroutine(RunTickCoroutine());
        }

        public void RunCollisionBenchOnly()
        {
            if (IsRunning) return;
            CollisionResults.Clear();
            StartCoroutine(RunCollisionCoroutine());
        }

        public void RunSpawnBenchOnly()
        {
            if (IsRunning) return;
            SpawnResults.Clear();
            StartCoroutine(RunSpawnCoroutine());
        }

        public void Cancel()
        {
            StopAllCoroutines();
            IsRunning = false;
            SetStatus("Cancelled.");
            Progress = 0f;
        }

        #endregion

        #region Master Coroutine

        private System.Collections.IEnumerator RunAllCoroutine()
        {
            IsRunning = true;
            yield return StartCoroutine(RunTickCoroutine());
            yield return StartCoroutine(RunCollisionCoroutine());
            yield return StartCoroutine(RunSpawnCoroutine());
            SetStatus("All benchmarks complete.");
            Progress  = 1f;
            IsRunning = false;
        }

        #endregion

        #region Tick Benchmark

        private System.Collections.IEnumerator RunTickCoroutine()
        {
            IsRunning = true;

            for (int ci = 0; ci < _tickProjectileCounts.Length; ci++)
            {
                int n = _tickProjectileCounts[ci];
                n     = Mathf.Min(n, MaxProjs);

                SetStatus($"Tick bench — {n} projectiles (warmup)…");
                FillProjs2D(n, straight: true);

                // Warmup
                for (int w = 0; w < _tickWarmupCount; w++)
                    ProjectileLib.tick_projectiles(
                        _pinProjs.AddrOfPinnedObject(), n, 0.016f);

                // Rust FFI tick
                SetStatus($"Tick bench — Rust FFI {n} projs × {_tickIterations} ticks…");
                var rustResult = MeasureTick_Rust(n, _tickIterations);
                rustResult.Label = $"Rust FFI  ({n} projs)";
                TickResults.Add(rustResult);

                MID_Logger.LogInfo(_logLevel,
                    $"[Tick] Rust {n} projs: {rustResult.Summary}",
                    nameof(ProjectileSystemBenchmark));

                // Managed C# equivalent tick
                SetStatus($"Tick bench — Managed C# {n} projs × {_tickIterations} ticks…");
                var managedResult = MeasureTick_Managed(n, _tickIterations);
                managedResult.Label = $"C# Loop   ({n} projs)";
                TickResults.Add(managedResult);

                MID_Logger.LogInfo(_logLevel,
                    $"[Tick] C#  {n} projs: {managedResult.Summary}",
                    nameof(ProjectileSystemBenchmark));

                Progress = (ci + 1f) / (_tickProjectileCounts.Length * 3f);
                yield return null;
            }
        }

        private TickBenchResult MeasureTick_Rust(int n, int iters)
        {
            FillProjs2D(n, straight: true);
            var sw   = new Stopwatch();
            double freq  = Stopwatch.Frequency;
            double total = 0, min = double.MaxValue, max = 0;

            for (int i = 0; i < iters; i++)
            {
                // Refresh alive flags each call so tick has real work
                RefreshAlive(n);

                sw.Restart();
                ProjectileLib.tick_projectiles(
                    _pinProjs.AddrOfPinnedObject(), n, 0.016f);
                sw.Stop();

                double ms = sw.ElapsedTicks / freq * 1000.0;
                total += ms;
                if (ms < min) min = ms;
                if (ms > max) max = ms;
            }

            double avg = total / iters;
            return new TickBenchResult
            {
                ProjectileCount   = n,
                Iterations        = iters,
                AvgMs             = avg,
                MinMs             = min,
                MaxMs             = max,
                ThroughputKPerMs  = avg > 0 ? n / avg / 1000.0 : 0,
                Valid             = true
            };
        }

        private TickBenchResult MeasureTick_Managed(int n, int iters)
        {
            FillProjs2D(n, straight: true);
            var sw   = new Stopwatch();
            double freq  = Stopwatch.Frequency;
            double total = 0, min = double.MaxValue, max = 0;

            for (int i = 0; i < iters; i++)
            {
                RefreshAlive(n);
                sw.Restart();
                ManagedTickStraight(_projs2D, n, 0.016f);
                sw.Stop();

                double ms = sw.ElapsedTicks / freq * 1000.0;
                total += ms;
                if (ms < min) min = ms;
                if (ms > max) max = ms;
            }

            double avg = total / iters;
            return new TickBenchResult
            {
                ProjectileCount  = n,
                Iterations       = iters,
                AvgMs            = avg,
                MinMs            = min,
                MaxMs            = max,
                ThroughputKPerMs = avg > 0 ? n / avg / 1000.0 : 0,
                Valid            = true
            };
        }

        /// <summary>
        /// Pure C# equivalent of a straight-movement tick — used as a managed baseline.
        /// Matches what the Rust sim does for MOVE_STRAIGHT.
        /// </summary>
        private static void ManagedTickStraight(NativeProjectile[] projs, int count, float dt)
        {
            for (int i = 0; i < count; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;
                p.Lifetime -= dt;
                if (p.Lifetime <= 0f) { p.Alive = 0; continue; }
                p.Vx += p.Ax * dt;
                p.Vy += p.Ay * dt;
                p.X  += p.Vx * dt;
                p.Y  += p.Vy * dt;
                p.TravelDist += Mathf.Sqrt(p.Vx * p.Vx * dt * dt + p.Vy * p.Vy * dt * dt);
            }
        }

        #endregion

        #region Collision Benchmark

        private System.Collections.IEnumerator RunCollisionCoroutine()
        {
            IsRunning = true;
            int step  = 0;
            int total = _collisionProjCounts.Length * _collisionTargetCounts.Length;

            foreach (int np in _collisionProjCounts)
            {
                foreach (int nt in _collisionTargetCounts)
                {
                    int n = Mathf.Min(np, MaxProjs);
                    int m = Mathf.Min(nt, MaxTargets);

                    SetStatus($"Collision bench — Rust grid {n}p × {m}t…");
                    var rustResult = MeasureCollision_Rust(n, m, _collisionIterations);
                    rustResult.Label = $"Rust grid  {n}p×{m}t";
                    CollisionResults.Add(rustResult);

                    MID_Logger.LogInfo(_logLevel,
                        $"[Collision] Rust {n}p×{m}t: {rustResult.Summary}",
                        nameof(ProjectileSystemBenchmark));

                    SetStatus($"Collision bench — Physics2D {n}p × {m}t…");
                    var physResult = MeasureCollision_Physics2D(n, m, _collisionIterations);
                    physResult.Label = $"Physics2D  {n}p×{m}t";
                    CollisionResults.Add(physResult);

                    MID_Logger.LogInfo(_logLevel,
                        $"[Collision] Phys {n}p×{m}t: {physResult.Summary}",
                        nameof(ProjectileSystemBenchmark));

                    step++;
                    Progress = (TickResults.Count > 0 ? 0.33f : 0f)
                             + 0.33f * (step / (float)total);
                    yield return null;
                }
            }
        }

        private CollisionBenchResult MeasureCollision_Rust(int np, int nt, int iters)
        {
            FillProjs2D(np, straight: true);
            FillTargets(nt);

            var sw   = new Stopwatch();
            double freq  = Stopwatch.Frequency;
            double total = 0, min = double.MaxValue, max = 0;
            long   totalHits = 0;

            for (int i = 0; i < iters; i++)
            {
                sw.Restart();
                ProjectileLib.check_hits_grid_ex(
                    _pinProjs.AddrOfPinnedObject(),    np,
                    _pinTargets.AddrOfPinnedObject(),  nt,
                    _pinHits.AddrOfPinnedObject(),     MaxHits,
                    _testCellSize,
                    out int hitCount);
                sw.Stop();

                totalHits += hitCount;
                double ms  = sw.ElapsedTicks / freq * 1000.0;
                total += ms;
                if (ms < min) min = ms;
                if (ms > max) max = ms;
            }

            return new CollisionBenchResult
            {
                ProjectileCount = np,
                TargetCount     = nt,
                Iterations      = iters,
                AvgMs           = total / iters,
                MinMs           = min,
                MaxMs           = max,
                AvgHits         = (double)totalHits / iters,
                Valid           = true
            };
        }

        private CollisionBenchResult MeasureCollision_Physics2D(int np, int nt, int iters)
        {
            // Simulate Physics2D.OverlapCircleNonAlloc equivalent:
            // For each alive projectile, do an overlap check.
            // This is what a traditional Unity projectile system would do.
            FillProjs2D(np, straight: true);
            var physTargets = new Collider2D[MaxTargets];

            var sw   = new Stopwatch();
            double freq  = Stopwatch.Frequency;
            double total = 0, min = double.MaxValue, max = 0;
            long   totalHits = 0;

            for (int iter = 0; iter < iters; iter++)
            {
                sw.Restart();
                int hits = 0;
                for (int i = 0; i < np; i++)
                {
                    ref var p = ref _projs2D[i];
                    if (p.Alive == 0) continue;
                    // Real Physics2D call (no colliders in test scene → always returns 0,
                    // but the overhead of the call is what we're measuring)
                    int found = Physics2D.OverlapCircleNonAlloc(
                        new Vector2(p.X, p.Y), p.ScaleX * 0.5f, physTargets);
                    hits += found;
                }
                sw.Stop();

                totalHits += hits;
                double ms  = sw.ElapsedTicks / freq * 1000.0;
                total += ms;
                if (ms < min) min = ms;
                if (ms > max) max = ms;
            }

            return new CollisionBenchResult
            {
                ProjectileCount = np,
                TargetCount     = nt,
                Iterations      = iters,
                AvgMs           = total / iters,
                MinMs           = min,
                MaxMs           = max,
                AvgHits         = (double)totalHits / iters,
                Valid           = true
            };
        }

        #endregion

        #region Spawn Benchmark

        private System.Collections.IEnumerator RunSpawnCoroutine()
        {
            IsRunning = true;

            var rustParams = new RustSpawnParams
            {
                Speed         = 15f,
                MovementType  = 0,
                PiercingType  = 0,
                MaxCollisions = 1,
                Lifetime      = 3f,
                GravityAy     = 0f,
                ScaleStart    = 1f,
                ScaleTarget   = 1f,
                ScaleSpeed    = 0f,
                Is3D          = false
            };

            for (int ci = 0; ci < _spawnCounts.Length; ci++)
            {
                int n = _spawnCounts[ci];
                BuildSpawnPoints(n);

                SetStatus($"Spawn bench — {n} projectiles × {_spawnIterations} iter…");

                var result = MeasureSpawn(n, rustParams, _spawnIterations);
                result.Label = n < BatchSpawnHelper.BurstThreshold
                    ? $"C# managed  (count={n}, < Burst threshold {BatchSpawnHelper.BurstThreshold})"
                    : $"Burst fill  (count={n}, >= Burst threshold {BatchSpawnHelper.BurstThreshold})";

                SpawnResults.Add(result);

                MID_Logger.LogInfo(_logLevel,
                    $"[Spawn] {n} projs: {result.Summary}",
                    nameof(ProjectileSystemBenchmark));

                Progress = (TickResults.Count > 0 ? 0.33f : 0f)
                         + (CollisionResults.Count > 0 ? 0.33f : 0f)
                         + 0.34f * (ci + 1f) / _spawnCounts.Length;
                yield return null;
            }

            IsRunning = false;
        }

        private SpawnBenchResult MeasureSpawn(int n, RustSpawnParams rustParams, int iters)
        {
            var sw   = new Stopwatch();
            double freq  = Stopwatch.Frequency;
            double total = 0;

            // Write into a scratch area of the preallocated buffer
            var writePtr = _pinSpawnTemp.AddrOfPinnedObject();
            int capacity = _spawnTemp.Length;

            for (int i = 0; i < iters; i++)
            {
                sw.Restart();
                BatchSpawnHelper.SpawnBatch2D(
                    _spawnPts, n,
                    null,             // config SO not needed for bench
                    rustParams,
                    configId:  0,
                    ownerId:   0,
                    nextProjId: (uint)(i * n),
                    projsOutPtr: writePtr,
                    bufferRemaining: capacity,
                    latencyCompensation: 0f);
                sw.Stop();

                total += sw.ElapsedTicks / freq * 1000.0;
            }

            double avgMs = total / iters;
            return new SpawnBenchResult
            {
                SpawnCount         = n,
                Iterations         = iters,
                AvgMs              = avgMs,
                AvgUsPerProjectile = n > 0 ? avgMs * 1000.0 / n : 0,
                Valid              = true
            };
        }

        #endregion

        #region Buffer Helpers

        private void AllocateBuffers()
        {
            _projs2D   = new NativeProjectile[MaxProjs];
            _targets   = new CollisionTarget[MaxTargets];
            _hits      = new HitResult[MaxHits];
            _spawnTemp = new NativeProjectile[256];
            _spawnPts  = new SpawnPoint[256];

            _pinProjs      = GCHandle.Alloc(_projs2D,   GCHandleType.Pinned);
            _pinTargets    = GCHandle.Alloc(_targets,   GCHandleType.Pinned);
            _pinHits       = GCHandle.Alloc(_hits,      GCHandleType.Pinned);
            _pinSpawnTemp  = GCHandle.Alloc(_spawnTemp, GCHandleType.Pinned);

            _buffersReady = true;

            MID_Logger.LogInfo(_logLevel, "Benchmark buffers allocated.",
                nameof(ProjectileSystemBenchmark));
        }

        private void FreeBuffers()
        {
            if (!_buffersReady) return;
            if (_pinProjs.IsAllocated)     _pinProjs.Free();
            if (_pinTargets.IsAllocated)   _pinTargets.Free();
            if (_pinHits.IsAllocated)      _pinHits.Free();
            if (_pinSpawnTemp.IsAllocated) _pinSpawnTemp.Free();
            _buffersReady = false;
        }

        private void FillProjs2D(int count, bool straight)
        {
            for (int i = 0; i < count; i++)
            {
                float angle = i / (float)count * Mathf.PI * 2f;
                _projs2D[i] = new NativeProjectile
                {
                    X            = UnityEngine.Random.Range(-20f, 20f),
                    Y            = UnityEngine.Random.Range(-20f, 20f),
                    Vx           = Mathf.Cos(angle) * 15f,
                    Vy           = Mathf.Sin(angle) * 15f,
                    Ax = 0f, Ay = 0f,
                    AngleDeg     = angle * Mathf.Rad2Deg,
                    ScaleX       = 0.2f,
                    ScaleY       = 0.2f,
                    ScaleTarget  = 0.2f,
                    ScaleSpeed   = 0f,
                    Lifetime     = 3f,
                    MaxLifetime  = 3f,
                    TravelDist   = 0f,
                    MovementType = 0,
                    PiercingType = 0,
                    Alive        = 1,
                    ProjId       = (uint)i
                };
            }
        }

        private void RefreshAlive(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _projs2D[i].Alive    = 1;
                _projs2D[i].Lifetime = 3f;
            }
        }

        private void FillTargets(int count)
        {
            for (int i = 0; i < count; i++)
            {
                float angle    = i / (float)count * Mathf.PI * 2f;
                float dist     = UnityEngine.Random.Range(2f, 15f);
                _targets[i] = new CollisionTarget
                {
                    X        = Mathf.Cos(angle) * dist,
                    Y        = Mathf.Sin(angle) * dist,
                    Radius   = 0.6f,
                    TargetId = (uint)i,
                    Active   = 1
                };
            }
        }

        private void BuildSpawnPoints(int count)
        {
            for (int i = 0; i < Mathf.Min(count, _spawnPts.Length); i++)
            {
                float a      = i / (float)Mathf.Max(count, 1) * Mathf.PI * 2f;
                _spawnPts[i] = new SpawnPoint
                {
                    Origin    = Vector3.zero,
                    Direction = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f),
                    Speed     = 15f
                };
            }
        }

        private void SetStatus(string msg)
        {
            StatusMessage = msg;
            MID_Logger.LogDebug(_logLevel, msg, nameof(ProjectileSystemBenchmark));
        }

        #endregion
    }
}
