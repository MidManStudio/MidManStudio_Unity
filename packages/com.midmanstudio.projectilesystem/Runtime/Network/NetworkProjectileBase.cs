// packages/com.midmanstudio.projectilesystem/Runtime/Network/NetworkProjectileBase.cs
//
// Extracted sync scaffolding from your Projectile.cs.
// Extends NetworkTransform — that IS the position sync.
// Owns ONLY:
//   - Identity NetworkVariables (owner, firedBy, velocity, visualSynch, isBotOwned, weaponLevel, serverIsActualOwner)
//   - OnNetworkSpawn  → get visual from LocalObjectPool, call Initialize
//   - OnNetworkDespawn → ReturnToPoolImmediate
//   - OnNetworkTransformStateUpdated → forward position tick to visual
//   - HasHitObjectClientRpc
//   - NotifyCollisionClientRpc   (your DoSometingOnCollisionClientRpc equivalent)
//   - SpawnImpactEffectClientRpc (your SpawnCoolProjectilCollisionEffectClientRpc equivalent)
//   - SpawnKillEffectClientRpc   (your SpawnPlayerKillEffectClientRpc equivalent)
//   - DestroyProjectile()
//   - InitialiseProjectile() — sets all NetworkVariables, starts lifetime clock
//   - Virtual hooks for game layer
//
// YOUR GAME LAYER (derive from this in your game assembly):
//   - Config lookup, damage, collision, explosion, effects — all yours
//   - Override OnProjectileInitialised() to set Rigidbody velocity etc.
//   - Override OnImpactServer() to call ExplodeProjectile() etc.
//   - Override OnCollisionNotifiedClient() for audio/particles
//   - Override OnSpawnImpactEffectClient() for LocalParticlePool call
//   - Override OnSpawnKillEffectClient() to Instantiate kill effect

using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using MidManStudio.Core.Pools;
using MidManStudio.Netcode.Pools;

namespace MidManStudio.Projectiles.Network
{
    [DisallowMultipleComponent]
    public class NetworkProjectileBase : NetworkTransform
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Inspector
        // ─────────────────────────────────────────────────────────────────────

        [Header("Lifetime")]
        public float TimeToLive = 8f;

        [Tooltip("Distance threshold: visual lerps to hit point; returns to pool when within this.")]
        public float EolPositionDelta = 0.20f;

        [Header("Visual Pool")]
        [Tooltip("Same PoolableObjectType you pass to LocalObjectPool in your Projectile.cs.")]
        [SerializeField] private PoolableObjectType _visualPoolType
            = PoolableObjectType.Projectile_Visual2D;

        [Header("Network Object Pool")]
        [SerializeField] private PoolableNetworkObjectType _networkPoolType
            = PoolableNetworkObjectType.BaseProjectileBlueprint;

        // ─────────────────────────────────────────────────────────────────────
        //  NetworkVariables
        //  Extracted 1-to-1 from your Projectile.cs NetworkVariables block.
        //  Names kept identical so grep/search works across both files.
        // ─────────────────────────────────────────────────────────────────────

        [Header("Network Variables")]
        [SerializeField]
        private NetworkVariable<ulong> n_ProjectilesOwner
            = new NetworkVariable<ulong>();

        [SerializeField]
        private NetworkVariable<ulong> n_FiredByNetworkObjectId
            = new NetworkVariable<ulong>();

        [SerializeField]
        private NetworkVariable<float> n_BulletVelocity
            = new NetworkVariable<float>();

        private NetworkVariable<bool> n_EnableVisualSynch
            = new NetworkVariable<bool>(true);

        private NetworkVariable<bool> n_IsBotOwned
            = new NetworkVariable<bool>();

        private NetworkVariable<byte> n_CurrentWeaponLevel
            = new NetworkVariable<byte>();

        private NetworkVariable<bool> n_ServerIsActualyOwner
            = new NetworkVariable<bool>();

        // ─────────────────────────────────────────────────────────────────────
        //  Accessors — read by derived class
        // ─────────────────────────────────────────────────────────────────────

