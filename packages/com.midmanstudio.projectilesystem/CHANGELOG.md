# com.midmanstudio.projectilesystem

- `ProjectileConfigSO._hitLayers` — `LayerMask` controlling which Unity layers a projectile can register hits against
- `ProjectileConfigSO` public accessors for wave/circular movement parameters (`WaveAmplitude`, `WaveFrequency`, `WavePhaseOffset`, `WaveVertical`, `CircularRadius`, `CircularAngularSpeed`, `CircularStartAngle`) so `DeterministicMotionMath` can read them without reflection
- `ProjectileConfigManager` + `ProjectileConfigMappingSO` — enum-based config resolution system (`ProjectileConfigType.MyBullet` → `ushort configId`)
- `ProjectileConfigTypeExtensions.Fire()` — extension method allowing `system.Fire((int)ProjectileConfigType.X, pts, n, ctx)` directly
- `ProjectileConfigGeneratorSettingsSO` + `ProjectileConfigProviderSO` + `ProjectileConfigEntry` — SO-driven Config Type Generator
- `ProjectileConfigGenerator` editor window — writes `ProjectileConfigType.cs` + updates `ProjectileConfigMappingSO`; lock-file backed
- `ProjectileConfigBootstrapper` — auto-creates default config assets on first package import
- `PatternShape.Formula` in `ProjectilePatternSO` — H(i,n) and V(i,n) math expressions for per-projectile shot angles
- `Preset.Formula` in `ProjectileShapeSO` — parametric X(t) / Y(t) mesh generation; center-fan triangulation
- `MathFormulaEvaluator` — self-contained recursive-descent expression parser with 30+ math functions and built-in example presets
- `ProjectilePatternEditor` / `ProjectileShapeEditor` — custom inspectors with live formula validation, example dropdowns, interactive viewport
- `DeterministicMotionMath` — closed-form 2D/3D position + velocity formulas for Wave and Circular types; perpendicular axis helpers
- `TrailObjectPool.SyncToSimulation3D()` — syncs 3D Rust buffer positions to trail slots
- `ProjectileImpactHandler` — `GlobalFXManager` integration path; per-config `EffectType` bindings; headshot override
- `ServerProjectileAuthority._renderer2D/_renderer3D` + `LateUpdate()` — host now renders from server buffer
- `ClientPredictionManager.SpawnLocalPhysicsVisual()` / `KillPhysicsVisual()` — explicit physics pool visual lifecycle
- `RaycastProjectileHandler._trustClientOnValidationMiss` — configurable fallback when server re-validation raycast misses desynced targets
- `ProjectileVisual_.EnsureShapeMeshComponents()` — runtime creation of `MeshFilter/MeshRenderer` for shape configs (prefabs no longer need them pre-added)
