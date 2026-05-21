// packages/com.midmanstudio.projectilesystem/Runtime/Network/NetworkProjectileBase.cs
//
// Base NetworkBehaviour for physics-driven projectiles (grenades, rockets,
// bouncy rounds, sticky bombs — anything that needs a Rigidbody + PhysicsMaterial).
//
// PACKAGE RESPONSIBILITY:
//   - Lifetime management (server kills projectile after _lifetime seconds)
//   - Owner identity forwarded from the fire event
//   - OnImpact virtual callback (server-side; override for damage logic)
//   - Pool-safe despawn via MID_NetworkObjectPool
//   - Disabled on clients that are not the server (simulation is server-only)
//
// GAME RESPONSIBILITY (derive from this in your game assembly):
//   - Add [RequireComponent(typeof(Rigidbody))] and your PhysicsMaterial
//   - Override OnImpact() to apply damage / spawn VFX
//   - Override OnProjectileExpired() if you need custom expiry behaviour
//   - Set _applyInitialForce=true and tune _initialSpeed for rocket-style launches
//
// SPAWN PATH:
//   Server calls MID_MasterProjectileSystem.Instance.SpawnPhysicsProjectile(type, pos, rot)
//   which returns the NetworkObject already spawned.
//   Then cast: var proj = netObj.GetComponent<NetworkProjectileBase>();
//              proj.InitialiseProjectile(ownerMidId, firedByNetObjId, configId, speed, dir);
//
// POOL RETURN:
//   Call ReturnToPool() from OnImpact or anywhere else — safe to call multiple times.
//   Internally guards against double-return with _returned flag.

using System;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Pools;
using MidManStudio.Netcode.Pools;
using MidManStudio.Core.Logging;