        protected ulong  OwnerMidId               => n_ProjectilesOwner.Value;
        protected ulong  FiredByNetObjId           => n_FiredByNetworkObjectId.Value;
        protected float  BulletVelocity            => n_BulletVelocity.Value;
        protected bool   VisualSynchEnabled        => n_EnableVisualSynch.Value;
        protected bool   IsBotOwned                => n_IsBotOwned.Value;
        protected byte   WeaponLevel               => n_CurrentWeaponLevel.Value;
        protected bool   ServerIsActuallyOwner     => n_ServerIsActualyOwner.Value;

        // ─────────────────────────────────────────────────────────────────────
        //  Visual reference — your existing ProjectileVisual via interface
        // ─────────────────────────────────────────────────────────────────────

        protected INetworkProjectileVisual ProjectileVisualInstance { get; private set; }
        private   GameObject               _visualGO;

        // ─────────────────────────────────────────────────────────────────────
        //  Internal state
        // ─────────────────────────────────────────────────────────────────────

        private float _endOfLifeTime;
        private bool  _initialised;
        private bool  _destroying;

        // ─────────────────────────────────────────────────────────────────────
        //  NGO lifecycle
        // ─────────────────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            _destroying  = false;
            _initialised = false;

            // Get visual from pool — mirrors your Projectile.cs OnNetworkSpawn
            _visualGO = LocalObjectPool.Instance.GetObject(
                _visualPoolType, transform.position, transform.rotation);

            ProjectileVisualInstance = _visualGO != null
                ? _visualGO.GetComponent<INetworkProjectileVisual>()
                : null;

