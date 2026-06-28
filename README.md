# MidManStudio

Unity package monorepo for MidMan Studio packages.

## Packages

| Package | Description |
|---------|-------------|
| `com.midmanstudio.utilities` | Core utilities: tick dispatcher, logger, timers, pools, audio, FX, scene management, UI state |
| `com.midmanstudio.netcode` | NGO-specific utilities: network singletons, object pooling, connection management, LAN/WiFi lobby |
| `com.midmanstudio.projectilesystem` | High-performance server-authoritative projectile system (Rust simulation core) |

## Repo StructureMidManStudio_Unity/
├── .github/workflows/ CI — stays here, never inside packages
├── rust_lib/ Rust native libraries (one sub-folder per package that needs one)
│ ├── projectile_core/ Backs com.midmanstudio.projectilesystem
│ └── mid_audio/ Backs com.midmanstudio.utilities' audio limiter
├── packages/ UPM packages
│ ├── com.midmanstudio.utilities/
│ ├── com.midmanstudio.netcode/
│ └── com.midmanstudio.projectilesystem/
└── PackageSandbox/ Unity 2022.3 development project (not shipped)## Development Setup

1. Open `PackageSandbox/` in Unity 2022.3
2. Packages are referenced via `file:` paths — changes are live immediately
3. To rebuild Rust libs:
   - `cd rust_lib/projectile_core && cargo build --release`
   - `cd rust_lib/mid_audio && cargo build --release`
4. CI builds all platforms on push (see `.github/workflows/build-rust-libs.yml` and `build-audio-libs.yml`)

## Installing Packages

**Via git URL** (Unity Package Manager → Add package from git URL):https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.utilities#utilities/v1.0.0
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.netcode#netcode/v1.0.0
https://github.com/MidManStudio/MidManStudio_Unity.git?path=/packages/com.midmanstudio.projectilesystem#projectilesystem/v1.1.0> The `#tagname` segment pins the install to a specific release tag (see **Releases** below).
> Without it, UPM resolves to the latest commit on `master`, which is not version-locked
> and will silently change underneath consumers between sessions.

**Via local file path** (development — `Packages/manifest.json`):

```json
"com.midmanstudio.utilities": "file:../../MidManStudio_Unity/packages/com.midmanstudio.utilities"
```

**Via OpenUPM** (once published):

Add scope `com.midmanstudio` to your project's scoped registry in `Packages/manifest.json`.

## Releases

Each package is tagged and released independently using a `<package>/v<semver>` tag, e.g.
`utilities/v1.0.0`, `netcode/v1.0.0`, `projectilesystem/v1.1.0`. Pushing a tag in that format
triggers `.github/workflows/release-packages.yml`, which cuts the matching GitHub Release.

No tags have been pushed yet — the git-URL install lines above will not resolve until the
corresponding tag exists. See the package's own README for the full release checklist.
