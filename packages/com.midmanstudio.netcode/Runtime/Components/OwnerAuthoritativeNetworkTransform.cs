// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.netcode.md, section "OwnerAuthoritativeNetworkTransform.cs"
// ============================================================================

using Unity.Netcode.Components;

namespace MidManStudio.Netcode.Components
{
    /// <summary>
    /// Owner-authoritative NetworkTransform: the owning client drives the transform
    /// instead of the server. Drop-in replacement for the stock
    /// <see cref="NetworkTransform"/> with the same Inspector fields (no serialized
    /// fields are added), so it can replace the stock component on a prefab without
    /// losing its configured axes, thresholds and interpolation.
    ///
    /// Use it with NetworkRigidbody when the owner drives a Rigidbody:
    /// NetworkRigidbody mirrors the authority model of its NetworkTransform.
    /// </summary>
    public class OwnerAuthoritativeNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative() => false;
    }
}
