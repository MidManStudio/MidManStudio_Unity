using System.Collections;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Projectiles.Config;

namespace TestGame
{
    /// <summary>
    /// Scene-placed weapon pickup. OnTriggerEnter is a per-client local physics
    /// callback — it fires independently on every client's own simulation, for
    /// every player that overlaps it (owned-locally or remote-only), which is
    /// exactly why WeaponController.PickupWeapon() already internally no-ops
    /// unless IsOwner is true on the calling instance. That means granting the
    /// weapon needs zero RPCs — it's the same owner-authoritative pattern the
    /// rest of this project already uses for movement/firing. The only thing
    /// that genuinely needs server authority is making the pickup disappear
    /// for EVERYONE once collected, since a scene-placed NetworkObject is
    /// server-owned by default — that's the one RequireOwnership=false ServerRpc
    /// below.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [DisallowMultipleComponent]
    public class WeaponPickup : NetworkBehaviour
    {
        [SerializeField] private WeaponDefinitionSO _weapon;

        [Tooltip("Hide + disable the pickup once collected. Off = an infinitely reusable pad.")]
        [SerializeField] private bool _consumeOnPickup = true;

        [Tooltip("Seconds before a consumed pickup reappears. 0 or less = collected once, stays gone.")]
        [SerializeField] private float _respawnDelay = 15f;

        [SerializeField] private Renderer[] _visualRenderers;

        private readonly NetworkVariable<bool> _collected = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _collected.OnValueChanged += OnCollectedChanged;
            ApplyVisualState(_collected.Value);
        }

        public override void OnNetworkDespawn()
        {
            _collected.OnValueChanged -= OnCollectedChanged;
            base.OnNetworkDespawn();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_weapon == null || _collected.Value) return;

            var netObj = other.GetComponentInParent<NetworkObject>();
            if (netObj == null) return;

            var controller = netObj.GetComponent<WeaponController>();
            if (controller == null) return;

            // No-ops on every device except the actual owner's — see class doc.
            controller.PickupWeapon(_weapon);

            if (controller.IsOwner && _consumeOnPickup)
                RequestConsumeServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestConsumeServerRpc()
        {
            if (_collected.Value) return;
            _collected.Value = true;
            if (_respawnDelay > 0f) StartCoroutine(RespawnAfterDelay());
        }

        private IEnumerator RespawnAfterDelay()
        {
            yield return new WaitForSeconds(_respawnDelay);
            if (IsServer) _collected.Value = false;
        }

        private void OnCollectedChanged(bool _, bool collected) => ApplyVisualState(collected);

        private void ApplyVisualState(bool collected)
        {
            if (_visualRenderers != null)
                foreach (var r in _visualRenderers)
                    if (r != null) r.enabled = !collected;

            var col = GetComponent<Collider>();
            if (col != null) col.enabled = !collected;
        }
    }
}
