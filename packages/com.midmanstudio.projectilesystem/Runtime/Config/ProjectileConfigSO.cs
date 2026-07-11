using UnityEngine;
using System;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Core;
using SimulationMode = MidManStudio.Projectiles.Core.SimulationMode;

namespace MidManStudio.Projectiles.Config
{
    [CreateAssetMenu(
        fileName = "ProjectileConfig",
        menuName  = "MidManStudio/Projectile System/Projectile Config",
        order     = 10)]
    public class ProjectileConfigSO : ScriptableObject
    {
        // ── Identity ──────────────────────────────────────────────────────────
        [HideInInspector] public ushort ConfigId;

        // ── Simulation routing ────────────────────────────────────────────────
        [Header("Simulation")]
        [SerializeField] private bool _is3D = false;
        public bool Is3D => _is3D;

        [Tooltip("Optional override for this config's simulation mode.\n" +
                 "RustSim (default) lets Is3D select the correct buffer automatically.\n" +
                 "Only change this if you need Raycast, PhysicsObject, or LocalOnly.")]
        [SerializeField] private SimulationMode _preferredSimMode = SimulationMode.RustSim;
        public SimulationMode PreferredSimMode => _preferredSimMode;

        /// <summary>
        /// True when a non-default SimulationMode has been set on this config.
        /// RustSim is the default — the router handles 2D vs 3D via Is3D.
        /// </summary>
        public bool HasSimModeOverride => _preferredSimMode != SimulationMode.RustSim;

        // ── Routing hooks ─────────────────────────────────────────────────────
        public virtual bool RequiresPhysicsObject() => false;

        public virtual bool IsRaycastEligible()
        {
            if (_piercingType != ProjectilePiercingType.None) return false;
            if (_is3D)                                         return false;
            if (RequiresPhysicsObject())                       return false;
            return true;
        }

        // ── Movement ──────────────────────────────────────────────────────────
        [Header("Movement")]
        [SerializeField] private ProjectileMovementType _movementType = ProjectileMovementType.Straight;
        public ProjectileMovementType MovementType => _movementType;

        [SerializeField] private float _minSpeed = 10f;
        [SerializeField] private float _maxSpeed = 10f;
        public float MinSpeed => _minSpeed;
        public float MaxSpeed => _maxSpeed;

        public float ResolveSpeed()
        {
            return _minSpeed == _maxSpeed ? _minSpeed
                : UnityEngine.Random.Range(_minSpeed, _maxSpeed);
        }

        [SerializeField] private float _lifetime = 3f;
        public float Lifetime => _lifetime;

        [SerializeField] private float _gravityScale = 0f;
        public float GravityScale => _gravityScale;

        [SerializeField] private float _maxRange = 50f;
        public float MaxRange => _maxRange;

        // ── Piercing ──────────────────────────────────────────────────────────
        [Header("Piercing")]
        [SerializeField] private ProjectilePiercingType _piercingType = ProjectilePiercingType.None;
        public ProjectilePiercingType PiercingType => _piercingType;

        [SerializeField, Range(1, 16)] private byte _maxCollisions = 1;
        public byte MaxCollisions => _maxCollisions;

        // ── Collision Layers ──────────────────────────────────────────────────
        [Header("Collision Layers")]
        [Tooltip("Which Unity layers this projectile can register hits against.\n" +
                 "Default = Everything. Exclude the 'Player' layer to prevent\n" +
                 "friendly-fire or self-damage from pattern projectiles.")]
        [SerializeField] private LayerMask _hitLayers = ~0;

        /// <summary>
        /// Unity LayerMask of layers this projectile can hit.
        /// Value -1 (all bits set) means hit everything.
        /// </summary>
        public LayerMask HitLayers => _hitLayers;

        // ── Scale Growth ──────────────────────────────────────────────────────
        [Header("Size & Scale Growth")]
        [Tooltip("Width of the projectile in world units at full size.")]
        [SerializeField] private float _fullSizeX = 0.2f;

        [Tooltip("Height of the projectile in world units at full size.")]
        [SerializeField] private float _fullSizeY = 0.08f;

        [Tooltip("Enable animated scale growth from a small spawn size to full size.")]
        [SerializeField] private bool _useScaleGrowth = false;

        [Tooltip("Spawn scale as a fraction of FullSizeX when UseScaleGrowth is enabled.")]
        [SerializeField, Range(0.01f, 1f)] private float _spawnScaleFraction = 0.2f;

        [SerializeField, Range(1f, 30f)] private float _growthSpeed = 8f;

        public float FullSizeX          => _fullSizeX;
        public float FullSizeY          => _fullSizeY;
        public bool  UseScaleGrowth     => _useScaleGrowth;
        public float SpawnScaleFraction => _spawnScaleFraction;
        public float GrowthSpeed        => _growthSpeed;

        // ── Damage Profile ────────────────────────────────────────────────────
        [Header("Damage Profile")]
        [SerializeField] private AnimationCurve _damageCurve =
            AnimationCurve.Constant(0f, 1f, 25f);
        public AnimationCurve DamageCurve => _damageCurve;

        [SerializeField, Range(1f, 5f)] private float _headshotMultiplier = 2f;
        public float HeadshotMultiplier => _headshotMultiplier;

        [SerializeField, Range(0f, 1f)] private float _critChance = 0f;
        public float CritChance => _critChance;

        [SerializeField, Range(1f, 5f)] private float _critMultiplier = 1.5f;
        public float CritMultiplier => _critMultiplier;