            base.OnNetworkSpawn();
        }

        public override void OnNetworkDespawn()
        {
            // Return visual — mirrors your Projectile.cs OnNetworkDespawn
            if (ProjectileVisualInstance != null)
            {
                if (_visualGO != null)
                    _visualGO.transform.SetParent(null);

                ProjectileVisualInstance.ReturnToPoolImmediate();
                ProjectileVisualInstance = null;
                _visualGO                = null;
            }

            base.OnNetworkDespawn();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Update — server lifetime watchdog
        //  Mirrors HandleServerUpdate() in your Projectile.cs
        // ─────────────────────────────────────────────────────────────────────

        protected override void Update()
        {
            if (!IsSpawned) return;

            if (IsServer)
            {
                base.Update(); // NetworkTransform tick

                if (_initialised
                    && NetworkManager.ServerTime.TimeAsFloat >= _endOfLifeTime)
                {
                    DestroyProjectile();
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  NetworkTransform override
        //  Mirrors OnNetworkTransformStateUpdated in your Projectile.cs exactly.
        // ─────────────────────────────────────────────────────────────────────

        protected override void OnNetworkTransformStateUpdated(
            ref NetworkTransformState oldState,
            ref NetworkTransformState newState)
        {
            if (newState.HasPositionChange
                && ProjectileVisualInstance != null
                && n_EnableVisualSynch.Value)
            {
                ProjectileVisualInstance.UpdatePositionInterpolator(
                    newState.GetPosition(),
                    newState.GetNetworkTick());
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Initialise
        //  Call on server right after SpawnPhysicsProjectile() returns.
        //  Sets all NetworkVariables then calls Initialize on the visual.
        //  Mirrors InitializeServerProjectile() in your Projectile.cs.
        // ─────────────────────────────────────────────────────────────────────

        public virtual void InitialiseProjectile(
            ulong  ownerMidId,
            ulong  firedByNetworkObjectId,
            float  bulletVelocity,
            bool   isBotOwned          = false,
            byte   weaponLevel         = 0,
            bool   serverIsActualOwner = false,
            bool   enableVisualSynch   = true)
        {
            if (!IsServer) return;

            // Write NetworkVariables
            n_ProjectilesOwner.Value       = ownerMidId;
            n_FiredByNetworkObjectId.Value = firedByNetworkObjectId;
            n_BulletVelocity.Value         = bulletVelocity;
            n_IsBotOwned.Value             = isBotOwned;
            n_CurrentWeaponLevel.Value     = weaponLevel;
            n_ServerIsActualyOwner.Value   = serverIsActualOwner;
            n_EnableVisualSynch.Value      = enableVisualSynch;

            // Lifetime clock — mirrors EndOfLifeTime = NetworkManager.ServerTime.TimeAsFloat + TimeToLive
            _endOfLifeTime = NetworkManager.ServerTime.TimeAsFloat + TimeToLive;

            // Parent visual under this transform on server
            // Mirrors ProjectileVisualInstance.transform.SetParent(transform) in your code
            if (_visualGO != null)
                _visualGO.transform.SetParent(transform);

            _initialised = true;

            // Let derived class do config lookup, set Rigidbody velocity, etc.
            OnProjectileInitialised();

            // Now call Initialize on the visual — derived class knows projectile name/config
            // so it does this call itself inside OnProjectileInitialised if needed,
            // OR we call the virtual below so base can wire it generically.
            InitialiseVisual();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Destroy
        //  Mirrors DestroyProjectile() in your Projectile.cs.
        //  Pattern: notify clients → virtual hook → return visual → Despawn.
        // ─────────────────────────────────────────────────────────────────────

        protected void DestroyProjectile()
        {
            if (!IsServer || !IsSpawned || _destroying) return;
            _destroying = true;

            // Mirrors DoSometingOnCollisionClientRpc call in your DestroyProjectile()
            NotifyCollisionClientRpc();

            // Virtual: derived class calls ExplodeProjectile(), SpawnKillEffectClientRpc, etc.
            OnImpactServer();

            // Mirrors CleanupVisualImmediate() + HasHitObjectClientRpc call in your code
            if (ProjectileVisualInstance != null)
            {
                ProjectileVisualInstance.ReturnToPoolImmediate();
                ProjectileVisualInstance = null;
                _visualGO                = null;
            }

            HasHitObjectClientRpc(transform.position, NetworkManager.ServerTime.Tick);

            NetworkObject.Despawn();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RPCs — extracted 1-to-1 from your Projectile.cs
        // ─────────────────────────────────────────────────────────────────────

        /// Mirrors HasHitObjectClientRpc in your Projectile.cs.
        [ClientRpc]
        private void HasHitObjectClientRpc(
            Vector3 position, int tick,
            ClientRpcParams p = default)
        {
            if (ProjectileVisualInstance != null)
                ProjectileVisualInstance.SetHitPosition(position, tick);
        }

        /// Mirrors DoSometingOnCollisionClientRpc in your Projectile.cs.
        [ClientRpc]
        private void NotifyCollisionClientRpc(ClientRpcParams p = default)
        {
            OnCollisionNotifiedClient();
        }

        /// Mirrors SpawnCoolProjectilCollisionEffectClientRpc in your Projectile.cs.
        [ClientRpc]
        protected void SpawnImpactEffectClientRpc(
            Vector3 position,
            ClientRpcParams p = default)
        {
            OnSpawnImpactEffectClient(position);
        }

        /// Mirrors SpawnPlayerKillEffectClientRpc in your Projectile.cs.
        [ClientRpc]
        protected void SpawnKillEffectClientRpc(
            Vector3 position,
            ClientRpcParams p = default)
        {
            OnSpawnKillEffectClient(position);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Virtual hooks — all empty, all yours to override
        // ─────────────────────────────────────────────────────────────────────

        /// Server. Called after NetworkVariables are set.
        /// Set Rigidbody velocity, physics material, collider size, etc.
        protected virtual void OnProjectileInitialised() { }

        /// Server. Called inside DestroyProjectile() before Despawn.
        /// Call ExplodeProjectile(), SpawnKillEffectClientRpc, etc.
        protected virtual void OnImpactServer() { }

        /// ALL clients. NotifyCollisionClientRpc arrived.
        /// Mirrors DoSometingOnCollisionClientRpc body.
        protected virtual void OnCollisionNotifiedClient() { }

        /// ALL clients. SpawnImpactEffectClientRpc arrived.
        /// Call LocalParticlePool.Instance.GetObject(...) here.
        protected virtual void OnSpawnImpactEffectClient(Vector3 position) { }

        /// ALL clients. SpawnKillEffectClientRpc arrived.
        /// Instantiate your kill effect prefab here.
        protected virtual void OnSpawnKillEffectClient(Vector3 position) { }

        /// Override to call ProjectileVisualInstance.Initialize() with your projectile name.
        /// Base does nothing — derived class knows the name/config.
        protected virtual void InitialiseVisual() { }
    }
}
