using Unity.Netcode.Components;

namespace MidManStudio.Netcode.Components
{
    /// <summary>
    /// Owner-authoritative NetworkTransform. Drop-in replacement for the stock
    /// Unity <see cref="NetworkTransform"/> — same Inspector fields, only the
    /// authority model changes.
    ///
    /// ROOT CAUSE OF "client player can't move":
    /// Stock NetworkTransform is server-authoritative by default
    /// (OnIsServerAuthoritative() == true). NetworkRigidbody mirrors whatever
    /// authority NetworkTransform reports — Unity's own docs put it plainly:
    /// "Whether the NetworkTransform is server authoritative (default) or owner
    /// authoritative, the NetworkRigidBody authority model will mirror it."
    ///
    /// On the HOST, IsServer == true for the host's own player, so "server
    /// authoritative" and "owner authoritative" happen to point at the same
    /// instance and movement just works. On a pure CLIENT (not host), IsServer
    /// is false even for that client's OWN owned player — NetworkedDimensionPlayer
    /// correctly checks IsOwner and sets the Rigidbody non-kinematic in 3D, but
    /// the stock NetworkTransform/NetworkRigidbody pair checks IsServer instead,
    /// decides this instance is non-authoritative, and forces isKinematic back to
    /// true every frame. Nothing sends an RPC to ask the server to move it either
    /// (movement here is fully local/owner-driven), so the object just sits frozen
    /// on that client's own screen. That's the "not separating client [IsServer]
    /// and player [IsOwner]" mix-up — baked into NGO's own default, not a typo in
    /// this project's code.
    ///
    /// FIX: flip authority to the owning client instead of the server. No new
    /// serialized fields are added, so this can be swapped in for the existing
    /// NetworkTransform component on Player.prefab without losing any of its
    /// configured sync-axis/threshold/interpolation values (see accompanying
    /// prefab edit instructions).
    /// </summary>
    public class OwnerAuthoritativeNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative() => false;
    }
}