        public float EvaluateDamage(float normalisedDistance)
            => _damageCurve.Evaluate(Mathf.Clamp01(normalisedDistance));

        public bool IsDamageConstant()
        {
            if (_damageCurve.length == 0) return true;
            float first = _damageCurve.Evaluate(0f);
            return Mathf.Approximately(_damageCurve.Evaluate(0.5f), first)
                && Mathf.Approximately(_damageCurve.Evaluate(1f),   first);
        }

        // ── Visual ────────────────────────────────────────────────────────────
        [Header("Visual")]
        [SerializeField] private Sprite _sprite;
        public Sprite ProjectileSprite => _sprite;

        [SerializeField] private bool _useSprite = true;
        public bool UseSprite => _useSprite;

        [SerializeField] private ProjectileShapeSO _customShape;
        public ProjectileShapeSO CustomShape => _customShape;

        // ── Trail ─────────────────────────────────────────────────────────────
        [Header("Trail")]
        [SerializeField] private bool _hasTrail = true;
        public bool HasTrail => _hasTrail;

        [SerializeField] private Material _trailMaterial;
        public Material TrailMaterial => _trailMaterial;

        [SerializeField] private bool _useGradientOverride = false;
        public bool UseGradientOverride => _useGradientOverride;

        [SerializeField] private Gradient _trailGradient;
        public Gradient TrailGradient => _trailGradient;

        [SerializeField, Range(0.02f, 2f)] private float _trailTime = 0.15f;
        public float TrailTime => _trailTime;

        [SerializeField, Range(0f, 1f)] private float _trailStartWidth = 0.08f;
        [SerializeField, Range(0f, 1f)] private float _trailEndWidth   = 0f;
        public float TrailStartWidth => _trailStartWidth;
        public float TrailEndWidth   => _trailEndWidth;

        [SerializeField, Range(0.01f, 2f)] private float _trailMinVertexDistance = 0.1f;
        public float TrailMinVertexDistance => _trailMinVertexDistance;

        [SerializeField, Range(0, 4)] private int _trailCapVertices = 2;
        public int TrailCapVertices => _trailCapVertices;

        [SerializeField] private bool _useSharedTrailMaterial = true;
        public bool UseSharedTrailMaterial => _useSharedTrailMaterial;

        // ── Impact ────────────────────────────────────────────────────────────
        [Header("Impact")]
        [SerializeField] private PoolableParticleType _impactEffectType;
        public PoolableParticleType ImpactEffectType => _impactEffectType;

        // ── RustSpawnParams helper ────────────────────────────────────────────
        public RustSpawnParams GetRustSpawnParams(float speedOverride = -1f)
        {
            float speed = speedOverride > 0f ? speedOverride : ResolveSpeed();

            float scaleStart  = _useScaleGrowth
                ? _fullSizeX * _spawnScaleFraction
                : _fullSizeX;
            float scaleTarget = _fullSizeX;
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

        // ── Wave/Circular registration ────────────────────────────────────────

        [Header("Wave Movement (only used when MovementType = Wave)")]
        [SerializeField] private float _waveAmplitude   = 1f;
        [SerializeField] private float _waveFrequency   = 1f;
        [SerializeField] private float _wavePhaseOffset = 0f;
        [SerializeField] private bool  _waveVertical    = false;

        // Public read-only accessors — used by DeterministicMotionMath for
        // client-side closed-form wave position calculation.
        public float WaveAmplitude   => _waveAmplitude;
        public float WaveFrequency   => _waveFrequency;
        public float WavePhaseOffset => _wavePhaseOffset;
        public bool  WaveVertical    => _waveVertical;

        [Header("Circular Movement (only used when MovementType = Circular)")]
        [SerializeField] private float _circularRadius       = 0.5f;
        [SerializeField] private float _circularAngularSpeed = 180f;
        [SerializeField] private float _circularStartAngle   = 0f;

        // Public read-only accessors — used by DeterministicMotionMath.
        // CircularAngularSpeed is in degrees/sec; callers convert to radians as needed.
        // CircularStartAngle is in degrees; callers convert to radians as needed.
        public float CircularRadius       => _circularRadius;
        public float CircularAngularSpeed => _circularAngularSpeed;
        public float CircularStartAngle   => _circularStartAngle;

        /// <summary>
        /// Registers this config's Wave/Circular movement parameters with the
        /// native sim. No-ops (with a logged warning) instead of throwing when
        /// the native library isn't available on this platform/architecture —
        /// previously an uncaught DllNotFoundException here (via ProjectileRegistry.
        /// Register(), often called from a coroutine such as
        /// TestSceneBootstrapper.Start()) could abort the caller mid-method,
        /// skipping any code queued after it — including lobby event subscriptions
        /// that never touch the native lib at all.
        /// </summary>
        public void RegisterMovementParams()
        {
            if (_movementType != ProjectileMovementType.Wave &&
                _movementType != ProjectileMovementType.Circular)
                return;

            if (!ProjectileLib.IsAvailable)
            {
                Debug.LogWarning(
                    $"[ProjectileConfigSO] '{name}': skipping native movement-param " +
                    "registration — projectile_core is unavailable on this device.");
                return;
            }

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
            if (!ProjectileLib.IsAvailable) return;

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
            if (_fullSizeX <= 0f) _fullSizeX = 0.05f;
            if (_fullSizeY <= 0f) _fullSizeY = 0.05f;

            if (Application.isPlaying)
                RegisterMovementParams();
        }
#endif
    }
}
