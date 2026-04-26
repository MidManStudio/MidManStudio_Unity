# MidManStudio

Unity package monorepo for MidMan Studio packages.

## Packages

| Package | Description |
|---------|-------------|
| `com.midmanstudio.utilities` | Core utilities: tick dispatcher, logger, timers, pools |
| `com.midmanstudio.projectilesystem` | High-performance server-authoritative projectile system |

## Repo Structure
MidManStudio/
.github/workflows/ CI — stays here, never inside packages
rust_lib/ Rust native libraries (one sub-folder per package that needs one)
packages/ UPM packages
DevProject/ Unity 2022.3 development project (not shipped)
## Development Setup
1. Open `DevProject/` in Unity 2022.3
2. Packages are referenced via `file:` paths — changes are live immediately
3. To rebuild Rust libs: `cd rust_lib/projectile_core && cargo build --release`
4. CI builds all platforms on push to main (see `.github/workflows/build-rust-libs.yml`)
## Installing Packages
**Via git URL:**
https://github.com/YOUR_USERNAME/MidManStudio.git?path=/packages/com.midmanstudio.utilities#v1.0.0 https://github.com/YOUR_USERNAME/MidManStudio.git?path=/packages/com.midmanstudio.projectilesystem#v1.0.0
**Via OpenUPM** (once published):
Add scope `com.midmanstudio` to your project's scoped registry.
