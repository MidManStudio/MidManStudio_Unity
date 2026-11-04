// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "INetworkProjectileVisual.cs"
// ============================================================================
// Contract for a game-side projectile visual that follows a NetworkProjectileBase.
// A visual class opts in by declaring ": INetworkProjectileVisual" and
// implementing the members below. NetworkProjectileBase looks for it with
// GetComponent on the pooled visual object. Nothing in this package implements
// it, and PhysicsProjectileBase (the only subclass of NetworkProjectileBase here)
// manages its own visual and never uses it.

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
