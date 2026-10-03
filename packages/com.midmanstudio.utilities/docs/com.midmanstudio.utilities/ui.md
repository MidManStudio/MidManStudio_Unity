# com.midmanstudio.utilities: Ui

## Modules

### `MID_Button.cs`
**What it does:** Drop-in companion component for `Button` (`[RequireComponent(typeof(Button))]`)
adding a coroutine-driven click animation (`AnimationType`: ScalePop, Move*,
Bounce, Pulse, Shake, Rotate, FadeFlash; no tween library dependency), a
post-click cooldown that disables the button briefly to prevent
double-clicks, and an optional click sound. Falls back to scale/rotation-only
animations inside a `LayoutGroup`, since a LayoutGroup fights position
tweens every frame, and rebuilds the layout after those.

## Fixes and Problems

### `MID_Button.cs`
- **Bug:** Disabling the GameObject during the post-click cooldown window
  left the button permanently non-interactable. `HandleClick` sets
  `_canClick = false` and `_button.interactable = false`, then starts
  `ResetCooldown()` to flip both back after `_cooldown` seconds. Unity
  stops every running coroutine on a MonoBehaviour when it's disabled,
  including `ResetCooldown()`, and there was no `OnEnable()` to restore
  that state, so a disable/re-enable cycle during the cooldown window
  permanently stuck `_canClick` at false with nothing left to flip it back.
- **Fix:** Added `OnEnable()` resetting `_canClick` and
  `_button.interactable` to true, mirroring the transform-state reset
  `OnDisable()` already did.
