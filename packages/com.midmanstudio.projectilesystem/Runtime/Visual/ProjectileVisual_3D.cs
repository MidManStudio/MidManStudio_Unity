using UnityEngine;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Visuals
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class ProjectileVisual_3D : ProjectileVisualBase
    {
        #region Inspector

        [Header("3D Renderers (auto-found if null)")]
        [SerializeField] private MeshFilter   _meshFilter;
        [SerializeField] private MeshRenderer _meshRenderer;

        [Header("Trail (auto-found in children if null)")]
        [SerializeField] private TrailRenderer _trailRenderer;

        [Header("Scale")]
        [Tooltip("Multiplier on top of config FullSizeX.")]
        [SerializeField] private float _scaleMultiplier = 1f;

        [Header("Material Fallback")]
        [Tooltip("Used when config has no sprite/atlas. Assign a simple Lit or Unlit material.\n" +
                 "If left empty, a shared default material is generated lazily at runtime (see " +
                 "GetDefaultFallbackMaterial) so the visual is never left with whatever material " +
                 "happened to already be on the renderer.")]
        [SerializeField] private Material _fallbackMaterial;

        #endregion

        #region State

        // FIX (native leak on domain reload): this was "created once, never
        // destroyed". Mesh is a native engine object — see the matching comment
        // in ProjectileVisual_2D._fallbackTexture for the full explanation of why
        // this causes Unity's "Leak Detected : Persistent allocates N individual
        // allocations" warning on the next domain reload, and why the fix below
        // (destroy before reload, via AssemblyReloadEvents) is the correct one.
        private static Mesh     _defaultCapsuleMesh;
        // Same pattern, same reasoning — see GetDefaultFallbackMaterial below.
        private static Material _defaultFallbackMaterial;

        private Material    _instancedMaterial;
        private bool        _trailConfigured;
        private ushort      _cachedConfigId;
        private bool        _configInitialised;

        #endregion

        #region Unity Lifecycle

        protected override void Awake()
        {
            base.Awake();

            // Auto-find all three renderer references if not assigned in inspector.
            // _trailRenderer had no fallback before — silently null → no trail.
            if (_meshFilter    == null) _meshFilter    = GetComponent<MeshFilter>();
            if (_meshRenderer  == null) _meshRenderer  = GetComponent<MeshRenderer>();
            if (_trailRenderer == null) _trailRenderer = GetComponentInChildren<TrailRenderer>(true);

            // Kill any stray SpriteRenderer from duplicated 2D prefabs.
            var sr = GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.enabled = false;
                Debug.LogWarning(
                    $"[ProjectileVisual3D] SpriteRenderer found on '{name}' and disabled. " +
                    "Remove it from the prefab — 3D visuals use MeshRenderer only.",
                    this);
            }
        }

        private void OnDestroy()
        {
            if (_instancedMaterial != null) Destroy(_instancedMaterial);
        }

        #endregion

        #region ProjectileVisualBase

        protected override void ApplyRotation(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return;
            Vector3 up = Mathf.Abs(Vector3.Dot(dir.normalized, Vector3.up)) > 0.99f
                ? Vector3.forward : Vector3.up;
            transform.rotation = Quaternion.LookRotation(dir.normalized, up);
        }

        protected override void OnInitialise(ProjectileConfigSO cfg)
        {
            bool configChanged = !_configInitialised || _cachedConfigId != ConfigId;
            if (configChanged)
            {
                _cachedConfigId    = ConfigId;
                _trailConfigured   = false;
                _configInitialised = true;
            }

            ApplyMesh(cfg);
            ApplyMaterial(cfg);
            ApplyScale(cfg);

            // GROWTH FIX ("gets spawned full scale rather than scaling up as
            // intended") — no-ops immediately if cfg is null or
            // UseScaleGrowth is false, leaving ApplyScale's one-shot value
            // just above untouched. See ProjectileVisualBase's own section
            // comment for the full explanation, and ApplyScaleAtSize below
            // for how this reproduces ApplyScale's length/width mapping at
            // each interpolated size during growth.
            RefreshScaleGrowth(cfg);

            ApplyTrail(cfg);
            OnInitialise3D(cfg);
        }

        protected override void OnReturnToPool()
        {
            _configInitialised = false;
            _trailConfigured   = false;

            if (_meshRenderer != null) _meshRenderer.enabled = true;

            if (_trailRenderer != null)
            {
                _trailRenderer.emitting = false;
                _trailRenderer.enabled  = false;
                _trailRenderer.Clear();
            }

            OnCleanup3D();
        }

        public override void HideProjectile()
        {
            if (_meshRenderer  != null) _meshRenderer.enabled  = false;
            if (_trailRenderer != null) _trailRenderer.emitting = false;
        }

        #endregion

        #region Sub-class Hooks

        protected virtual void OnInitialise3D(ProjectileConfigSO cfg) { }
        protected virtual void OnCleanup3D() { }

        #endregion

        #region Visual Setup

        private void ApplyMesh(ProjectileConfigSO cfg)
        {
            if (_meshFilter == null) return;
            Mesh mesh = cfg?.CustomShape?.GetMesh();
            if (mesh == null || mesh.vertexCount == 0)
                mesh = GetDefaultCapsule();
            _meshFilter.sharedMesh = mesh;
        }

        private void ApplyMaterial(ProjectileConfigSO cfg)
        {
            if (_meshRenderer == null) return;

            bool hasSprite = cfg != null && cfg.UseSprite && cfg.ProjectileSprite?.texture != null;

            if (hasSprite)
            {
                if (_instancedMaterial == null)
                {
                    var src = _meshRenderer.sharedMaterial ?? _fallbackMaterial ?? GetDefaultFallbackMaterial();
                    if (src != null) _instancedMaterial = new Material(src);
                }
                if (_instancedMaterial != null)
                {
                    _instancedMaterial.mainTexture = cfg.ProjectileSprite.texture;

                    if (_instancedMaterial.HasProperty("_UVRect"))
                    {
                        Vector4 uv = ProjectileRegistry.HasInstance
                            ? ProjectileRegistry.Instance.GetUVRect(ConfigId)
                            : new Vector4(0f, 0f, 1f, 1f);
                        _instancedMaterial.SetVector("_UVRect", uv);
                    }

                    _meshRenderer.material = _instancedMaterial;
                }
            }
            else
            {
                // BUG FIX ("3D visual is not being set" when a config has no
                // sprite/atlas, or configId hasn't resolved to a registered
                // config yet): this branch used to be
                // `else if (_fallbackMaterial != null) sharedMaterial = _fallbackMaterial;`
                // with NO final else — if the prefab had no _fallbackMaterial
                // assigned, the renderer just kept whatever material it already
                // had (often none, or an error-shader magenta), so an unset
                // config visually read as "nothing happened", whereas 2D's
                // GetFallbackSprite() always renders *something* even when
                // wrong. This mirrors that: fall back to a lazily-generated
                // shared default material so 3D always shows something too.
                _meshRenderer.sharedMaterial = _fallbackMaterial != null
                    ? _fallbackMaterial
                    : GetDefaultFallbackMaterial();
            }

            _meshRenderer.enabled           = true;
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows     = false;
        }

        private void ApplyScale(ProjectileConfigSO cfg)
        {
            float length = cfg != null ? cfg.FullSizeX * _scaleMultiplier : _scaleMultiplier;
            float width  = cfg != null && cfg.FullSizeX > 0.001f
                ? (cfg.FullSizeY / cfg.FullSizeX) * length : length * 0.2f;
            transform.localScale = new Vector3(width, width, length);
        }

        /// <summary>
        /// GROWTH FIX companion — reproduces ApplyScale's own length/width/
        /// aspect-ratio formula above, but driven by the growth coroutine's
        /// interpolated (sizeX, sizeY) instead of always reading cfg's full
        /// FullSizeX/FullSizeY directly. Because both sizeX and sizeY are
        /// scaled down by the SAME growth fraction at any given moment
        /// (ProjectileVisualBase.GrowScaleRoutine), their ratio sizeY/sizeX
        /// always equals cfg.FullSizeY/cfg.FullSizeX exactly — so this
        /// produces IDENTICAL output to ApplyScale(cfg) once sizeX/sizeY
        /// reach the config's full values, and scales smoothly in between.
        /// </summary>
        protected override void ApplyScaleAtSize(float sizeX, float sizeY)
        {
            float length = sizeX * _scaleMultiplier;
            float width  = sizeX > 0.001f ? (sizeY / sizeX) * length : length * 0.2f;
            transform.localScale = new Vector3(width, width, length);
        }

        private void ApplyTrail(ProjectileConfigSO cfg)
        {
            if (_trailRenderer == null) return;

            if (cfg == null || !cfg.HasTrail)
            {
                _trailRenderer.enabled  = false;
                _trailRenderer.emitting = false;
                _trailConfigured = false;
                return;
            }

            if (!_trailConfigured)
            {
                if (cfg.TrailMaterial != null)
                {
                    if (cfg.UseSharedTrailMaterial)
                        _trailRenderer.sharedMaterial = cfg.TrailMaterial;
                    else
                        _trailRenderer.material = cfg.TrailMaterial;
                }

                if (cfg.UseGradientOverride && cfg.TrailGradient != null)
                    _trailRenderer.colorGradient = cfg.TrailGradient;

                _trailRenderer.time              = cfg.TrailTime;
                _trailRenderer.startWidth        = cfg.TrailStartWidth;
                _trailRenderer.endWidth          = cfg.TrailEndWidth;
                _trailRenderer.numCapVertices    = cfg.TrailCapVertices;
                _trailRenderer.minVertexDistance = cfg.TrailMinVertexDistance;
                _trailRenderer.alignment         = LineAlignment.View;

                _trailRenderer.shadowCastingMode         = UnityEngine.Rendering.ShadowCastingMode.Off;
                _trailRenderer.receiveShadows             = false;
                _trailRenderer.generateLightingData       = false;
                _trailRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

                _trailConfigured = true;
            }

            _trailRenderer.Clear();
            _trailRenderer.enabled  = true;
            _trailRenderer.emitting = true;
        }

        #endregion

        #region Default Capsule Mesh / Default Fallback Material

