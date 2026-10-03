# com.midmanstudio.utilities: Events

## Modules

### `MID_GameEvent.cs`
**What it does:** ScriptableObject-based event channel (`Raise`, `Register`,
`Deregister`, `ListenerCount`). Zero coupling between sender and receiver;
create an asset via `MidManStudio > Utilities > Game Event` and wire it to
one or more `MID_GameEventListener` components.

### `MID_GameEventListener.cs`
**What it does:** MonoBehaviour that self-registers with a `MID_GameEvent`
on enable and deregisters on disable, firing a UnityEvent (`OnEventRaised`)
when the event raises.

**Decisions:**
- `OnEnable`/`OnDisable` are `protected virtual`, not `private`, specifically
  so `MID_DelayedGameEventListener` can override `OnDisable` and call
  `base.OnDisable()` rather than silently shadowing it. See this file's
  Fixes and Problems entry and `MID_DelayedGameEventListener.cs`'s.

### `MID_DelayedGameEventListener.cs`
**What it does:** Subclass of `MID_GameEventListener` that fires an
immediate response (the base behaviour) and a delayed response after
`_delay` seconds, scheduled via `MID_TickDelay` (no coroutine/Task
allocation).

### `MID_EventBus.cs`
**What it does:** Typed static event bus (`MID_EventBus<T>`): one channel
per payload type `T` (must implement the `IMIDEvent` marker interface),
with `Subscribe`/`Unsubscribe`/`Raise`/`ClearAll`. `MID_EventBusRegistry`
lets you register several channels' `ClearAll` for one bulk teardown call
on scene unload.

### `MID_EventUtilities.cs`
**What it does:** Safe subscribe/unsubscribe/`IsSubscribed` helpers for
plain `Action`/`Action<T>`/`Action<T1,T2>` fields you manage directly,
instead of `MID_EventBus`. Prevents duplicate subscriptions and
null-reference unsubscribes.

## Fixes and Problems

### `MID_GameEventListener.cs` / `MID_DelayedGameEventListener.cs`
- **Bug:** `MID_DelayedGameEventListener` declared its own `private void
  OnDisable()` (to cancel its pending `MID_TickDelay` handle), and the base
  `MID_GameEventListener` also declared `private void OnDisable()` (to call
  `_gameEvent.Deregister(this)`). Unity's MonoBehaviour message dispatch
  only invokes the most-derived declaration of a lifecycle method when a
  base and a subclass both declare one privately (no "new"/"override"
  relationship exists between two unrelated private methods with the same
  name); the base method isn't called at all for instances of the
  subclass. In practice this meant a `MID_DelayedGameEventListener` never
  deregistered itself from its `MID_GameEvent` when disabled: the listener
  stayed registered, so a disabled listener would still receive
  `OnEventRaised()` (immediate response, and a newly scheduled delayed
  response) the next time the event fired, and `MID_GameEvent.ListenerCount`
  would never drop for a disabled-but-not-destroyed delayed listener.
- **Fix:** Changed `MID_GameEventListener.OnEnable`/`OnDisable` from
  `private` to `protected virtual`, and `MID_DelayedGameEventListener.OnDisable`
  to `protected override`, calling `base.OnDisable()` after cancelling its
  own pending handle. Both now run correctly.
