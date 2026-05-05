// ═══════════════════════════════════════════════════════════════════
// MidMan Studio Utilities — Test Scene Setup Guide
// ═══════════════════════════════════════════════════════════════════
//
// ── SCENE 1: TickDispatcher + TickDelay Bench ──────────────────────
//
// 1. Create new scene: Assets > Create > Scene > "TickBenchScene"
// 2. Create empty GameObject "BenchRunner"
//    Add component: MID_TickDelayBenchRunner
//    Add component: MID_TickDispatcherBench
// 3. Enter Play Mode
// 4. Open bench windows:
//    MidManStudio > Utilities > Tests > Tick Delay Bench
//    MidManStudio > Utilities > Tests > Tick Dispatcher Bench
// 5. Click "Run All" in each window
// 6. Expected GC result: MID_TickDelay = 0 B per call (green ✓)
//
// NOTE: If you see "DelayBenchGCResult is missing ExtensionOfNativeClass":
//   - Open the scene file in a text editor or the Unity hierarchy
//   - Find the GameObject with the missing component (yellow warning icon)
//   - In the Inspector click the gear icon on the missing script > Remove Component
//   - This happens because DelayBenchGCResult is a struct, not a MonoBehaviour
//
// ── SCENE 2: UIState System ────────────────────────────────────────
//
// STEP A — Generate your states:
// 1. Right-click Project > MidManStudio > Utilities > UI State Context Provider
//    Name it "MenuContextProvider"
//    Set contextName = "Menu"
//    Set packageId = "com.mygame"
//    Add states: MainMenu, Settings, Credits, Pause
// 2. MidManStudio > Utilities > UI State Context Generator > Generate Now
//    This creates: Runtime/UIState/Generated/MenuUIState.cs
//    (Flags enum: MainMenu=1, Settings=2, Credits=4, Pause=8)
//
// STEP B — Create the context asset:
// 3. Right-click Project > MidManStudio > Utilities > UI State Context
//    Name it "MenuContext"
//    contextDisplayName = "Menu"
//    enumTypeName = "MidManStudio.Core.UIState.MenuUIState"
//
// STEP C — Scene setup:
// 4. Create new scene "UIStateScene"
// 5. Create Canvas > create four panels:
//    Panel_MainMenu, Panel_Settings, Panel_Credits, Panel_Pause
// 6. Create empty GameObject "UIManager"
//    Add component: MID_UIStateManager
//    Assign Context = MenuContext SO
//    Set Initial State = (int)MenuUIState.MainMenu = 1
//    Add 4 UIStatePanelConfig entries (inspector shows named dropdowns):
//      Config 0: stateMask=MainMenu(1), show=[Panel_MainMenu]
//      Config 1: stateMask=Settings(2), show=[Panel_Settings], hide=[Panel_MainMenu]
//      Config 2: stateMask=Credits(4),  show=[Panel_Credits],  hide=[Panel_MainMenu]
//      Config 3: stateMask=Pause(8),    show=[Panel_Pause]
// 7. On Panel_Settings, add MID_UIStateVisibility:
//    Context = MenuContext, Show When = Settings ✓
// 8. Create a Button, add MID_UIStateButton:
//    Context = MenuContext, State = Settings
//    This button will show the Settings panel when clicked
//
// ── SCENE 3: Library System ────────────────────────────────────────
//
// STEP A — Create items:
// 1. Right-click Project > MidManStudio > Utilities > Library Item (Basic)
//    Create three items: "Sword", "Shield", "Potion"
//    Each gets a displayName and optional icon
//
// STEP B — Create a library:
// 2. Right-click Project > MidManStudio > Utilities > Library
//    Name it "ItemLibrary", set libraryId = "Items"
//    Drag your three items into the Items list
//
// STEP C — Register and retrieve:
// 3. Create empty GameObject "LibraryRegistry"
//    Add component: MID_LibraryRegistry
//    Add "ItemLibrary" to the Libraries list
// 4. In any MonoBehaviour:
//
//    private void Start()
//    {
//        var sword = MID_LibraryRegistry.Instance
//            .GetItem<MID_BasicLibraryItemSO>("Items", "Sword");
//        Debug.Log(sword.displayName);  // "Sword"
//    }
//
// ── SCENE 4: Pool System ───────────────────────────────────────────
//
// 1. Run Pool Type Generator with your entries
//    MidManStudio > Utilities > Pool Type Generator
// 2. Create a prefab for each pool type
// 3. Create empty GameObject "Pools"
//    Add LocalObjectPool, assign pool configs
//    Add LocalParticlePool if using particles
// 4. On game start: LocalObjectPool.Instance.CallInitializePool();
//    Spawning: var go = LocalObjectPool.Instance.GetObject(
//                  PoolableObjectType.YourType, pos, rot);
//    Returning: LocalObjectPool.Instance.ReturnObject(go, PoolableObjectType.YourType);
//
// ── SCENE 5: Audio System ──────────────────────────────────────────
//
// 1. Right-click > MidManStudio > Utilities > Audio Library
//    Create "MusicLibrary" and "SFXLibrary"
//    Add entries with string IDs (e.g. "menu_theme", "button_click")
// 2. Create empty GameObject "AudioManager"
//    Add MID_AudioManager
//    Assign MusicLibrary and SFXLibrary
//    Optionally assign AudioMixerGroups
// 3. Usage:
//    MID_AudioManager.Instance.PlayMusic("menu_theme");
//    MID_AudioManager.Instance.PlaySFX("button_click");
//    MID_AudioManager.Instance.SetMusicEnabled(false); // e.g. from settings
//
// ── SCENE 6: SceneDependencyInjector (for testing without bootstrap) ─
//
// 1. In any test scene, create "DependencyInjector" GameObject
//    Add SceneDependencyInjector
//    Enable autoInjectOnPlay = true
//    Add prefabs: AudioManager, PoolManager, LibraryRegistry, etc.
// 2. Press Play — all managers are instantiated automatically
//    No bootstrap scene needed for isolated testing
//
// ── RECOMMENDED PERSISTENT MANAGER PREFAB ORDER ───────────────────
//
// Create a "Managers" prefab with this hierarchy (order matters for init):
//   Managers (DontDestroyOnLoad)
//   ├── MID_Logger
//   ├── MID_TickDispatcher  ← must exist before any subscriber
//   ├── SusValueManager
//   ├── LocalObjectPool     ← calls CallInitializePool() which chains to:
//   ├── LocalParticlePool   ←   this (auto-chained, can also be separate)
//   ├── TrailRendererPool
//   ├── MID_AudioManager
//   ├── MID_LibraryRegistry
//   └── MID_UIStateManager  (one per screen context)
