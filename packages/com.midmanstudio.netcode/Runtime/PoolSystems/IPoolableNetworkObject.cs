// IPoolableNetworkObject.cs
// Implement this on any NetworkBehaviour prefab that is managed by MID_NetworkObjectPool.
// The pool calls these methods at the appropriate lifecycle points so the object
// can reset its own state — no game-specific logic lives in the pool itself.

using UnityEngine;

namespace MidManStudio.Core.Netcode
{
    /// <summary>
    /// Implement on a NetworkBehaviour component on your pooled prefab.
    /// MID_NetworkObjectPool calls these instead of knowing about your game types.
    /// </summary>
    public interface IPoolableNetworkObject
    {
        /// <summary>
        /// Called once when the object is first created and added to the pool,
        /// and again each time it is returned to the pool.
        /// Reset all state here — disable visuals, stop effects, clear references.
        /// </summary>
        void OnPoolReset();

        /// <summary>
        /// Called just before the object is handed to a caller via GetNetworkObject().
        /// Apply spawn-time configuration here (position is already set by the pool).
        /// </summary>
        void OnPoolRetrieve();
    }
}
