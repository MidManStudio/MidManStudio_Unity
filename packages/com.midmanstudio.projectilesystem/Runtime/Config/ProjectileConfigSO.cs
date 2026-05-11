// ProjectileConfigSO.cs — CORE PACKAGE VERSION
//
// Lean, game-agnostic projectile configuration base class.
// Contains ONLY what the simulation system, renderer, trail system,
// and network layer need.
//
// EXTENSION PATTERN:
//   Do not modify this file for game-specific fields.
//   Create a derived class in your game assembly:
//
//   [CreateAssetMenu(...)]
//   public class MyGameProjectileConfig : ProjectileConfigSO
//   {
//       [Header("Game-Specific")]
//       public ProjectileType projectileType;
//       public ProjectileClass projectileClass;
//
//       public override bool RequiresPhysicsObject()
//           => projectileType == ProjectileType.Rocket
//           || projectileType == ProjectileType.FireBall;
//
//       public override bool IsRaycastEligible()
//           => base.IsRaycastEligible() && projectileClass != ProjectileClass.Exploder;
//   }
//
// ROUTING HOOKS:
//   Override RequiresPhysicsObject() and IsRaycastEligible() to inject
//   game-specific routing logic into ProjectileTypeRouter without modifying
//   the package.

using UnityEngine;
using System;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Core;
using SimulationMode = MidManStudio.Projectiles.Core.SimulationMode;
namespace MidManStudio.Projectiles.Config
{
    [CreateAssetMenu(
        fileName = "ProjectileConfig",
        menuName  = "MidMan/Projectile System/Projectile Config",
        order     = 10)]
    public class ProjectileConfigSO : ScriptableObject
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Identity
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Assigned by ProjectileRegistry at registration. Do not set manually.</summary>
        [HideInInspector] public ushort ConfigId;

        // ─────────────────────────────────────────────────────────────────────
        //  Simulation routing
        // ─────────────────────────────────────────────────────────────────────

        [Header("Simulation")]

        [Tooltip("Is this a 3D projectile?\n" +
                 "FALSE: 2D Rust sim (NativeProjectile, tick_projectiles).\n" +
                 "TRUE:  3D Rust sim (NativeProjectile3D, tick_projectiles_3d).")]
        [SerializeField] private bool _is3D = false;
        public bool Is3D => _is3D;

        [Tooltip("Override the automatic SimulationMode decision for this config.\n\n" +
                 "Leave as RustSim2D to let ProjectileTypeRouter decide automatically.\n" +
                 "Override to force a specific mode regardless of weapon context.")]
        [SerializeField] private SimulationMode _preferredSimMode = SimulationMode.RustSim2D;
        public SimulationMode PreferredSimMode => _preferredSimMode;

        /// <summary>True when PreferredSimMode has been explicitly overridden from the default.</summary>
        public bool HasSimModeOverride => _preferredSimMode != SimulationMode.RustSim2D;

        // ─────────────────────────────────────────────────────────────────────
        //  Routing hooks — override in derived class for game-specific logic
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Return true if this projectile type requires Unity physics (Rigidbody2D/3D)
        /// and therefore cannot be simulated in the Rust tick buffer.
        ///
        /// Base implementation always returns false (all projectiles are sim-compatible
        /// by default). Override in your game-specific derived class to return true for
        /// rockets, fireballs, bouncy rounds, sticky rounds, etc.
        ///
        /// Example override:
        ///   public override bool RequiresPhysicsObject()
        ///       => projectileType == ProjectileType.Rocket
        ///       || extraPhysics == ExtraPhysicsType.Bouncy;
        /// </summary>
        public virtual bool RequiresPhysicsObject() => false;

        /// <summary>
        /// Return true if this projectile is safe for instant hitscan (Raycast mode).
        ///
        /// Base implementation: eligible when non-piercing AND 2D.
        /// Piercing requires per-tick collision counting; raycast has no ongoing state.
        /// 3D projectiles use the Rust 3D sim, not Physics2D.Raycast.
        ///
        /// Override in your derived class to add additional ineligibility conditions
        /// (e.g. exotic movement types that need ongoing position state):
        ///   public override bool IsRaycastEligible()
        ///       => base.IsRaycastEligible()
        ///       &amp;&amp; movementType == ProjectileMovementType.Straight;
        /// </summary>
        public virtual bool IsRaycastEligible()
        {
            if (_piercingType != ProjectilePiercingType.None) return false;
            if (_is3D)                                         return false;
            if (RequiresPhysicsObject())                       return false;
            return true;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Movement (Rust-facing)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Movement")]

        [SerializeField] private ProjectileMovementType _movementType = ProjectileMovementType.Straight;
        public ProjectileMovementType MovementType => _movementType;

        [Tooltip("Min speed (world units/sec). If Min == Max, range is skipped.")]
        [SerializeField] private float _minSpeed = 10f;
        [SerializeField] private float _maxSpeed = 10f;
        public float MinSpeed => _minSpeed;
        public float MaxSpeed => _maxSpeed;

        /// <summary>
        /// Resolve speed — skips Random.Range when min == max (deterministic).
        /// Call once at spawn time; store result in NativeProjectile.
        /// </summary>
        public float ResolveSpeed()
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            return _minSpeed == _maxSpeed
                ? _minSpeed
                : UnityEngine.Random.Range(_minSpeed, _maxSpeed);
        }

