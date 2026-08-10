// Complete FFI layer for projectile_core Rust native library.
// ALL P/Invoke bindings live here. Nothing else uses DllImport.
//
// Platform DLL resolution:
//   iOS / WebGL : "__Internal" — resolved at link time by Xcode / Emscripten.
//   All others  : "projectile_core" — loaded at runtime from Plugins/Native/.
//
// Struct size reference (must match Rust repr(C) exactly):
//   NativeProjectile    = 72 bytes   (2D)
//   HitResult           = 24 bytes   (2D)
//   CollisionTarget     = 20 bytes   (2D)
//   ShapeCollider2D     = 76 bytes   (2D — Box/Capsule/Edge/Polygon, additive)
//   SpawnRequest        = 32 bytes
//   NativeProjectile3D  = 84 bytes   (3D)
//   HitResult3D         = 28 bytes   (3D)
//   CollisionTarget3D   = 24 bytes   (3D)
//   ShapeCollider3D     = 108 bytes  (3D — Box/Capsule/Edge/Polygon, additive)

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

    /// <summary>
    /// Plain (x, y) pair, matches Rust shapes.rs's Vec2Raw exactly — 8 bytes,
    /// sequential layout needs no explicit offsets of its own since it's only
    /// ever embedded (via FieldOffset on the parent) inside ShapeCollider2D.
    /// </summary>
    public struct Vec2Raw
    {
        public float X;
        public float Y;
        public Vec2Raw(float x, float y) { X = x; Y = y; }
        public Vec2Raw(Vector2 v) { X = v.x; Y = v.y; }
        public Vector2 ToVector2() => new Vector2(X, Y);
    }

    /// <summary>
    /// Descriptive-only shape tag — see shapes.rs's module header. Rust never
    /// branches on this; every shape type runs the same segment-distance test.
    /// It exists purely so the C# side (auto-detection, editor UX, debugging)
    /// can tell what a given ShapeCollider2D/3D was authored as.
    /// </summary>
    public enum ShapeColliderType : byte
    {
        Box     = 0,
        Capsule = 1,
        Edge    = 2,
        Polygon = 3
    }

    /// <summary>
    /// 2D point-sequence shape collider (Box/Capsule/Edge/Polygon) — additive
    /// alongside CollisionTarget, never a replacement for it. See shapes.rs's
    /// module header for the full design rationale: every shape here is a
    /// sequence of up to MaxPoints points with a per-shape thickness, tested
    /// via closest-point-on-segment. 76 bytes — must match Rust ShapeCollider2D
    /// repr(C) exactly.
    ///
    /// Points are WORLD space — same convention CollisionTarget already uses
    /// for X/Y. A moving shape must re-set its points (via SetPoint) every
    /// tick it moves, exactly like RustSimTargetRegistrar already re-registers
    /// a moving CollisionTarget's X/Y.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 76)]
    public struct ShapeCollider2D
    {
        public const int MaxPoints = 8;

        [FieldOffset(0)]  public uint TargetId;
        [FieldOffset(4)]  public byte ShapeType;   // ShapeColliderType — descriptive only, see summary
        [FieldOffset(5)]  public byte PointCount;  // valid range 2..=MaxPoints
        [FieldOffset(6)]  public byte Closed;      // 1 = wrap last point back to first
        [FieldOffset(7)]  public byte Active;
        [FieldOffset(8)]  public float Thickness;  // capsule/edge radius; 0 for a bare box/polygon edge

        [FieldOffset(12)] public Vec2Raw Point0;
        [FieldOffset(20)] public Vec2Raw Point1;
        [FieldOffset(28)] public Vec2Raw Point2;
        [FieldOffset(36)] public Vec2Raw Point3;
        [FieldOffset(44)] public Vec2Raw Point4;
        [FieldOffset(52)] public Vec2Raw Point5;
        [FieldOffset(60)] public Vec2Raw Point6;
        [FieldOffset(68)] public Vec2Raw Point7;

        /// <summary>
        /// No unsafe code / pointer tricks — a plain switch keeps this FFI
        /// struct's layout honest and portable, matching how the rest of this
        /// file avoids unsafe blocks entirely in favor of GCHandle pinning.
        /// </summary>
        public void SetPoint(int index, Vector2 worldPos)
        {
            var p = new Vec2Raw(worldPos);
            switch (index)
            {
                case 0: Point0 = p; break; case 1: Point1 = p; break;
                case 2: Point2 = p; break; case 3: Point3 = p; break;
                case 4: Point4 = p; break; case 5: Point5 = p; break;
                case 6: Point6 = p; break; case 7: Point7 = p; break;
                default: throw new ArgumentOutOfRangeException(nameof(index), index, $"Must be 0..{MaxPoints - 1}.");
            }
        }

        public Vector2 GetPoint(int index) => index switch
        {
            0 => Point0.ToVector2(), 1 => Point1.ToVector2(),
            2 => Point2.ToVector2(), 3 => Point3.ToVector2(),
            4 => Point4.ToVector2(), 5 => Point5.ToVector2(),
            6 => Point6.ToVector2(), 7 => Point7.ToVector2(),
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, $"Must be 0..{MaxPoints - 1}.")
        };
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
        [FieldOffset(0)]  public float X;
        [FieldOffset(4)]  public float Y;
        [FieldOffset(8)]  public float Z;
        [FieldOffset(12)] public float Vx;
        [FieldOffset(16)] public float Vy;
        [FieldOffset(20)] public float Vz;
        [FieldOffset(24)] public float Ax;
        [FieldOffset(28)] public float Ay;
        [FieldOffset(32)] public float Az;
        [FieldOffset(36)] public float ScaleX;
        [FieldOffset(40)] public float ScaleY;
        [FieldOffset(44)] public float ScaleZ;
        [FieldOffset(48)] public float ScaleTarget;
        [FieldOffset(52)] public float ScaleSpeed;
        [FieldOffset(56)] public float Lifetime;
        [FieldOffset(60)] public float MaxLifetime;
        [FieldOffset(64)] public float TravelDist;
        [FieldOffset(68)] public float TimerT;
        [FieldOffset(72)] public ushort ConfigId;
        [FieldOffset(74)] public ushort OwnerId;
        [FieldOffset(76)] public uint   ProjId;
        [FieldOffset(80)] public byte CollisionCount;
        [FieldOffset(81)] public byte MovementType;
        [FieldOffset(82)] public byte PiercingType;
        [FieldOffset(83)] public byte Alive;

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

    /// <summary>
    /// Plain (x, y, z) triple, matches Rust shapes.rs's Vec3Raw exactly —
    /// 12 bytes. See Vec2Raw's doc comment; same rationale, 3D version.
    /// </summary>
    public struct Vec3Raw
    {
        public float X;
        public float Y;
        public float Z;
        public Vec3Raw(float x, float y, float z) { X = x; Y = y; Z = z; }
        public Vec3Raw(Vector3 v) { X = v.x; Y = v.y; Z = v.z; }
        public Vector3 ToVector3() => new Vector3(X, Y, Z);
    }

    /// <summary>
    /// 3D point-sequence shape collider. See ShapeCollider2D's doc comment —
    /// identical design, Vector3 points. 108 bytes — must match Rust
    /// ShapeCollider3D repr(C) exactly.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 108)]
    public struct ShapeCollider3D
    {
        public const int MaxPoints = 8;

        [FieldOffset(0)]  public uint TargetId;
        [FieldOffset(4)]  public byte ShapeType;
        [FieldOffset(5)]  public byte PointCount;
        [FieldOffset(6)]  public byte Closed;
        [FieldOffset(7)]  public byte Active;
        [FieldOffset(8)]  public float Thickness;

        [FieldOffset(12)]  public Vec3Raw Point0;
        [FieldOffset(24)]  public Vec3Raw Point1;
        [FieldOffset(36)]  public Vec3Raw Point2;
        [FieldOffset(48)]  public Vec3Raw Point3;
        [FieldOffset(60)]  public Vec3Raw Point4;
        [FieldOffset(72)]  public Vec3Raw Point5;
        [FieldOffset(84)]  public Vec3Raw Point6;
        [FieldOffset(96)]  public Vec3Raw Point7;

        public void SetPoint(int index, Vector3 worldPos)
        {
            var p = new Vec3Raw(worldPos);
            switch (index)
            {
                case 0: Point0 = p; break; case 1: Point1 = p; break;
                case 2: Point2 = p; break; case 3: Point3 = p; break;
                case 4: Point4 = p; break; case 5: Point5 = p; break;
                case 6: Point6 = p; break; case 7: Point7 = p; break;
                default: throw new ArgumentOutOfRangeException(nameof(index), index, $"Must be 0..{MaxPoints - 1}.");
            }
        }

        public Vector3 GetPoint(int index) => index switch
        {
            0 => Point0.ToVector3(), 1 => Point1.ToVector3(),
            2 => Point2.ToVector3(), 3 => Point3.ToVector3(),
            4 => Point4.ToVector3(), 5 => Point5.ToVector3(),
            6 => Point6.ToVector3(), 7 => Point7.ToVector3(),
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, $"Must be 0..{MaxPoints - 1}.")
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Shared enums
    // ─────────────────────────────────────────────────────────────────────────

    public enum PatternId : byte
    {
        Single  = 0,
        Spread3 = 1,
        Spread5 = 2,
        Spiral  = 3,
        Ring8   = 4
    }

    public enum ProjectileMovementType : byte
    {
        Straight  = 0,

        // Arching (was 1) REMOVED: tick_arching/tick_arching_3d in Rust's
        // simulation.rs were byte-for-byte identical to tick_straight/
        // tick_straight_3d — same vel += accel*dt; pos += vel*dt integration,
        // same accel gather (which already applies gravity via a nonzero Ay
        // set at spawn time, regardless of movement type). An "arc" was never
        // a second mode — it was Straight with gravity, wearing a different
        // name. Value 1 is intentionally left unassigned rather than reused,
        // so any already-serialized ProjectileConfigSO still carrying byte
        // value 1 falls through Rust's tick_scalar_one default arm to
        // tick_straight — exactly what it always computed anyway, not a
        // behavior change for old data.

        Guided    = 2,
        Teleport  = 3,
        Wave      = 4,
        Circular  = 5,

        /// <summary>
        /// PHYSICS-PROJECTILE-ONLY. Drives PhysicsProjectileBase.ApplyCustomCurve()
        /// from user-authored AnimationCurves on ProjectileConfigSO (speed profile +
        /// perpendicular offset over the shot's lifetime) — for movement shapes Wave/
        /// Circular's fixed sine math can't express (ease in/out, pulses, zigzags,
        /// boomerangs, etc). Deliberately has no Rust-side tick: unlike Wave/Circular,
        /// RustSim projectiles never use this value. If one somehow did (it shouldn't —
        /// no RustSim spawn path sets it), simulation.rs's tick_scalar_one match falls
        /// through its default arm to tick_straight, same as any other unrecognized
        /// movement_type — a safe no-op, not a crash, but still: don't spawn RustSim/
        /// native projectiles with this. Physics projectiles only.
        /// </summary>
        CustomCurve = 6
    }

    public enum ProjectilePiercingType : byte
    {
        None   = 0,
        Piecer = 1,
        Random = 2
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Movement type constant cache
    // ─────────────────────────────────────────────────────────────────────────

    public class MovementTypeConstants
    {
        public byte Straight  { get; internal set; }
        // Arching removed — see ProjectileMovementType.Straight's doc comment.
        public byte Guided    { get; internal set; }
        public byte Teleport  { get; internal set; }
        public byte Wave      { get; internal set; }
        public byte Circular  { get; internal set; }

        internal void Validate()
        {
            bool ok = true;
            ok &= CheckConst("Straight",  Straight,  (byte)ProjectileMovementType.Straight);
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
        // ── DLL name resolution ───────────────────────────────────────────────
        //
        // iOS  : static lib linked by Xcode → __Internal
        // WebGL: static lib linked by Emscripten → __Internal
        //        (Unity WebGL P/Invoke resolves via the same __Internal mechanism)
        // All others: runtime loaded from Plugins/Native/<platform>/
        //
#if (UNITY_IOS || UNITY_WEBGL) && !UNITY_EDITOR
        private const string DLL = "__Internal";
#else
        private const string DLL = "projectile_core";
#endif

        public static readonly MovementTypeConstants MovementTypes = new MovementTypeConstants();

        // ── Native availability probe ─────────────────────────────────────────
        //
        // FIX (device-specific DllNotFoundException hardening):
        // Every P/Invoke call site below assumed the native library would either
        // load correctly or the caller would already be inside ValidateStructSizes()'s
        // try/catch. In practice, a missing/misconfigured per-architecture .so
        // (e.g. an Android Plugin Importer entry missing its CPU tag) throws
        // DllNotFoundException the instant ANY of these extern methods is called —
        // including from call sites like ProjectileConfigSO.RegisterMovementParams()
        // that never went through ValidateStructSizes() at all. Since that
        // exception type was never being caught anywhere, it could escape
        // uncaught out of a MonoBehaviour.Awake() or, worse, out of a running
        // Coroutine — silently killing everything queued after it in that
        // coroutine (see TestSceneBootstrapper.Start()).
        //
        // IsAvailable performs ONE cheap probe call the first time it's touched
        // and caches the result for the rest of the session. Callers that can't
        // function without the native lib (RegisterMovementParams, etc.) should
        // check this BEFORE calling any other extern method, and degrade
        // gracefully (log + return) instead of letting the exception propagate.
        private static bool? _isAvailable;

        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable.HasValue) return _isAvailable.Value;

                try
                {
                    _ = projectile_struct_size();
                    _isAvailable = true;
                }
                catch (Exception ex) when (
                    ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    Debug.LogError(
                        $"[ProjectileLib] Native library '{DLL}' failed to load on this platform/" +
                        $"architecture: {ex.GetType().Name} — {ex.Message}. The projectile system " +
                        "will be disabled on this device. Check the Plugin Importer CPU setting for " +
                        "Plugins/Native/Android/<abi>/libprojectile_core.so (per-ABI meta files need an " +
                        "explicit Android platform entry with the matching CPU tag, or Unity silently " +
                        "drops the .so from that ABI's build).");
                    _isAvailable = false;
                }

                return _isAvailable.Value;
            }
        }

        // ── Layout validation ─────────────────────────────────────────────────

        [DllImport(DLL)] private static extern int projectile_struct_size();
        [DllImport(DLL)] private static extern int hit_result_struct_size();
        [DllImport(DLL)] private static extern int collision_target_struct_size();
        [DllImport(DLL)] private static extern int spawn_request_struct_size();
        [DllImport(DLL)] private static extern int projectile3d_struct_size();
        [DllImport(DLL)] private static extern int hit_result3d_struct_size();
        [DllImport(DLL)] private static extern int collision_target3d_struct_size();
        [DllImport(DLL)] private static extern int shape_collider_2d_struct_size();
        [DllImport(DLL)] private static extern int shape_collider_3d_struct_size();
        [DllImport(DLL)] public  static extern int shape_collider_max_points();

        // ── Movement type constant fetchers ───────────────────────────────────

        [DllImport(DLL)] private static extern byte movement_type_straight();
        // movement_type_arching() removed — see ProjectileMovementType.Straight's doc comment.
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

        // ── Collision — Shape colliders (Box/Capsule/Edge/Polygon, additive) ───
        //
        // Call right after check_hits_grid[_3d] above, passing the SAME outHits
        // buffer and hitOffset = whatever hit count that call just produced.
        // Returns the new grand-total hit count for that buffer — assign it
        // straight back, no addition needed. See shapes.rs's module header.

        [DllImport(DLL)]
        public static extern int check_hits_shapes_2d(
            IntPtr projs,   int projCount,
            IntPtr shapes,  int shapeCount,
            IntPtr outHits, int maxHits,
            int    hitOffset);

        [DllImport(DLL)]
        public static extern int check_hits_shapes_3d(
            IntPtr projs,   int projCount,
            IntPtr shapes,  int shapeCount,
            IntPtr outHits, int maxHits,
            int    hitOffset);

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
            MovementTypes.Guided   = movement_type_guided();
            MovementTypes.Teleport = movement_type_teleport();
            MovementTypes.Wave     = movement_type_wave();
            MovementTypes.Circular = movement_type_circular();
            MovementTypes.Validate();
        }

        /// <summary>
        /// Verify all C# struct sizes match the compiled Rust library.
        /// Throws InvalidOperationException on mismatch, AND now also throws
        /// InvalidOperationException (instead of letting DllNotFoundException
        /// escape uncaught) if the native library isn't available on this
        /// platform/architecture at all. This keeps every existing call site
        /// that already does `catch (InvalidOperationException)` working
        /// correctly for the "lib missing" case, with no changes needed there.
        /// Call ONCE on startup before any FFI call.
        /// </summary>
        public static void ValidateStructSizes()
        {
            if (!IsAvailable)
                throw new InvalidOperationException(
                    $"[ProjectileLib] Native library '{DLL}' is not available on this platform/" +
                    "architecture — see the earlier error log for the underlying exception. " +
                    "All P/Invoke calls are unsafe until this is fixed.");

            bool ok = true;

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

            ok &= Check("NativeProjectile3D",
                Marshal.SizeOf<NativeProjectile3D>(),
                projectile3d_struct_size(), 84);

            ok &= Check("HitResult3D",
                Marshal.SizeOf<HitResult3D>(),
                hit_result3d_struct_size(), 28);

            ok &= Check("CollisionTarget3D",
                Marshal.SizeOf<CollisionTarget3D>(),
                collision_target3d_struct_size(), 24);

            ok &= Check("ShapeCollider2D",
                Marshal.SizeOf<ShapeCollider2D>(),
                shape_collider_2d_struct_size(), 76);

            ok &= Check("ShapeCollider3D",
                Marshal.SizeOf<ShapeCollider3D>(),
                shape_collider_3d_struct_size(), 108);

            int rustMaxPoints = shape_collider_max_points();
            if (rustMaxPoints != ShapeCollider2D.MaxPoints)
            {
                ok = false;
                Debug.LogError(
                    $"[ProjectileLib] ShapeCollider MaxPoints mismatch — " +
                    $"C#={ShapeCollider2D.MaxPoints}, Rust={rustMaxPoints}.");
            }

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
