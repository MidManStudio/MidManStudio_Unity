// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "WeaponDefinitionSO.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Core.Libraries;
using MidManStudio.Core.Pools;

namespace MidManStudio.Projectiles.Config
{
    /// <summary>
    /// Per-weapon data as an asset: fire rate, shot pattern, pellets and spread,
    /// physics and raycast tuning, pool types, audio and muzzle FX.
    ///
    /// Extends MID_LibraryItemSO (utilities), the base for custom library data.
    /// That means:
    ///   - ItemId (inherited) is a free string key for the pickup and inventory
    ///     system.
    ///   - The assets can be dropped into a MID_LibrarySO with LibraryId
    ///     "Weapons" and looked up through
    ///       MID_LibraryRegistry.Instance.GetItem<WeaponDefinitionSO>("Weapons", id)
    ///   - Nothing requires the registry: pickups reference a WeaponDefinitionSO
    ///     directly, so it works with no setup beyond creating the assets.
    ///
    /// WeaponId is a separate compact ushort (not the string ItemId) so
    /// WeaponController can sync "which weapon is equipped" cheaply over a
    /// NetworkVariable&lt;ushort&gt;. Every client has the same asset in its
    /// build (project data, not spawned data), so a small id is all that has to
    /// cross the wire.
    ///
    /// Only the test and sample scripts (WeaponController, NetworkTurretTarget in
    /// Tests/Runtime) read this asset. Nothing under Runtime/ or Editor/ does.
    /// </summary>
    [CreateAssetMenu(
        fileName = "WeaponDefinition",
        menuName = "MidManStudio/Projectile System/Weapon Definition",
        order = 141)]
    public class WeaponDefinitionSO : MID_LibraryItemSO
    {
        [Header("Identity")]
        [Tooltip("Compact id synced over the network as the 'currently equipped' " +
                 "weapon. Keep these unique across every WeaponDefinitionSO in your " +
                 "project — that's all that matters, they don't need to be sequential.")]
        [SerializeField] private ushort _weaponId;
        [SerializeField] private string _displayName = "Weapon";

        [Tooltip("Optional — the low-poly gun model to show once it's equipped. " +
                 "WeaponController will instantiate this under the player's weapon " +
                 "socket on equip if assigned; left null, switching still works, " +
                 "it just won't swap a visible model.")]
        [SerializeField] private GameObject _weaponModelPrefab;

        [Tooltip("Unused. WeaponController plays a procedural switch animation on the " +
                 "weapon holder now and no longer fires an Animator trigger. Kept so " +
                 "existing assets keep their serialized data.")]
        [SerializeField] private string _switchAnimTrigger;

        [Header("Projectile Config Type IDs")]
        [Tooltip("ProjectileConfigType enum value cast to int, 2D projectiles. " +
                 "Resolved via ProjectileConfigManager when available, falls back " +
                 "to a direct ushort cast otherwise — same convention as before.")]
        [SerializeField] private int _configTypeId2D = 0;
        [Tooltip("ProjectileConfigType enum value for 3D projectiles.")]
        [SerializeField] private int _configTypeId3D = 0;

        [Header("Fire Settings")]
        [SerializeField] private float _fireRate = 5f;
        [SerializeField, Range(1, 64)]   private int   _pelletsPerShot     = 1;
        [SerializeField, Range(0f, 45f)] private float _spreadDeg         = 0f;
        [SerializeField, Range(1, 32)]   private int   _raycastPelletCount = 1;

        [Header("Shot Pattern (optional)")]
        [SerializeField] private ProjectilePatternSO _shotPattern;

        [Header("Raycast Settings")]
        [SerializeField] private LayerMask _raycastLayers = -1;
        [SerializeField] private float     _raycastRange  = 200f;

        [Header("Physics Projectile Settings")]
        [SerializeField] private PoolableNetworkObjectType _physicsPoolType2D
            = PoolableNetworkObjectType.BaseProjectileBlueprint_2D;
        [SerializeField] private PoolableNetworkObjectType _physicsPoolType3D
            = PoolableNetworkObjectType.BaseProjectileBlueprint_3D;
        [SerializeField] private float _physicsProjectileSpeed  = 20f;
        [SerializeField] private float _physicsDamageMultiplier = 1f;

        [Header("Audio")]
        [SerializeField] private int   _fireSoundClipIndex = 0;
        [SerializeField, Range(0f, 1f)] private float _fireSoundVolume = 0.7f;
        [SerializeField] private AudioClip _fallbackFireClip;
        [SerializeField, Range(0f, 1f)]      private float _fallbackVolume        = 0.6f;
        [SerializeField, Range(0.01f, 0.3f)] private float _fallbackPitchVariance = 0.1f;

        [Header("Muzzle Flash — GlobalFX")]
        [SerializeField] private int   _muzzleFlashParticleCount = 4;
        [SerializeField, Range(0f, 1f)] private float _muzzleFlashVolume = 0.8f;

        // ── Public API ──────────────────────────────────────────────────────

        public ushort         WeaponId               => _weaponId;
        public string         DisplayName             => string.IsNullOrWhiteSpace(_displayName) ? ItemId : _displayName;
        public GameObject     WeaponModelPrefab       => _weaponModelPrefab;
        public string         SwitchAnimTrigger       => _switchAnimTrigger;

        public int   ConfigTypeId2D => _configTypeId2D;
        public int   ConfigTypeId3D => _configTypeId3D;

        public float FireRate            => _fireRate;
        public int   PelletsPerShot      => _pelletsPerShot;
        public float SpreadDeg           => _spreadDeg;
        public int   RaycastPelletCount  => _raycastPelletCount;

        public ProjectilePatternSO ShotPattern => _shotPattern;

        public LayerMask RaycastLayers => _raycastLayers;
        public float     RaycastRange  => _raycastRange;

        public PoolableNetworkObjectType PhysicsPoolType2D => _physicsPoolType2D;
        public PoolableNetworkObjectType PhysicsPoolType3D => _physicsPoolType3D;
        public float PhysicsProjectileSpeed  => _physicsProjectileSpeed;
        public float PhysicsDamageMultiplier => _physicsDamageMultiplier;

        public int   FireSoundClipIndex => _fireSoundClipIndex;
        public float FireSoundVolume    => _fireSoundVolume;
        public AudioClip FallbackFireClip     => _fallbackFireClip;
        public float     FallbackVolume       => _fallbackVolume;
        public float     FallbackPitchVariance => _fallbackPitchVariance;

        public int   MuzzleFlashParticleCount => _muzzleFlashParticleCount;
        public float MuzzleFlashVolume        => _muzzleFlashVolume;
    }
}
