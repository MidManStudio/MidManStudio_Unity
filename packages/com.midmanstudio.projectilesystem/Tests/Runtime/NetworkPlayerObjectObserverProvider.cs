using UnityEngine;
using Unity.Netcode;
using MidManStudio.Projectiles.Network;

namespace TestGame
{
    /// <summary>
    /// Reference IProjectileObserverProvider implementation for testing distance
    /// culling. Doesn't know or care about NetworkedDimensionPlayer specifically —
    /// it just asks NGO for whatever NetworkObject was spawned via
    /// SpawnAsPlayerObject(clientId) (see TestSceneBootstrapper.
    /// NetworkedSessionCoroutine, which already does exactly that) and returns
    /// its transform position. Server-only, matching
    /// NetworkSpawnManager.GetPlayerNetworkObject's own restriction — which is
    /// fine, ServerProjectileAuthority.SendSnapshots() (the only caller of any
    /// IProjectileObserverProvider) only ever runs server-side anyway.
    ///
    /// This works for ANY game using the standard SpawnAsPlayerObject pattern,
    /// not just this test scene — copy it into your own project as a starting
    /// point rather than treating it as test-only scaffolding.
    /// </summary>
    public class NetworkPlayerObjectObserverProvider : IProjectileObserverProvider
    {
        public bool TryGetObserverPosition(ulong clientId, out Vector3 position)
        {
            position = default;

            var spawnManager = NetworkManager.Singleton?.SpawnManager;
            if (spawnManager == null) return false;

            var playerObject = spawnManager.GetPlayerNetworkObject(clientId);
            if (playerObject == null) return false;

            position = playerObject.transform.position;
            return true;
        }
    }
}
