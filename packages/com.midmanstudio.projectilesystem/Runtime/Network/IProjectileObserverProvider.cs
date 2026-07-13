using UnityEngine;

namespace MidManStudio.Projectiles.Network
{
    /// <summary>
    /// Package-side contract for distance-based interest management.
    ///
    /// This package has no idea what a "player" is in your game — no
    /// assumptions about a PlayerController type, a specific movement system,
    /// or how positions are tracked. This interface is the one hook it needs:
    /// given a connected client's id, where should that client be treated as
    /// observing the world from, for the purposes of deciding which
    /// projectile snapshots are actually worth sending them.
    ///
    /// Implement this once, anywhere in your game code that already knows
    /// where players are (a session/lobby manager, a spawn manager, whatever
    /// you've already got), then register it:
    ///
    ///     ServerProjectileAuthority.Instance.ObserverProvider = myProvider;
    ///
    /// Leave it unregistered (or leave ServerProjectileAuthority's "Enable
    /// Distance Culling" off) and this does nothing at all — every client
    /// gets every snapshot, exactly like before this existed. Opt-in, safe
    /// default, works identically for 2D and 3D games since it's just a
    /// Vector3 (a 2D game's observer position naturally has Z == 0, and
    /// distance math falls out the same either way).
    /// </summary>
    public interface IProjectileObserverProvider
    {
        /// <summary>
        /// Return true and the observer position for a connected client id, or
        /// false if this client currently has no valid observer position (still
        /// loading, spectating with no target picked yet, etc.). Clients this
        /// returns false for receive every projectile snapshot regardless of
        /// distance settings — "I don't know where they are" can't be safely
        /// treated as "they're far away," that would just make things vanish
        /// for players you haven't finished tracking yet.
        /// </summary>
        bool TryGetObserverPosition(ulong clientId, out Vector3 position);
    }
}
