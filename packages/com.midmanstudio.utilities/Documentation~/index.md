# MidMan Studio Utilities

Core runtime utilities for Unity 2022.3+. No game-specific dependencies — pull in only
the systems you use.

## What's included

| System | Purpose |
|---|---|
| Tick Dispatcher | Shared interval dispatcher — replaces per-object `Update()` |
| Tick Delay | Zero-allocation delayed/repeating actions |
| Auto Reference | Attribute-driven auto-wiring of Component/GameObject/interface fields |
| Logger | Leveled logging singleton |
| Singletons | MonoSingleton base classes |
| Observable Values | Change-notifying value wrappers |
| Events | Lightweight event bus |
| Pool System | Object/particle pools, trail renderer pool |
| Audio | Audio manager and playback pooling |
| FX System | Effect playback/pooling |
| Timers | Countdown, stopwatch, and value-interpolation timers |
| Library System | Data library / registry pattern |
| Scene Management | Scene dependency injection and load utilities |
| UI State System | State-driven UI panel management |
| Sticky Note | Edit-mode-visible scene note overlay |
| Editor Tools | Script utilities, execution order, sprite-to-tilemap, and more |

Full per-system API reference lives in [`APICATALOG.md`](../APICATALOG.md) — this page is
the getting-started overview, not the reference.

## Installation

Add via git URL through the Unity Package Manager, pinned to a release tag:

```
https://github.com/MidManStudio/MidManStudio_Unity.git?path=packages/com.midmanstudio.utilities#utilities/v1.0.0
```

See the [root README](https://github.com/MidManStudio/MidManStudio_Unity#readme) for the
full list of published tags and general repo layout.

## Getting started

1. Install the package (above).
2. Import the **Setup Demo** sample from Package Manager — it's a working manager-prefab
   hierarchy already wired to the Tick Dispatcher, Pool System, Audio System, Library
   System, and UI State System.
3. Follow [`scenesetup.md`](./scenesetup.md) to hand-build the same test scenes yourself,
   system by system — useful when you want to see exactly how each piece is wired instead
   of starting from the pre-built sample.

## Auto Reference — quick start

The most common source of "why is this field null" bugs in Unity is forgetting to
drag-and-drop a reference in the inspector. Auto Reference removes that step:

```csharp
[MID_AutoRefable]
public class MyPanel : MonoBehaviour
{
    [SerializeField] private TMP_Text _titleText;
    [SerializeField] private Image _icon;
}
```

Add a `MID_AutoRef` component to the same GameObject — or open
`MidManStudio > Utilities > Auto Reference` and let it add one for you — and it fills in
`_titleText` / `_icon` by searching self, children, and (if enabled) an external search
root. If more than one candidate of the right type exists, it's disambiguated by matching
the field name against the candidate's GameObject name. See
[`APICATALOG.md §19`](../APICATALOG.md#19-auto-reference) for the full API.

## Version history

See [`CHANGELOG.md`](../CHANGELOG.md) for what shipped in each release.

## Support

Open an issue on the [GitHub repo](https://github.com/MidManStudio/MidManStudio_Unity/issues).
