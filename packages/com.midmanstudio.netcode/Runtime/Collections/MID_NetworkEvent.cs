// MID_NetworkEvent<T> — the "invoke something, everyone gets it" pattern,
// as opposed to NetworkVariable's "set a value, subscribe to OnValueChanged"
// pattern. The actual underlying mechanism for this in Netcode is
// [ServerRpc]/[ClientRpc] — genuinely fire-and-forget, no persisted state,
// no late-join replay (confirmed against Unity's own 1.7.1 docs — an RPC
// simply doesn't run for anyone who wasn't connected at the moment it was
// sent; OnSynchronize is the separate mechanism you'd add if you also wanted
// late joiners caught up on *state*, which is explicitly not what this is
// for).
//
// WHY THIS IS COMPOSITION, NOT A READY-MADE BASE CLASS:
// Netcode's RPC support is IL-post-processing-generated for whichever
// NetworkBehaviour a [ServerRpc]/[ClientRpc]-attributed method is physically
// declared on. I looked for a way to supply that method from a shared base
// class so consumers wouldn't need to declare anything at all, but couldn't
// find a source confirming inherited RPC methods behave correctly across
// NetworkBehaviour subclasses on this specific version (1.7.1) — rather than
// assume, this composes instead: you still declare ONE thin forwarding RPC
// method per usage site, and this class gives you the clean C# event API
// (+=/-=) around it instead of hand-rolling subscriber lists.
//
// No type constraint on T — unlike MID_NetworkString/MID_NetworkDictionary,
// this class does zero serialization itself, it just holds and invokes a C#
// delegate. Whatever type Netcode's RPC serialization actually needs to
// support is enforced by your own [ClientRpc]/[ServerRpc] method signature,
// not by this wrapper — which means this works for plain `string` too (a
// real allocation-per-call, but for a one-off signal that's fine — see
// MID_NetworkString's file header for why that's NOT fine for continuously-
// synced state).
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
