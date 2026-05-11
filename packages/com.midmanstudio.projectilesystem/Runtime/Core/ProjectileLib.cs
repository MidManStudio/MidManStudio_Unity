// ProjectileLib.cs
// Complete FFI layer for projectile_core Rust native library.
// ALL P/Invoke bindings live here. Nothing else uses DllImport.
//
// iOS: IL2CPP links the static lib as __Internal.
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

namespace MidManStudio.Projectiles.Core
{
    // ─────────────────────────────────────────────────────────────────────────
    //  2D FFI structs
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawn request passed to spawn_pattern (legacy) or used as a template.
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
        [FieldOffset(20)] public byte   PatternId;
        // 3 bytes padding at 21-23
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
        [FieldOffset(4)]  public uint  ProjIndex;
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
        [FieldOffset(16)] public byte  Active;
        // 3 bytes padding at 17-19
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  3D FFI structs
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Core 3D projectile state. 84 bytes.
    /// Must match Rust NativeProjectile3D repr(C) exactly.
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

        // ── Scale ─────────────────────────────────────────────────────────────
        [FieldOffset(36)] public float ScaleX;
        [FieldOffset(40)] public float ScaleY;
        [FieldOffset(44)] public float ScaleZ;
        [FieldOffset(48)] public float ScaleTarget;
        [FieldOffset(52)] public float ScaleSpeed;

        // ── Lifetime & travel ─────────────────────────────────────────────────
        [FieldOffset(56)] public float Lifetime;
        [FieldOffset(60)] public float MaxLifetime;
        [FieldOffset(64)] public float TravelDist;
        [FieldOffset(68)] public float TimerT;

        // ── Identity ──────────────────────────────────────────────────────────
        [FieldOffset(72)] public ushort ConfigId;
        [FieldOffset(74)] public ushort OwnerId;
        [FieldOffset(76)] public uint   ProjId;

        // ── Flags ──────────────────────────────────────────────────────────────
        [FieldOffset(80)] public byte CollisionCount;
        [FieldOffset(81)] public byte MovementType;
        [FieldOffset(82)] public byte PiercingType;
        [FieldOffset(83)] public byte Alive;

        // ── Convenience helpers ───────────────────────────────────────────────

        public bool    IsAlive         => Alive != 0;
        public float   CollisionRadius => ScaleX * 0.5f;
        public Vector3 Position        => new Vector3(X, Y, Z);

        public UnityEngine.Quaternion VisualRotation()
        {
            var v = new Vector3(Vx, Vy, Vz);
            return v.sqrMagnitude < 0.0001f
                ? UnityEngine.Quaternion.identity
                : UnityEngine.Quaternion.LookRotation(v.normalized, Vector3.up);
        }

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
        // 3 bytes padding at 21-23
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Shared enums
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Spawn pattern for the legacy spawn_pattern entry point.</summary>
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
    /// Use ProjectileLib.MovementTypes (fetched from Rust at startup) to validate.
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

    /// <summary>Piercing capability. Must match Rust PiercingType constants.</summary>
    public enum ProjectilePiercingType : byte
    {
        None   = 0,
        Piecer = 1,
        Random = 2
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Movement type constant cache — NOT a static class (CS0708 fix)
    //  Fetched from Rust at startup so C# and Rust always agree.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Movement type byte constants fetched from Rust at startup.
    /// Access via ProjectileLib.MovementTypes after calling
    /// ProjectileLib.FetchMovementTypeConstants().
    /// </summary>
    public class MovementTypeConstants
    {
        public byte Straight  { get; internal set; }
        public byte Arching   { get; internal set; }
        public byte Guided    { get; internal set; }
        public byte Teleport  { get; internal set; }
        public byte Wave      { get; internal set; }
        public byte Circular  { get; internal set; }

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
                Debug.LogError(
                    "[ProjectileLib] Movement type constant mismatch between C# enum and Rust. " +
                    "Update ProjectileMovementType enum to match simulation.rs constants.");
        }