        [Tooltip("Maximum lifetime in seconds before the projectile is killed.")]
        [SerializeField] private float _lifetime = 3f;
        public float Lifetime => _lifetime;

        [Tooltip("Gravity / downward acceleration (maps to Rust NativeProjectile.Ay).\n" +
                 "0 = no gravity. Positive = falls down. Used by Arching movement.")]
        [SerializeField] private float _gravityScale = 0f;
        public float GravityScale => _gravityScale;

        [Tooltip("Maximum travel distance in world units. Also used to normalise\n" +
                 "distance for damage curve evaluation (0 = spawn, 1 = max range).")]
        [SerializeField] private float _maxRange = 50f;
        public float MaxRange => _maxRange;

        // ─────────────────────────────────────────────────────────────────────
        //  Piercing (Rust-facing)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Piercing")]

        [SerializeField] private ProjectilePiercingType _piercingType = ProjectilePiercingType.None;
        public ProjectilePiercingType PiercingType => _piercingType;

        [Tooltip("Maximum targets hit before dying. Only relevant when PiercingType != None.")]
        [SerializeField, Range(1, 16)] private byte _maxCollisions = 1;
        public byte MaxCollisions => _maxCollisions;

        // ─────────────────────────────────────────────────────────────────────
        //  Scale growth (Rust-facing, opt-in)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Scale Growth (Optional)")]

        [Tooltip("Enable scale growth. FALSE (default): ScaleSpeed=0, Rust skips tick_scale (zero cost).\n" +
                 "TRUE: grows from SpawnScale to FullSize at GrowthSpeed.")]
        [SerializeField] private bool _useScaleGrowth = false;
        public bool UseScaleGrowth => _useScaleGrowth;

        [SerializeField] private float _fullSizeX = 0.2f;
        [SerializeField] private float _fullSizeY = 0.2f;
        [SerializeField, Range(0.01f, 1f)] private float _spawnScaleFraction = 0.2f;
        [SerializeField, Range(1f, 30f)]   private float _growthSpeed = 8f;

        public float FullSizeX          => _fullSizeX;
        public float FullSizeY          => _fullSizeY;
        public float SpawnScaleFraction => _spawnScaleFraction;
        public float GrowthSpeed        => _growthSpeed;

        // ─────────────────────────────────────────────────────────────────────
        //  Damage profile (C#-only)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Damage Profile")]

        [Tooltip("Damage as a function of normalised travel distance (x=0 to 1).\n" +
                 "Flat curve = constant damage. Use Damage Profile Editor to visualise.")]
        [SerializeField] private AnimationCurve _damageCurve =
            AnimationCurve.Constant(0f, 1f, 25f);
        public AnimationCurve DamageCurve => _damageCurve;

        [Tooltip("Headshot multiplier on top of damage curve. 1.0 = no bonus.")]
        [SerializeField, Range(1f, 5f)] private float _headshotMultiplier = 2f;
        public float HeadshotMultiplier => _headshotMultiplier;

        [Tooltip("Chance (0-1) to roll a critical hit.")]
        [SerializeField, Range(0f, 1f)] private float _critChance = 0f;
        public float CritChance => _critChance;

        [Tooltip("Damage multiplier when a crit is rolled.")]
        [SerializeField, Range(1f, 5f)] private float _critMultiplier = 1.5f;
        public float CritMultiplier => _critMultiplier;

        /// <summary>Evaluate the damage curve at a normalised travel distance.</summary>
        public float EvaluateDamage(float normalisedDistance)
            => _damageCurve.Evaluate(Mathf.Clamp01(normalisedDistance));

