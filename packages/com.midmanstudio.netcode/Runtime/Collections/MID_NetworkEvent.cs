// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.netcode.md, section "MID_NetworkEvent.cs"
// ============================================================================

// MID_NetworkEvent<T> - "invoke something, everyone gets it", as opposed to
// NetworkVariable's "set a value, subscribe to OnValueChanged". The transport is
// a [ServerRpc]/[ClientRpc]: fire-and-forget, no persisted state, no replay for
// late joiners (an RPC only reaches peers that are connected when it is sent).
// To catch late joiners up on state, use a NetworkVariable or OnSynchronize.
//
// This is composition, not a base class. Netcode generates the RPC code for the
// NetworkBehaviour that declares the [ServerRpc]/[ClientRpc] method, so each
// usage site declares one thin forwarding RPC and this class supplies the C#
// event (+= / -=) around it.
//
// T has no constraint: nothing is serialized here. What T may be is decided by
// the signature of your own RPC method. A plain string works too, with an
// allocation per call (fine for a one-off signal, not for continuously synced
// state, see MID_NetworkString).
//
// USAGE:
//   public class ExplosionSignaler : NetworkBehaviour
//   {
//       public readonly MID_NetworkEvent<Vector3> OnExplosion = new();
//
//       public void TriggerExplosion(Vector3 position)
//       {
//           // Server-only in practice — gate this with IsServer if this method
//           // is reachable from client code too.
//           OnExplosion.Raise(position, ExplosionClientRpc);
//       }
//
//       [ClientRpc]
//       private void ExplosionClientRpc(Vector3 position) => OnExplosion.Receive(position);
//   }
//
//   // elsewhere:
//   explosionSignaler.OnExplosion.OnRaised += pos => SpawnExplosionFX(pos);
//
// For a client -> server signal, same shape, just call Raise from the client
// and forward through a [ServerRpc] instead:
//
//   public readonly MID_NetworkEvent<None> OnReadyPressed = new();
//   public void PressReady() => OnReadyPressed.Raise(default, _ => ReadyServerRpc());
//   [ServerRpc] private void ReadyServerRpc() => OnReadyPressed.Receive(default);
//
// (Unity.Netcode doesn't ship a built-in "no payload" type — use a throwaway
// `struct None { }` for events that don't need to carry any data.)

using System;

namespace MidManStudio.Netcode.Collections
{
    public sealed class MID_NetworkEvent<T>
    {
        /// <summary>
        /// Fires on this peer whenever Receive() is called — i.e. whenever the
        /// RPC you've wired this to actually arrives (or, for the sending
        /// side, immediately if invokeLocally is true).
        /// </summary>
        public event Action<T> OnRaised;

        private readonly bool _invokeLocally;

        /// <param name="invokeLocally">
        /// If true (default), Raise() fires OnRaised immediately on the
        /// calling peer as well as sending the RPC — matches how you'd
        /// usually want a server-side "this happened" signal to also affect
        /// the server's own local state without waiting on its own RPC round
        /// trip (which wouldn't even happen for a ClientRpc sent from a
        /// dedicated server with no local client anyway). Set false if the
        /// sending side should only react once its own RPC bounces back
        /// (rare — normally only relevant if you're deliberately routing
        /// through a ServerRpc -> ClientRpc round trip for ordering reasons).
        /// </param>
        public MID_NetworkEvent(bool invokeLocally = true)
        {
            _invokeLocally = invokeLocally;
        }

        /// <summary>
        /// Call this from inside your own [ClientRpc]/[ServerRpc] method body
        /// — that's the one line of boilerplate this class doesn't remove.
        /// Raises OnRaised for anything subscribed on this peer.
        /// </summary>
        public void Receive(T value) => OnRaised?.Invoke(value);

        /// <summary>
        /// Call this from the sending side. Raises locally first (if
        /// invokeLocally), then invokes whatever RPC delegate you pass in to
        /// actually send it over the wire — keeps the "fire locally + tell
        /// everyone else" two-step in one place instead of repeating it at
        /// every call site.
        /// </summary>
        public void Raise(T value, Action<T> sendRpc)
        {
            if (_invokeLocally) OnRaised?.Invoke(value);
            sendRpc?.Invoke(value);
        }
    }

    /// <summary>Throwaway payload type for events that don't carry any data.</summary>
    public struct None { }
}