        private static bool CheckConst(string name, byte rust, byte csharp)
        {
            if (rust == csharp) return true;
            Debug.LogError(
                $"[ProjectileLib] MovementType.{name}: Rust={rust}, C#={csharp}. MISMATCH.");
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

        /// <summary>Movement type byte constants fetched from Rust at startup.</summary>
        public static readonly MovementTypeConstants MovementTypes = new MovementTypeConstants();

        // ── Layout validation ─────────────────────────────────────────────────

        [DllImport(DLL)] private static extern int projectile_struct_size();
        [DllImport(DLL)] private static extern int hit_result_struct_size();
        [DllImport(DLL)] private static extern int collision_target_struct_size();
        [DllImport(DLL)] private static extern int spawn_request_struct_size();
        [DllImport(DLL)] private static extern int projectile3d_struct_size();
        [DllImport(DLL)] private static extern int hit_result3d_struct_size();
        [DllImport(DLL)] private static extern int collision_target3d_struct_size();

        // ── Movement type constant fetchers ───────────────────────────────────

        [DllImport(DLL)] private static extern byte movement_type_straight();
        [DllImport(DLL)] private static extern byte movement_type_arching();
        [DllImport(DLL)] private static extern byte movement_type_guided();
        [DllImport(DLL)] private static extern byte movement_type_teleport();
        [DllImport(DLL)] private static extern byte movement_type_wave();
        [DllImport(DLL)] private static extern byte movement_type_circular();

        // ── Tick — 2D ─────────────────────────────────────────────────────────

        [DllImport(DLL)]
        public static extern int tick_projectiles(IntPtr projs, int count, float dt);

        // ── Tick — 3D ─────────────────────────────────────────────────────────

        [DllImport(DLL)]
        public static extern int tick_projectiles_3d(IntPtr projs, int count, float dt);

        // ── Collision — 2D ────────────────────────────────────────────────────

        [DllImport(DLL)]
        public static extern void check_hits_grid(
            IntPtr projs,    int projCount,
            IntPtr targets,  int targetCount,
            IntPtr outHits,  int maxHits,
            out int outHitCount);

        [DllImport(DLL)]
        public static extern void check_hits_grid_ex(
            IntPtr projs,    int   projCount,
            IntPtr targets,  int   targetCount,
            IntPtr outHits,  int   maxHits,
            float  cellSize,
            out int outHitCount);

        // ── Collision — 3D ────────────────────────────────────────────────────

        [DllImport(DLL)]
        public static extern void check_hits_grid_3d(
            IntPtr projs,    int   projCount,
            IntPtr targets,  int   targetCount,
            IntPtr outHits,  int   maxHits,
            float  cellSize,
            out int outHitCount);

        // ── Spawn — legacy pattern path ───────────────────────────────────────

        [DllImport(DLL)]
        public static extern void spawn_pattern(
            IntPtr req,    IntPtr outProjs,
            int    maxOut, out int outCount);

        // ── Spawn — batch path ────────────────────────────────────────────────

        [DllImport(DLL)]
        public static extern void spawn_batch(
            IntPtr projsIn,  int    count,
            IntPtr projsOut, int    maxOut,
            out int outCount);

        [DllImport(DLL)]
        public static extern void spawn_batch_3d(
            IntPtr projsIn,  int    count,
            IntPtr projsOut, int    maxOut,
            out int outCount);

        // ── State save / restore ──────────────────────────────────────────────

        [DllImport(DLL)]
        public static extern int save_state(
            IntPtr projs, int count,
            IntPtr buf,   int bufLen);

        [DllImport(DLL)]
        public static extern void restore_state(
            IntPtr outProjs, int    maxCount,
            IntPtr buf,      int    bufLen,
            out int outCount);

        // ── Movement parameter registration ───────────────────────────────────

        [DllImport(DLL)]
        public static extern void register_wave_params(
            ushort configId,
            float  amplitude,
            float  frequency,
            float  phaseOffset,
            byte   vertical);

        [DllImport(DLL)]
        public static extern void register_circular_params(
            ushort configId,
            float  radius,
            float  angularSpeed,
            float  startAngle);

        [DllImport(DLL)]
        public static extern void unregister_wave_params(ushort configId);

        [DllImport(DLL)]
        public static extern void unregister_circular_params(ushort configId);

        [DllImport(DLL)]
        public static extern void clear_movement_params();

        // ── Public validation + initialisation ───────────────────────────────

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
        /// Verify all C# struct sizes match the compiled Rust library.
        /// Throws InvalidOperationException on mismatch.
        /// Call ONCE on startup before any FFI call.
        /// </summary>
        public static void ValidateStructSizes()
        {
            bool ok = true;

            ok &= Check("NativeProjectile (2D)",
                System.Runtime.InteropServices.Marshal.SizeOf<NativeProjectile>(),
                projectile_struct_size(), 72);

            ok &= Check("HitResult (2D)",
                System.Runtime.InteropServices.Marshal.SizeOf<HitResult>(),
                hit_result_struct_size(), 24);

            ok &= Check("CollisionTarget (2D)",
                System.Runtime.InteropServices.Marshal.SizeOf<CollisionTarget>(),
                collision_target_struct_size(), 20);

            ok &= Check("SpawnRequest",
                System.Runtime.InteropServices.Marshal.SizeOf<SpawnRequest>(),
                spawn_request_struct_size(), 32);

            ok &= Check("NativeProjectile3D",
                System.Runtime.InteropServices.Marshal.SizeOf<NativeProjectile3D>(),
                projectile3d_struct_size(), 84);

            ok &= Check("HitResult3D",
                System.Runtime.InteropServices.Marshal.SizeOf<HitResult3D>(),
                hit_result3d_struct_size(), 28);

            ok &= Check("CollisionTarget3D",
                System.Runtime.InteropServices.Marshal.SizeOf<CollisionTarget3D>(),
                collision_target3d_struct_size(), 24);

            FetchMovementTypeConstants();

            if (!ok)
                throw new InvalidOperationException(
                    "[ProjectileLib] One or more struct size mismatches detected. " +
                    "Check the Unity console for details. " +
                    "All P/Invoke calls are unsafe until the layout is corrected.");
        }

        private static bool Check(string name, int csharpSize, int rustSize, int expected)
        {
            bool ok = (csharpSize == rustSize) && (csharpSize == expected);
            if (!ok)
                Debug.LogError(
                    $"[ProjectileLib] STRUCT SIZE MISMATCH — {name}\n" +
                    $"  C# Marshal.SizeOf  = {csharpSize} bytes\n" +
                    $"  Rust sizeof        = {rustSize} bytes\n" +
                    $"  Expected           = {expected} bytes");
            return ok;
        }
    }
}