        public bool IsDamageConstant()
        {
            if (_damageCurve.length == 0) return true;
            float first = _damageCurve.Evaluate(0f);
            return Mathf.Approximately(_damageCurve.Evaluate(0.5f), first)
                && Mathf.Approximately(_damageCurve.Evaluate(1f),   first);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Visual (C#-only)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Visual")]

        [Tooltip("Sprite drawn for this projectile. Null = trail-only.")]
        [SerializeField] private Sprite _sprite;
        public Sprite ProjectileSprite => _sprite;

        [Tooltip("If false, no mesh quad is rendered. Useful when the trail IS the visual.")]
        [SerializeField] private bool _useSprite = true;
        public bool UseSprite => _useSprite;

        [Tooltip("Optional custom mesh shape. Null = default quad.")]
        [SerializeField] private ProjectileShapeSO _customShape;
        public ProjectileShapeSO CustomShape => _customShape;

        // ─────────────────────────────────────────────────────────────────────
        //  Trail (C#-only)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Trail")]

        [SerializeField] private bool _hasTrail = true;
        public bool HasTrail => _hasTrail;

        [Tooltip("Trail material. Must be assigned if HasTrail is true.")]
        [SerializeField] private Material _trailMaterial;
        public Material TrailMaterial => _trailMaterial;

        [Tooltip("When false, uses the material's own colour. When true, applies the gradient below.")]
        [SerializeField] private bool _useGradientOverride = false;
        public bool UseGradientOverride => _useGradientOverride;

        [Tooltip("Gradient applied to the trail when UseGradientOverride is true.")]
        [SerializeField] private Gradient _trailGradient;
        public Gradient TrailGradient => _trailGradient;

        [Tooltip("Trail length in seconds. Shorter = less geometry = better performance.")]
        [SerializeField, Range(0.02f, 2f)] private float _trailTime = 0.15f;
        public float TrailTime => _trailTime;

        [SerializeField, Range(0f, 1f)] private float _trailStartWidth = 0.08f;
        [SerializeField, Range(0f, 1f)] private float _trailEndWidth   = 0f;
        public float TrailStartWidth => _trailStartWidth;
        public float TrailEndWidth   => _trailEndWidth;

        [Tooltip("Minimum world-space distance between trail vertices.\n" +
                 "Higher = fewer vertices = better performance.")]
        [SerializeField, Range(0.01f, 2f)] private float _trailMinVertexDistance = 0.1f;
        public float TrailMinVertexDistance => _trailMinVertexDistance;

        [Tooltip("Number of cap vertices on the trail end. 0 = flat, 2-4 = rounded.")]
        [SerializeField, Range(0, 4)] private int _trailCapVertices = 2;
        public int TrailCapVertices => _trailCapVertices;

        [Tooltip("Share one material instance across all projectiles of this type for better batching.")]
        [SerializeField] private bool _useSharedTrailMaterial = true;
        public bool UseSharedTrailMaterial => _useSharedTrailMaterial;

        // ─────────────────────────────────────────────────────────────────────
        //  Impact (C#-only)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Impact")]

        [SerializeField] private PoolableParticleType _impactEffectType;
        public PoolableParticleType ImpactEffectType => _impactEffectType;

        // ─────────────────────────────────────────────────────────────────────
        //  RustSpawnParams helper
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Produces minimal Rust-facing spawn parameters from this config.
        /// Called once per spawn event by BatchSpawnHelper.
        /// </summary>
        public RustSpawnParams GetRustSpawnParams(float speedOverride = -1f)
        {
            float speed = speedOverride > 0f ? speedOverride : ResolveSpeed();

            float scaleStart  = _useScaleGrowth ? _fullSizeX * _spawnScaleFraction : 1f;
            float scaleTarget = _useScaleGrowth ? _fullSizeX : 1f;
            float scaleSpeed  = _useScaleGrowth ? _growthSpeed : 0f;

            return new RustSpawnParams
            {
                Speed         = speed,
                MovementType  = (byte)_movementType,
                PiercingType  = (byte)_piercingType,
                MaxCollisions = _maxCollisions,
                Lifetime      = _lifetime,
                GravityAy     = _gravityScale,
                ScaleStart    = scaleStart,
                ScaleTarget   = scaleTarget,
                ScaleSpeed    = scaleSpeed,
                Is3D          = _is3D
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Wave/Circular movement registration helpers
        // ─────────────────────────────────────────────────────────────────────

        [Header("Wave Movement (only used when MovementType = Wave)")]
        [SerializeField] private float _waveAmplitude   = 1f;
        [SerializeField] private float _waveFrequency   = 2f;
        [SerializeField] private float _wavePhaseOffset = 0f;
        [SerializeField] private bool  _waveVertical    = false;

        [Header("Circular Movement (only used when MovementType = Circular)")]
        [SerializeField] private float _circularRadius       = 0.5f;
        [SerializeField] private float _circularAngularSpeed = 180f;
        [SerializeField] private float _circularStartAngle   = 0f;

        /// <summary>
        /// Register movement params with Rust for Wave or Circular movement types.
        /// Called by ProjectileRegistry after assigning ConfigId.
        /// </summary>
        public void RegisterMovementParams()
        {
            switch (_movementType)
            {
                case ProjectileMovementType.Wave:
                    ProjectileLib.register_wave_params(
                        ConfigId, _waveAmplitude, _waveFrequency,
                        _wavePhaseOffset, _waveVertical ? (byte)1 : (byte)0);
                    break;

                case ProjectileMovementType.Circular:
                    ProjectileLib.register_circular_params(
                        ConfigId, _circularRadius,
                        _circularAngularSpeed, _circularStartAngle);
                    break;
            }
        }

        public void UnregisterMovementParams()
        {
            switch (_movementType)
            {
                case ProjectileMovementType.Wave:
                    ProjectileLib.unregister_wave_params(ConfigId);
                    break;
                case ProjectileMovementType.Circular:
                    ProjectileLib.unregister_circular_params(ConfigId);
                    break;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            _maxSpeed = Mathf.Max(_maxSpeed, _minSpeed);
            if (_fullSizeX <= 0f) _fullSizeX = 0.2f;
            if (_fullSizeY <= 0f) _fullSizeY = 0.2f;
        }
#endif
    }
}