#if UNITY_EDITOR
        // See ProjectileVisual_2D for the full explanation. Same pattern: release
        // the native object right before the domain that cached it goes away.
        static ProjectileVisual_3D()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseStaticNativeCaches;
        }

        private static void ReleaseStaticNativeCaches()
        {
            if (_defaultCapsuleMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(_defaultCapsuleMesh);
                _defaultCapsuleMesh = null;
            }
            if (_defaultFallbackMaterial != null)
            {
                UnityEngine.Object.DestroyImmediate(_defaultFallbackMaterial);
                _defaultFallbackMaterial = null;
            }
        }
#endif

        private static Mesh GetDefaultCapsule()
        {
            if (_defaultCapsuleMesh != null && _defaultCapsuleMesh.vertexCount > 0)
                return _defaultCapsuleMesh;

            int   sides   = 6;
            float radius  = 0.08f;
            float halfLen = 0.5f;

            var verts = new System.Collections.Generic.List<Vector3>();
            var uvs   = new System.Collections.Generic.List<Vector2>();
            var tris  = new System.Collections.Generic.List<int>();

            for (int i = 0; i < sides; i++)
            {
                float a = i / (float)sides * Mathf.PI * 2f;
                float x = Mathf.Cos(a) * radius;
                float y = Mathf.Sin(a) * radius;
                verts.Add(new Vector3(x, y,  halfLen)); uvs.Add(new Vector2(i/(float)sides, 1f));
                verts.Add(new Vector3(x, y, -halfLen)); uvs.Add(new Vector2(i/(float)sides, 0f));
            }
            int tipIdx  = verts.Count; verts.Add(new Vector3(0, 0,  halfLen)); uvs.Add(new Vector2(0.5f, 1f));
            int tailIdx = verts.Count; verts.Add(new Vector3(0, 0, -halfLen)); uvs.Add(new Vector2(0.5f, 0f));

            for (int i = 0; i < sides; i++)
            {
                int next  = (i + 1) % sides;
                int a0 = i * 2, a1 = i * 2 + 1;
                int b0 = next * 2, b1 = next * 2 + 1;
                tris.AddRange(new[]{ a0, b0, a1, b0, b1, a1 });
            }
            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                tris.AddRange(new[]{ tipIdx, i * 2, next * 2 });
            }
            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;
                tris.AddRange(new[]{ tailIdx, next * 2 + 1, i * 2 + 1 });
            }

            _defaultCapsuleMesh = new Mesh { name = "DefaultProjectile3D_Capsule" };
            _defaultCapsuleMesh.SetVertices(verts);
            _defaultCapsuleMesh.SetUVs(0, uvs);
            _defaultCapsuleMesh.SetTriangles(tris, 0);
            _defaultCapsuleMesh.RecalculateNormals();
            _defaultCapsuleMesh.RecalculateBounds();
            return _defaultCapsuleMesh;
        }

        /// <summary>
        /// Shared, lazily-created fallback material used when a config has no
        /// sprite/atlas AND no per-prefab _fallbackMaterial is assigned. Mirrors
        /// ProjectileVisual_2D.GetFallbackSprite() — same "always show something"
        /// intent, just for the 3D mesh path. Tries URP Lit first, then Built-in
        /// Standard, then Sprites/Default as a last resort so this never silently
        /// no-ops on a render pipeline this project doesn't happen to use.
        /// </summary>
        private static Material GetDefaultFallbackMaterial()
        {
            if (_defaultFallbackMaterial != null) return _defaultFallbackMaterial;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Sprites/Default");
            if (shader == null) return null;

            _defaultFallbackMaterial = new Material(shader) { name = "DefaultProjectile3D_Fallback" };
            return _defaultFallbackMaterial;
        }

        #endregion
    }
}
