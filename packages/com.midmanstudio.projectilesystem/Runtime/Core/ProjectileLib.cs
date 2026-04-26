// ProjectileLib.cs
// Complete FFI layer for projectile_core Rust native library.
// ALL P/Invoke bindings live here. Nothing else imports DllImport.
//
// iOS: IL2CPP links the static lib as __Internal.
//      The conditional at the top switches the DLL name at build time.
//
// Startup validation:
//   Call ProjectileLib.ValidateStructSizes() before any FFI call.
//   A struct size mismatch causes silent memory corruption on every P/Invoke.
//   MID_MasterProjectileSystem calls this in Awake() — do not skip it.
//
// Movement type constants:
//   Fetched from Rust at startup via movement_type_*() functions.
//   This guarantees C# and Rust always agree even if constants shift.
//   Use ProjectileLib.MovementTypes after calling FetchMovementTypeConstants().
//
// Struct size reference (must match Rust repr(C) exactly):
//   NativeProjectile    = 72 bytes   (2D)
//   HitResult           = 24 bytes   (2D)
//   CollisionTarget     = 20 bytes   (2D)
//   SpawnRequest        = 32 bytes
//   NativeProjectile3D  = 84 bytes   (3D)
//   HitResult3D         = 28 bytes   (3D)
//   CollisionTarget3D   = 24 bytes   (3D)

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  2D FFI structs
    //  NativeProjectile is defined in NativeProjectile.cs (keep_unchanged).
    //  SpawnRequest, CollisionTarget, HitResult defined here.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawn request passed to spawn_pattern (legacy) or used as a template
    /// by BatchSpawnHelper before calling spawn_batch.
    /// 32 bytes — must match Rust SpawnRequest repr(C) exactly.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct SpawnRequest
    {
        [FieldOffset(0)]  public float  OriginX;
        [FieldOffset(4)]  public float  OriginY;
        [FieldOffset(8)]  public float  AngleDeg;
        [FieldOffset(12)] public float  Speed;
        [FieldOffset(16)] public ushort ConfigId;
        [FieldOffset(18)] public ushort OwnerId;
        [FieldOffset(20)] public byte   PatternId;  // PatternId enum cast to byte
        // 3 bytes padding at 21–23 (compiler-inserted, matches Rust _pad: [u8;3])
        [FieldOffset(24)] public uint   RngSeed;
        [FieldOffset(28)] public uint   BaseProjId;
    }

    /// <summary>
    /// 2D hit event returned by check_hits_grid / check_hits_grid_ex.
    /// 24 bytes — must match Rust HitResult repr(C) exactly.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct HitResult
    {
        [FieldOffset(0)]  public uint  ProjId;
        [FieldOffset(4)]  public uint  ProjIndex;   // index into 2D sim buffer
        [FieldOffset(8)]  public uint  TargetId;
        [FieldOffset(12)] public float TravelDist;
        [FieldOffset(16)] public float HitX;
        [FieldOffset(20)] public float HitY;
    }

    /// <summary>
    /// 2D collision target sphere registered with the sim.
    /// 20 bytes — must match Rust CollisionTarget repr(C) exactly.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct CollisionTarget
    {
        [FieldOffset(0)]  public float X;
        [FieldOffset(4)]  public float Y;
        [FieldOffset(8)]  public float Radius;
        [FieldOffset(12)] public uint  TargetId;
        [FieldOffset(16)] public byte  Active;   // 1 = hittable, 0 = skip
        // 3 bytes padding at 17–19
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  3D FFI structs
    //  Defined here (not in NativeProjectile3D.cs) so the full FFI layer
    //  is in one file. NativeProjectile3D.cs re-exports via using aliases
    //  to avoid breaking call sites that imported from there.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Core 3D projectile state. 84 bytes.
    /// Must match Rust NativeProjectile3D repr(C) exactly.
    ///
    /// ax/ay/az meaning by movement type:
    ///   Straight/Arching : constant acceleration (gravity in ay)
    ///   Guided           : normalised homing target direction (C# writes via TickDispatcher)
    ///   Wave             : normalised perpendicular oscillation axis (set once at spawn)
    ///   Circular         : first perpendicular axis of orbital plane (set once at spawn)
    ///
    /// angle_deg is absent — C# derives rotation from Vx/Vy/Vz each frame.
    /// timer_t replaces curve_t: used by Arching (elapsed time) and Teleport (interval timer).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 84)]
    public struct NativeProjectile3D
    {
        // ── Position ─────────────────────────────────────────────────────────
        [FieldOffset(0)]  public float X;
        [FieldOffset(4)]  public float Y;
        [FieldOffset(8)]  public float Z;

        // ── Velocity ─────────────────────────────────────────────────────────
        [FieldOffset(12)] public float Vx;
        [FieldOffset(16)] public float Vy;
        [FieldOffset(20)] public float Vz;

        // ── Acceleration / homing / oscillation axis ──────────────────────────
        [FieldOffset(24)] public float Ax;
        [FieldOffset(28)] public float Ay;
        [FieldOffset(32)] public float Az;

        // ── Scale (opt-in growth — zero cost when ScaleSpeed == 0) ──────────
        [FieldOffset(36)] public float ScaleX;
        [FieldOffset(40)] public float ScaleY;
        [FieldOffset(44)] public float ScaleZ;
        [FieldOffset(48)] public float ScaleTarget;
        [FieldOffset(52)] public float ScaleSpeed;   // 0 = no growth, skip tick_scale_3d

        // ── Lifetime & travel ────────────────────────────────────────────────
        [FieldOffset(56)] public float Lifetime;
        [FieldOffset(60)] public float MaxLifetime;
        [FieldOffset(64)] public float TravelDist;
        [FieldOffset(68)] public float TimerT;       // arching elapsed / teleport interval

        // ── Identity ─────────────────────────────────────────────────────────
        [FieldOffset(72)] public ushort ConfigId;
        [FieldOffset(74)] public ushort OwnerId;
        [FieldOffset(76)] public uint   ProjId;

        // ── Flags ─────────────────────────────────────────────────────────────
        [FieldOffset(80)] public byte CollisionCount;
        [FieldOffset(81)] public byte MovementType;
        [FieldOffset(82)] public byte PiercingType;
        [FieldOffset(83)] public byte Alive;

        // ── Convenience helpers ───────────────────────────────────────────────

        public bool    IsAlive          => Alive != 0;
        public float   CollisionRadius  => ScaleX * 0.5f;
        public Vector3 Position         => new Vector3(X, Y, Z);

        public UnityEngine.Quaternion VisualRotation()
        {
            var v = new Vector3(Vx, Vy, Vz);
            return v.sqrMagnitude < 0.0001f
                ? UnityEngine.Quaternion.identity
                : UnityEngine.Quaternion.LookRotation(v.normalized, Vector3.up);
        }

        /// Set guided homing / wave perpendicular / circular first-perp direction.
        /// Does not need to be normalised — Rust normalises defensively in tick functions.
        public void SetAcceleration(Vector3 dir)
        {
            Ax = dir.x;
            Ay = dir.y;
            Az = dir.z;
        }
    }

    /// <summary>
    /// 3D hit event returned by check_hits_grid_3d.
    /// 28 bytes — must match Rust HitResult3D repr(C) exactly.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct HitResult3D
    {
        [FieldOffset(0)]  public uint  ProjId;
        [FieldOffset(4)]  public uint  ProjIndex;
        [FieldOffset(8)]  public uint  TargetId;
        [FieldOffset(12)] public float TravelDist;
        [FieldOffset(16)] public float HitX;
        [FieldOffset(20)] public float HitY;
        [FieldOffset(24)] public float HitZ;
    }

    /// <summary>
    /// 3D collision target sphere.
    /// 24 bytes — must match Rust CollisionTarget3D repr(C) exactly.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct CollisionTarget3D
    {
        [FieldOffset(0)]  public float X;
        [FieldOffset(4)]  public float Y;
        [FieldOffset(8)]  public float Z;
        [FieldOffset(12)] public float Radius;
        [FieldOffset(16)] public uint  TargetId;
        [FieldOffset(20)] public byte  Active;
        // 3 bytes padding at 21–23
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Enums (shared 2D / 3D)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawn pattern for the legacy spawn_pattern entry point.
    /// Not used by BatchSpawnHelper — patterns are C#-computed via ProjectilePatternSO.
    /// </summary>
    public enum PatternId : byte
    {
        Single  = 0,
        Spread3 = 1,
        Spread5 = 2,
        Spiral  = 3,
        Ring8   = 4
    }

    /// <summary>
    /// Projectile movement type byte constants.
    /// Values must match Rust simulation.rs constants exactly.
    /// Use ProjectileLib.MovementTypes (fetched from Rust at startup) instead of
    /// hardcoding these values — MovementTypes.Straight etc. guarantee agreement.
    /// </summary>
    public enum ProjectileMovementType : byte
    {
        Straight  = 0,
        Arching   = 1,
        Guided    = 2,
        Teleport  = 3,
        Wave      = 4,
        Circular  = 5
    }

    /// <summary>
    /// Piercing capability. Must match Rust PiercingType constants.
    /// </summary>
    public enum ProjectilePiercingType : byte
    {
        None   = 0,
        Piecer = 1,
        Random = 2
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Movement type constant cache
    //  Fetched from Rust at startup so C# and Rust always agree.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Movement type byte constants fetched from Rust at startup.
    /// Access via ProjectileLib.MovementTypes after calling
    /// ProjectileLib.FetchMovementTypeConstants().
    /// </summary>
    public static class MovementTypeConstants
    {
        public byte Straight  { get; internal set; }
        public byte Arching   { get; internal set; }
        public byte Guided    { get; internal set; }
        public byte Teleport  { get; internal set; }
        public byte Wave      { get; internal set; }
        public byte Circular  { get; internal set; }

        /// <summary>
        /// Validate that the C# enum values match the Rust constants.
        /// Logs an error if any constant drifted. Called by FetchMovementTypeConstants().
        /// </summary>
        internal void Validate()
        {
            bool ok = true;
            ok &= CheckConst("Straight",  Straight,  (byte)ProjectileMovementType.Straight);
            ok &= CheckConst("Arching",   Arching,   (byte)ProjectileMovementType.Arching);
            ok &= CheckConst("Guided",    Guided,    (byte)ProjectileMovementType.Guided);
            ok &= CheckConst("Teleport",  Teleport,  (byte)ProjectileMovementType.Teleport);
            ok &= CheckConst("Wave",      Wave,      (byte)ProjectileMovementType.Wave);
            ok &= CheckConst("Circular",  Circular,  (byte)ProjectileMovementType.Circular);

            if (!ok)
            {
                Debug.LogError(
                    "[ProjectileLib] Movement type constant mismatch between C# enum and Rust. " +
                    "Update ProjectileMovementType enum to match simulation.rs constants.");
            }
        }

        private static bool CheckConst(string name, byte rust, byte csharp)
        {
            if (rust == csharp) return true;
            Debug.LogError(
                $"[ProjectileLib] MovementType.{name}: Rust={rust}, C# enum={csharp}. MISMATCH.");
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  P/Invoke bindings
    // ─────────────────────────────────────────────────────────────────────────

    public static class ProjectileLib
    {
#if UNITY_IOS && !UNITY_EDITOR
        private const string DLL = "__Internal";
#else
        private const string DLL = "projectile_core";
#endif

        // ── Cached movement type constants ────────────────────────────────────

        /// <summary>
        /// Movement type byte constants fetched from Rust at startup.
        /// Populated by FetchMovementTypeConstants() which is called by ValidateStructSizes().
        /// </summary>
        public static readonly MovementTypeConstants MovementTypes = new MovementTypeConstants();

        // ─────────────────────────────────────────────────────────────────────
        //  Layout validation — private Rust size reporters
        // ─────────────────────────────────────────────────────────────────────

        [DllImport(DLL)] private static extern int projectile_struct_size();
        [DllImport(DLL)] private static extern int hit_result_struct_size();
        [DllImport(DLL)] private static extern int collision_target_struct_size();
        [DllImport(DLL)] private static extern int spawn_request_struct_size();
        [DllImport(DLL)] private static extern int projectile3d_struct_size();
        [DllImport(DLL)] private static extern int hit_result3d_struct_size();
        [DllImport(DLL)] private static extern int collision_target3d_struct_size();

        // ─────────────────────────────────────────────────────────────────────
        //  Movement type constant fetchers
        //  These call into Rust to get the byte value of each movement constant.
        //  Rust exports: movement_type_straight() → u8, etc.
        // ─────────────────────────────────────────────────────────────────────

        [DllImport(DLL)] private static extern byte movement_type_straight();
        [DllImport(DLL)] private static extern byte movement_type_arching();
        [DllImport(DLL)] private static extern byte movement_type_guided();
        [DllImport(DLL)] private static extern byte movement_type_teleport();
        [DllImport(DLL)] private static extern byte movement_type_wave();
        [DllImport(DLL)] private static extern byte movement_type_circular();

        // ─────────────────────────────────────────────────────────────────────
        //  Tick — 2D
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Advance all 2D projectiles by dt seconds.
        /// Returns the number that died this tick (lifetime expired).
        /// Called every FixedUpdate by ServerProjectileAuthority / LocalProjectileManager.
        /// </summary>
        [DllImport(DLL)]
        public static extern int tick_projectiles(IntPtr projs, int count, float dt);

        // ─────────────────────────────────────────────────────────────────────
        //  Tick — 3D
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Advance all 3D projectiles by dt seconds.
        /// Returns the number that died this tick.
        /// </summary>
        [DllImport(DLL)]
        public static extern int tick_projectiles_3d(IntPtr projs, int count, float dt);

        // ─────────────────────────────────────────────────────────────────────
        //  Collision — 2D
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Spatial-grid 2D collision check. Uses default cell_size (4.0 world units).
        /// Prefer check_hits_grid_ex when you need cell_size control.
        /// </summary>
        [DllImport(DLL)]
        public static extern void check_hits_grid(
            IntPtr projs,      int projCount,
            IntPtr targets,    int targetCount,
            IntPtr outHits,    int maxHits,
            out int outHitCount);

        /// <summary>
        /// Spatial-grid 2D collision check with explicit cell_size.
        /// Pass 0.0f to use the Rust default (4.0 world units).
        /// Tune to approximately 2× the largest target radius.
        /// </summary>
        [DllImport(DLL)]
        public static extern void check_hits_grid_ex(
            IntPtr projs,      int   projCount,
            IntPtr targets,    int   targetCount,
            IntPtr outHits,    int   maxHits,
            float  cellSize,
            out int outHitCount);

        // ─────────────────────────────────────────────────────────────────────
        //  Collision — 3D
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Spatial-grid 3D collision check.
        /// Pass 0.0f for cellSize to use the Rust default (4.0 world units).
        /// Uses a 512-bucket grid with u64 keys (3D cell coordinates packed).
        /// </summary>
        [DllImport(DLL)]
        public static extern void check_hits_grid_3d(
            IntPtr projs,      int   projCount,
            IntPtr targets,    int   targetCount,
            IntPtr outHits,    int   maxHits,
            float  cellSize,
            out int outHitCount);

        // ─────────────────────────────────────────────────────────────────────
        //  Spawn — legacy pattern path (2D only, kept for backward compat)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Write up to maxOut NativeProjectiles using hardcoded Rust pattern math.
        /// Has 928µs per-call FFI overhead regardless of projectile count.
        /// Use spawn_batch for all new code — it eliminates this overhead.
        /// C# writes Lifetime, MovementType, Scale etc. AFTER this returns.
        /// </summary>
        [DllImport(DLL)]
        public static extern void spawn_pattern(
            IntPtr req,     IntPtr outProjs,
            int    maxOut,  out int outCount);

        // ─────────────────────────────────────────────────────────────────────
        //  Spawn — batch path (single FFI call for any count)
        //
        //  These eliminate the 928µs per-call overhead by copying a
        //  pre-filled struct array into the sim buffer in one crossing.
        //  BatchSpawnHelper fills the array (Burst for 8+, C# loop for <8)
        //  then calls these once.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Copy a pre-filled 2D projectile array into the sim buffer in one FFI call.
        ///
        /// projsIn   — pointer to temp array filled by BatchSpawnHelper
        /// projsOut  — pointer to current write head of the 2D sim buffer
        ///             (= bufferBase + activeCount * 72)
        /// maxOut    — remaining sim buffer capacity (maxProjectiles - activeCount)
        /// outCount  — number actually written; caller adds this to activeCount
        /// </summary>
        [DllImport(DLL)]
        public static extern void spawn_batch(
            IntPtr projsIn,  int    count,
            IntPtr projsOut, int    maxOut,
            out int outCount);

        /// <summary>
        /// Copy a pre-filled 3D projectile array into the 3D sim buffer in one FFI call.
        /// Same semantics as spawn_batch but for NativeProjectile3D (84 bytes each).
        /// </summary>
        [DllImport(DLL)]
        public static extern void spawn_batch_3d(
            IntPtr projsIn,  int    count,
            IntPtr projsOut, int    maxOut,
            out int outCount);

        // ─────────────────────────────────────────────────────────────────────
        //  State save / restore (client reconciliation / rollback)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Snapshot the entire 2D sim buffer into buf as raw bytes.
        /// Required buf capacity = activeCount * 72 bytes.
        /// Returns bytes written, or 0 if buf is too small.
        /// Used by ClientPredictionManager for rollback reconciliation.
        /// </summary>
        [DllImport(DLL)]
        public static extern int save_state(
            IntPtr projs,  int count,
            IntPtr buf,    int bufLen);

        /// <summary>
        /// Restore 2D sim state from a previously saved snapshot.
        /// outCount = number of projectiles restored.
        /// </summary>
        [DllImport(DLL)]
        public static extern void restore_state(
            IntPtr outProjs,  int    maxCount,
            IntPtr buf,       int    bufLen,
            out int outCount);

        // ─────────────────────────────────────────────────────────────────────
        //  Movement parameter registration
        //
        //  Wave and Circular movement types store per-config constants
        //  (amplitude, frequency, radius, angular_speed) in a Rust-side
        //  HashMap<u16, WaveParams / CircularParams> indexed by config_id.
        //
        //  Why Rust-side?
        //    These constants are read every tick inside the hot loop.
        //    Storing them per-projectile adds 8-16 bytes to ALL projectiles,
        //    bloating cache lines for 95% of projectiles that never wave/circle.
        //    One HashMap lookup per tick for affected projectiles only
        //    is far cheaper than inflating the struct for all projectiles.
        //
        //  Who calls these?
        //    ProjectileConfigSO.RegisterMovementParams() — called automatically
        //    by ProjectileRegistry.Register(). Game code never calls these directly.
        //
        //  Thread safety:
        //    Registration must happen on the main thread before any simulation runs.
        //    Rust uses RwLock internally — reads are lock-free after registration.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Register sine/cosine wave movement parameters for a config ID.
        /// Called automatically by ProjectileConfigSO.RegisterMovementParams()
        /// when the config's MovementType is Wave.
        ///
        /// amplitude    — lateral displacement in world units (0.5 = gentle, 2.0 = strong)
        /// frequency    — oscillations per second (1 = slow, 5 = fast)
        /// phaseOffset  — starting phase in radians. Vary per pellet via ProjectilePatternSO
        ///                for multi-pellet spread variety (e.g. 0, π/4, π/2 for a helix spread).
        /// vertical     — 1 = oscillate vertically (Y axis in 2D, Y in 3D world-up)
        ///                0 = oscillate horizontally (perpendicular to travel)
        ///
        /// The perpendicular world axis is set in ax/ay at spawn by BatchSpawnHelper.
        /// Rust reads ax/ay as the oscillation direction — it does not compute it.
        /// </summary>
        [DllImport(DLL)]
        public static extern void register_wave_params(
            ushort configId,
            float  amplitude,
            float  frequency,
            float  phaseOffset,
            byte   vertical);

        /// <summary>
        /// Register circular/helical orbit parameters for a config ID.
        /// Called automatically by ProjectileConfigSO.RegisterMovementParams()
        /// when the config's MovementType is Circular.
        ///
        /// radius        — orbit radius in world units
        /// angularSpeed  — degrees per second (positive = CCW, negative = CW)
        /// startAngle    — starting orbit angle in degrees.
        ///                 For multi-pellet helical patterns: vary this per pellet
        ///                 (e.g. 0°, 90°, 180°, 270° for 4-pellet helix).
        ///                 Use ProjectilePatternSO with circular arrangement.
        ///
        /// The first perpendicular axis is set in ax/ay(/az for 3D) at spawn.
        /// Rust computes the second perpendicular as forward × first_perp internally.
        /// </summary>
        [DllImport(DLL)]
        public static extern void register_circular_params(
            ushort configId,
            float  radius,
            float  angularSpeed,
            float  startAngle);

        /// <summary>
        /// Unregister wave params for a config ID.
        /// Call when a config is unloaded or hot-reloaded.
        /// ProjectileRegistry.OnDestroy() handles this automatically.
        /// </summary>
        [DllImport(DLL)]
        public static extern void unregister_wave_params(ushort configId);

        /// <summary>
        /// Unregister circular params for a config ID.
        /// </summary>
        [DllImport(DLL)]
        public static extern void unregister_circular_params(ushort configId);

        /// <summary>
        /// Clear ALL registered movement params from Rust memory.
        /// Called by MID_MasterProjectileSystem.OnDestroy() on full system shutdown.
        /// Also call between scene loads if projectile configs change.
        /// </summary>
        [DllImport(DLL)]
        public static extern void clear_movement_params();

        // ─────────────────────────────────────────────────────────────────────
        //  Public validation + initialisation
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetch movement type byte constants from Rust and validate against C# enum.
        /// Called by ValidateStructSizes(). Safe to call separately if needed.
        /// </summary>
        public static void FetchMovementTypeConstants()
        {
            MovementTypes.Straight = movement_type_straight();
            MovementTypes.Arching  = movement_type_arching();
            MovementTypes.Guided   = movement_type_guided();
            MovementTypes.Teleport = movement_type_teleport();
            MovementTypes.Wave     = movement_type_wave();
            MovementTypes.Circular = movement_type_circular();

            MovementTypes.Validate();
        }

        /// <summary>
        /// Verify that all C# struct sizes match the compiled Rust library,
        /// and fetch + validate movement type constants.
        ///
        /// Call ONCE on startup before any FFI call.
        /// A struct size mismatch causes silent memory corruption.
        /// Throws InvalidOperationException if any check fails — crash loudly,
        /// do not silently continue with a broken FFI layer.
        ///
        /// Called automatically by MID_MasterProjectileSystem.Awake() and
        /// ProjectileRegistry.Awake(). Do not call from both — idempotent but wasteful.
        /// </summary>
        public static void ValidateStructSizes()
        {
            bool ok = true;

            // 2D structs
            ok &= Check("NativeProjectile (2D)",
                Marshal.SizeOf<NativeProjectile>(),
                projectile_struct_size(), 72);

            ok &= Check("HitResult (2D)",
                Marshal.SizeOf<HitResult>(),
                hit_result_struct_size(), 24);

            ok &= Check("CollisionTarget (2D)",
                Marshal.SizeOf<CollisionTarget>(),
                collision_target_struct_size(), 20);

            ok &= Check("SpawnRequest",
                Marshal.SizeOf<SpawnRequest>(),
                spawn_request_struct_size(), 32);

            // 3D structs
            ok &= Check("NativeProjectile3D",
                Marshal.SizeOf<NativeProjectile3D>(),
                projectile3d_struct_size(), 84);

            ok &= Check("HitResult3D",
                Marshal.SizeOf<HitResult3D>(),
                hit_result3d_struct_size(), 28);

            ok &= Check("CollisionTarget3D",
                Marshal.SizeOf<CollisionTarget3D>(),
                collision_target3d_struct_size(), 24);

            // Fetch and validate movement type constants
            FetchMovementTypeConstants();

            if (!ok)
                throw new InvalidOperationException(
                    "[ProjectileLib] One or more struct size mismatches detected. " +
                    "Check the Unity console for details. " +
                    "All P/Invoke calls are unsafe until the layout is corrected. " +
                    "Do NOT proceed — the mismatched struct will corrupt sim buffer memory.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Internal helper
        // ─────────────────────────────────────────────────────────────────────

        private static bool Check(
            string name, int csharpSize, int rustSize, int expected)
        {
            bool ok = (csharpSize == rustSize) && (csharpSize == expected);
            if (!ok)
            {
                Debug.LogError(
                    $"[ProjectileLib] STRUCT SIZE MISMATCH — {name}\n" +
                    $"  C# Marshal.SizeOf  = {csharpSize} bytes\n" +
                    $"  Rust sizeof        = {rustSize} bytes\n" +
                    $"  Expected           = {expected} bytes\n" +
                    $"  Every P/Invoke call involving this struct is UNDEFINED BEHAVIOUR until fixed.\n" +
                    $"  Check FieldOffset attributes vs Rust repr(C) field order.");
            }
            return ok;
        }
    }
}