namespace MidManStudio.Projectiles.Network
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Impact payload passed to OnImpact
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Data available when a physics projectile collides with something.
    /// Passed to OnImpact() on the server.
    /// </summary>
    public struct PhysicsProjectileImpact
    {
        /// <summary>World-space contact point.</summary>
        public Vector3 ContactPoint;

        /// <summary>Inward surface normal at the contact point.</summary>
        public Vector3 ContactNormal;

        /// <summary>The Collider that was hit.</summary>
        public Collider HitCollider;

        /// <summary>
        /// NetworkObject attached to the hit collider (or a parent), if any.
        /// Null when hitting static geometry.
        /// </summary>
        public NetworkObject HitNetworkObject;

        /// <summary>
        /// NetworkObjectId of the hit target, or 0 for static geometry.
        /// Safe to serialise into RPCs.
        /// </summary>
        public ulong HitNetworkObjectId;

        /// <summary>Relative speed of the projectile at impact (magnitude of collision.relativeVelocity).</summary>
        public float ImpactSpeed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  NetworkProjectileBase
    // ─────────────────────────────────────────────────────────────────────────

    [DisallowMultipleComponent]
    public class NetworkProjectileBase : NetworkBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Lifetime")]
        [Tooltip("Server kills this projectile after this many seconds regardless of impact.")]
        [SerializeField] protected float _lifetime = 8f;

        [Header("Initial Velocity (optional)")]
        [Tooltip("If true, Initialise() applies an initial force in the fire direction.\n" +
                 "Set false if your weapon script applies velocity directly on the Rigidbody.")]
        [SerializeField] private bool  _applyInitialForce = true;

        [Header("Pool")]
        [Tooltip("Pool type to return to on impact or expiry.\n" +
                 "Must match the type used in MID_MasterProjectileSystem.SpawnPhysicsProjectile().")]
        [SerializeField] private PoolableNetworkObjectType _poolType
            = PoolableNetworkObjectType.BaseProjectileBlueprint;

        [Header("Impact")]
        [Tooltip("Minimum relative impact speed to trigger OnImpact.\n" +
                 "Prevents micro-collision spam on rough geometry.")]
        [SerializeField] private float _minImpactSpeed = 0.5f;

        [Tooltip("Seconds after first impact before the projectile is returned to pool.\n" +
                 "A small delay lets the impact VFX ClientRpc arrive before despawn.")]
        [SerializeField] private float _postImpactDelay = 0.12f;

        [Header("Debug")]
        [SerializeField] protected MID_LogLevel _logLevel = MID_LogLevel.None;

        // ── Owner identity — written by InitialiseProjectile() ─────────────

        /// <summary>MID ID of the player or bot that fired this projectile.</summary>
        public ulong OwnerMidId             { get; private set; }

        /// <summary>NetworkObjectId of the weapon / character that fired.</summary>
        public ulong FiredByNetworkObjectId { get; private set; }

        /// <summary>Registered projectile config ID (for damage lookups).</summary>
        public ushort ConfigId              { get; private set; }

        /// <summary>True once InitialiseProjectile() has been called.</summary>
        public bool IsInitialised           { get; private set; }

        // ── Internal state ────────────────────────────────────────────────────

        private Rigidbody _rb;
        private float     _spawnTime;
        private bool      _returned;        // guard against double-return
        private bool      _impactHandled;   // guard against multiple OnImpact calls

        // ─────────────────────────────────────────────────────────────────────
        //  Unity lifecycle
        // ─────────────────────────────────────────────────────────────────────

        protected virtual void Awake()
        {
            _rb = GetComponent<Rigidbody>();
        }

        // NGO calls this each time the object is spawned from the pool.
        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _spawnTime     = Time.time;
            _returned      = false;
            _impactHandled = false;
            IsInitialised  = false;

            // Only the server runs the lifetime timer and physics simulation.
            // Clients receive position via NetworkTransform (add that component
            // to your derived class prefab — not included here so the base
            // class has no mandatory Netcode component dependencies).
            if (!IsServer) return;

            // Lifetime watchdog
            Invoke(nameof(OnProjectileExpiredInternal), _lifetime);
        }

        public override void OnNetworkDespawn()
        {
            CancelInvoke();
            base.OnNetworkDespawn();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public initialisation API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Initialise a freshly-spawned physics projectile.
        /// Call on server immediately after SpawnPhysicsProjectile() returns.
        /// </summary>
        /// <param name="ownerMidId">MID ID of the firing entity.</param>
        /// <param name="firedByNetObjId">NetworkObjectId of the weapon / character.</param>
        /// <param name="configId">Registered ProjectileConfigSO ID.</param>
        /// <param name="speed">Launch speed (world units/s).</param>
        /// <param name="direction">Normalised launch direction.</param>
        public virtual void InitialiseProjectile(
            ulong  ownerMidId,
            ulong  firedByNetObjId,
            ushort configId,
            float  speed,
            Vector3 direction)
        {
            if (!IsServer) return;

            OwnerMidId             = ownerMidId;
            FiredByNetworkObjectId = firedByNetObjId;
            ConfigId               = configId;
            IsInitialised          = true;

            if (_applyInitialForce && _rb != null)
            {
                _rb.velocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.AddForce(direction.normalized * speed, ForceMode.VelocityChange);
            }

            MID_Logger.LogDebug(_logLevel,
                $"InitialiseProjectile: owner={ownerMidId} cfgId={configId} spd={speed:F1}",
                nameof(NetworkProjectileBase));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Pool return
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Return this projectile to MID_NetworkObjectPool.
        /// Safe to call multiple times — subsequent calls are no-ops.
        /// Can be called from OnImpact, a coroutine, or external code.
        /// </summary>
        public void ReturnToPool()
        {
            if (_returned) return;
            _returned = true;

            CancelInvoke();

            if (!IsServer) return;

            if (MID_MasterProjectileSystem.HasInstance)
                MID_MasterProjectileSystem.Instance.ReturnPhysicsProjectile(
                    NetworkObject, _poolType);
            else
                NetworkObject.Despawn();

            MID_Logger.LogDebug(_logLevel,
                $"ReturnToPool: netId={NetworkObjectId}",
                nameof(NetworkProjectileBase));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Collision
        // ─────────────────────────────────────────────────────────────────────

        protected virtual void OnCollisionEnter(Collision collision)
        {
            if (!IsServer)             return;
            if (_impactHandled)        return;
            if (!IsInitialised)        return;

            float relSpeed = collision.relativeVelocity.magnitude;
            if (relSpeed < _minImpactSpeed) return;

            _impactHandled = true;

            // Build payload
            ContactPoint cp = collision.GetContact(0);
            var netObj = collision.collider.GetComponentInParent<NetworkObject>();

            var impact = new PhysicsProjectileImpact
            {
                ContactPoint        = cp.point,
                ContactNormal       = cp.normal,
                HitCollider         = collision.collider,
                HitNetworkObject    = netObj,
                HitNetworkObjectId  = netObj != null ? netObj.NetworkObjectId : 0,
                ImpactSpeed         = relSpeed
            };

            OnImpact(impact);

            // Freeze in place while the delay plays out then return to pool.
            // Override OnImpact to bypass if you want immediate return.
            if (_rb != null)
            {
                _rb.velocity        = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic     = true;
            }

            Invoke(nameof(ReturnToPool), _postImpactDelay);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Virtual callbacks — override in derived class
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Server-side impact callback. Override to apply damage, spawn VFX RPCs, etc.
        /// Base implementation does nothing — you are expected to override.
        ///
        /// Example override:
        ///   protected override void OnImpact(PhysicsProjectileImpact impact)
        ///   {
        ///       float dmg = config.EvaluateDamage(0f) * DamageMultiplier;
        ///       // apply to impact.HitNetworkObject ...
        ///       NotifyImpactClientRpc(impact.ContactPoint);
        ///       ReturnToPool();   // call early if you don't want the freeze delay
        ///   }
        /// </summary>
        protected virtual void OnImpact(PhysicsProjectileImpact impact) { }

        /// <summary>
        /// Server-side callback when the projectile expires without hitting anything.
        /// Override for smoke-signal VFX, UXO marking, etc.
        /// Base implementation returns to pool.
        /// </summary>
        protected virtual void OnProjectileExpired()
        {
            ReturnToPool();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Internal lifetime expiry
        // ─────────────────────────────────────────────────────────────────────

        private void OnProjectileExpiredInternal()
        {
            if (_returned) return;
            MID_Logger.LogDebug(_logLevel,
                $"Expired after {_lifetime:F1}s — netId={NetworkObjectId}",
                nameof(NetworkProjectileBase));
            OnProjectileExpired();
        }
    }
}
