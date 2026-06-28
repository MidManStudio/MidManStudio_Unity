// Minimal interface your existing ProjectileVisual implements.
// Add ": INetworkProjectileVisual" to your ProjectileVisual class declaration.
// All the methods already exist on it — no new logic needed.

using UnityEngine;
using Unity.Netcode;

namespace MidManStudio.Projectiles.Network
{
    public interface INetworkProjectileVisual
    {
        bool HasHitTarget { get; }

        void Initialize(
            MID_AllProjectileNames projectileName,
            Vector3                currentPosition,
            Vector3                targetPosition,
            float                  positionDelta,
            Vector3                adjustedOffset,
            bool                   visualSynch,
            NetworkManager         networkManager);

        void UpdatePositionInterpolator(Vector3 position, int tick);
        void SetHitPosition(Vector3 hitPosition, int tick);
        void ReturnToPoolImmediate();
    }
}
