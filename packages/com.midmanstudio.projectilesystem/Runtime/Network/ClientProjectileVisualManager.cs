// ClientProjectileVisualManager.cs
// Stub manager for non-local player projectile visuals.
// ClientPredictionManager delegates other-player spawns here.
// Implement the body in your game layer by extending or replacing this class.

using UnityEngine;

namespace MidManStudio.Projectiles.Network
{
    /// <summary>
    /// Manages visual projectiles for players other than the local client.
    /// ClientPredictionManager calls SpawnVisual and NotifyHit for every
    /// confirmed projectile whose ownerMidId does not match the local player.
    ///
    /// This is a package-level stub. Override behaviour in your game assembly
    /// by subscribing to MID_ProjectileNetworkBridge.SpawnConfirmedClientRpc
    /// and MID_ProjectileNetworkBridge.HitConfirmedClientRpc directly.
    /// </summary>
    public static class ClientProjectileVisualManager
    {
        /// <summary>
        /// Called when a non-local-player projectile is confirmed by the server.
        /// Spawn a visual object from the pool and set it moving toward its endpoint.
        /// </summary>
        /// <param name="projId">Server-assigned projectile ID.</param>
        /// <param name="configId">Registered config ID for visual lookup.</param>
        /// <param name="origin">World-space spawn origin.</param>
        /// <param name="direction">Normalised travel direction.</param>
        /// <param name="speed">Travel speed in world units per second.</param>
        /// <param name="ownerMidId">MID ID of the owning player.</param>
        /// <param name="isLocalPlayer">Always false when this method is called.</param>
        public static void SpawnVisual(
            int     projId,
            ushort  configId,
            Vector3 origin,
            Vector3 direction,
            float   speed,
            ulong   ownerMidId,
            bool    isLocalPlayer)
        {
            // Package stub — implement in your game assembly.
            // Example: look up a pooled visual by configId, place it at origin,
            // give it a velocity of direction * speed, and return it to the pool
            // when NotifyHit is called.
        }

        /// <summary>
        /// Called when the server confirms a hit for a non-local-player projectile.
        /// Stop the visual, snap it to hitPosition, play an impact effect, and
        /// return the visual object to the pool.
        /// </summary>
        /// <param name="projId">Server-assigned projectile ID.</param>
        /// <param name="hitPosition">World-space impact position.</param>
        /// <param name="playImpact">True if an impact particle should play.</param>
        public static void NotifyHit(int projId, Vector3 hitPosition, bool playImpact)
        {
            // Package stub — implement in your game assembly.
        }
    }
}
