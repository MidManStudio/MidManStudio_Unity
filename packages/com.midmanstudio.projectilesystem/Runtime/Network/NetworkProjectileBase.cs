using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Pools;
using Unity.Netcode.Components;
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
        [SerializeField] protected PoolableNetworkObjectType _networkPoolType
            = PoolableNetworkObjectType.BaseProjectileBlueprint_2D;

        // ─────────────────────────────────────────────────────────────────────
        //  NetworkVariables
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
        //  Accessors
        // ─────────────────────────────────────────────────────────────────────

        protected ulong  OwnerMidId               => n_ProjectilesOwner.Value;
        protected ulong  FiredByNetObjId           => n_FiredByNetworkObjectId.Value;
        protected float  BulletVelocity            => n_BulletVelocity.Value;
        protected bool   VisualSynchEnabled        => n_EnableVisualSynch.Value;
        protected bool   IsBotOwned                => n_IsBotOwned.Value;
        protected byte   WeaponLevel               => n_CurrentWeaponLevel.Value;
        protected bool   ServerIsActuallyOwner     => n_ServerIsActualyOwner.Value;

        // ─────────────────────────────────────────────────────────────────────
        //  Visual
        // ─────────────────────────────────────────────────────────────────────

        protected INetworkProjectileVisual ProjectileVisualInstance { get; private set; }
        private   GameObject               _visualGO;

        // ─────────────────────────────────────────────────────────────────────
        //  Internal state
        // ─────────────────────────────────────────────────────────────────────

        private float _endOfLifeTime;
        private bool  _initialized;
        private bool  _destroying;

        // ─────────────────────────────────────────────────────────────────────
        //  Virtual extension points
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Override to false in derived classes that manage their own pool visual
        /// (e.g. PhysicsProjectile) to prevent a second, unparented visual from
        /// being spawned by the base class in OnNetworkSpawn.
        /// </summary>
        protected virtual bool ShouldAutoSpawnVisual => true;

        /// <summary>
        /// Called on non-server clients when the BulletVelocity NetworkVariable
        /// value is received from the server (i.e. > 0).  Override in derived
        /// classes to initialise visuals with the correct speed and direction.
        /// NOT called on the server — server initialises via OnProjectileInitialised.
        /// </summary>
        protected virtual void OnNetworkVelocityReceived() { }

        // ─────────────────────────────────────────────────────────────────────
        //  NGO lifecycle
        // ─────────────────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            _destroying  = false;
            _initialized = false;

            // Only spawn the base pool visual when the derived class has not
            // opted out (PhysicsProjectile manages its own visual).
            if (ShouldAutoSpawnVisual)
            {
                _visualGO = LocalObjectPool.Instance.GetObject(
                    _visualPoolType, transform.position, transform.rotation);

                ProjectileVisualInstance = _visualGO != null
                    ? _visualGO.GetComponent<INetworkProjectileVisual>()
                    : null;
            }

            // Subscribe so clients get a hook when velocity data arrives.
            n_BulletVelocity.OnValueChanged += HandleBulletVelocityChanged;

            base.OnNetworkSpawn();
        }

        public override void OnNetworkDespawn()
        {
            n_BulletVelocity.OnValueChanged -= HandleBulletVelocityChanged;

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
        //  NetworkVariable callback
        // ─────────────────────────────────────────────────────────────────────

        private void HandleBulletVelocityChanged(float oldVal, float newVal)
        {
            // Only act on clients (not the server — server uses OnProjectileInitialised).
            if (!IsServer && newVal > 0f)
                OnNetworkVelocityReceived();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Update — server lifetime watchdog
        // ─────────────────────────────────────────────────────────────────────

        protected override void Update()
        {
            if (!IsSpawned) return;

            // BUG FIX (projectiles spawn but never move on clients — visible as
            // "client sees it get pulled from the pool but it just sits there
            // until despawn"): this used to be
            //
            //     if (IsServer) { base.Update(); ... }
            //
            // which meant NetworkTransform's own Update() — the method that both
            // (a) detects local transform changes to broadcast on the authority
            // side AND (b) advances the interpolator that APPLIES incoming
            // position/rotation state on every non-authoritative peer — only ever
            // ran on the server. On a dedicated server that's "fine" for sending,
            // but every client (including the host's remote clients) never
            // called base.Update() at all, so the interpolated state NetworkTransform
            // received was parsed and stored but never applied to transform.position.
            // The projectile visually froze at its spawn pose and only visibly
            // changed again when OnNetworkDespawn tore it down — exactly the
            // reported symptom. NetworkTransform.Update() must run on every peer;
            // only the TTL watchdog below is actually server-only.
            base.Update();

            if (IsServer
                && _initialized
                && NetworkManager.ServerTime.TimeAsFloat >= _endOfLifeTime)
            {
                DestroyProjectile();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  NetworkTransform override
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

            n_ProjectilesOwner.Value       = ownerMidId;
            n_FiredByNetworkObjectId.Value = firedByNetworkObjectId;
            n_BulletVelocity.Value         = bulletVelocity;
            n_IsBotOwned.Value             = isBotOwned;
            n_CurrentWeaponLevel.Value     = weaponLevel;
            n_ServerIsActualyOwner.Value   = serverIsActualOwner;
            n_EnableVisualSynch.Value      = enableVisualSynch;

            _endOfLifeTime = NetworkManager.ServerTime.TimeAsFloat + TimeToLive;

            if (_visualGO != null)
                _visualGO.transform.SetParent(transform);

            _initialized = true;

            OnProjectileInitialised();
            InitialiseVisual();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Destroy
        // ─────────────────────────────────────────────────────────────────────

        protected void DestroyProjectile()
        {
            if (!IsServer || !IsSpawned || _destroying) return;
            _destroying = true;

            NotifyCollisionClientRpc();
            OnImpactServer();

            if (ProjectileVisualInstance != null)
            {
                ProjectileVisualInstance.ReturnToPoolImmediate();
                ProjectileVisualInstance = null;
                _visualGO                = null;
            }

            HasHitObjectClientRpc(transform.position, NetworkManager.ServerTime.Tick);
            NetworkObject.Despawn();
        }

        private void ReturnToNGOPool()
        {
            if (MID_NetworkObjectPool.HasInstance)
            {
                MID_NetworkObjectPool.Instance.ReturnNetworkObject(NetworkObject, _networkPoolType);
            }
        }
        // ─────────────────────────────────────────────────────────────────────
        //  RPCs
        // ─────────────────────────────────────────────────────────────────────

        [ClientRpc]
        private void HasHitObjectClientRpc(
            Vector3 position, int tick,
            ClientRpcParams p = default)
        {
            if (ProjectileVisualInstance != null)
                ProjectileVisualInstance.SetHitPosition(position, tick);
        }

        [ClientRpc]
        private void NotifyCollisionClientRpc(ClientRpcParams p = default)
        {
            OnCollisionNotifiedClient();
        }

        [ClientRpc]
        protected void SpawnImpactEffectClientRpc(
            Vector3 position,
            ClientRpcParams p = default)
        {
            OnSpawnImpactEffectClient(position);
        }

        [ClientRpc]
        protected void SpawnKillEffectClientRpc(
            Vector3 position,
            ClientRpcParams p = default)
        {
            OnSpawnKillEffectClient(position);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Virtual hooks
        // ─────────────────────────────────────────────────────────────────────

        protected virtual void OnProjectileInitialised()       { }
        protected virtual void OnImpactServer()                { }
        protected virtual void OnCollisionNotifiedClient()     { }
        protected virtual void OnSpawnImpactEffectClient(Vector3 position) { }
        protected virtual void OnSpawnKillEffectClient(Vector3 position)   { }
        protected virtual void InitialiseVisual()              { }
    }
}
