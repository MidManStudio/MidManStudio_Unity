using UnityEngine;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Visuals
{
    public class ProjectileVisual_2D : ProjectileVisualBase
    {
        #region Inspector

        [Header("2D Renderers")]
        [SerializeField] public SpriteRenderer projectileSpriteRend;
        [SerializeField] public TrailRenderer  projectileTrailRend;

        [Header("Draw Order")]
        [SerializeField] private int _spriteSortingOrder = 1;
        [SerializeField] private int _trailSortingOrder  = 0;

        [Header("Shape Mesh (auto-created at runtime when needed)")]
        [Tooltip("MeshFilter for CustomShape configs. Auto-found then created if missing.")]
        [SerializeField] private MeshFilter   _shapeMeshFilter;
        [Tooltip("MeshRenderer for CustomShape configs. Auto-found then created if missing.")]
        [SerializeField] private MeshRenderer _shapeMeshRenderer;
        [Tooltip("Sorting order for the shape MeshRenderer.")]
        [SerializeField] private int _shapeSortingOrder = 1;
        [Tooltip("Material for shape mesh rendering.\n" +
                 "Assign InstancedProjectile.shader material for correct atlas UV support.\n" +
                 "If null, falls back to Sprites/Default (no atlas UV remapping).")]
        [SerializeField] private Material _fallbackShapeMaterial;

        #endregion

        #region State

        private Sprite _cachedSprite;
        private bool   _trailConfigured;
        private ushort _cachedConfigId;
        private bool   _configInitialised;
        private bool   _usingShapeMesh;

        private MaterialPropertyBlock _shapeMpb;

        // Shared across all instances — created once, lazily, on first use.
        // FIX (native leak on domain reload): this used to be "created once, never
        // destroyed". The Texture2D + Sprite are native engine objects. Unity's
        // domain reload (assembly recompile, or Enter Play Mode with "Reload
        // Domain" enabled — the default) wipes the *managed* static field back to
        // null, but it does NOT destroy the underlying native object, because that
        // requires an explicit Object.Destroy/DestroyImmediate call that nothing
        // was making. The orphaned native allocations are exactly what Unity's
        // post-reload leak detector reports as
        // "Leak Detected : Persistent allocates 8 individual allocations."
        // Fix: explicitly release both native objects right before each domain
        // reload via AssemblyReloadEvents (editor-only — builds never reload the
        // domain at runtime, so there is nothing to leak there).
        private static Sprite    _fallbackSprite;
        private static Texture2D _fallbackTexture;

        #endregion

        #region Fallback Sprite

        private static Sprite GetFallbackSprite()
        {
            if (_fallbackSprite != null) return _fallbackSprite;

            _fallbackTexture = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp,
                name       = "FallbackProjectileTexture"
            };
            Color32[] pixels = new Color32[16];
            for (int i = 0; i < 16; i++) pixels[i] = new Color32(255, 255, 255, 255);
            _fallbackTexture.SetPixels32(pixels);
            _fallbackTexture.Apply(false, true);

            _fallbackSprite = Sprite.Create(
                _fallbackTexture,
                new Rect(0, 0, 4, 4),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit: 4f);
            _fallbackSprite.name = "FallbackProjectileSprite";
            return _fallbackSprite;
        }

#if UNITY_EDITOR
        // Registered once per domain load. Fires right before the NEXT domain
        // reload, so whatever was cached during this session gets explicitly
        // destroyed instead of orphaned. The static fields themselves are reset
        // to null for free when the new domain loads — only the native side
        // needs the explicit call.
        static ProjectileVisual_2D()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseStaticNativeCaches;
        }

        private static void ReleaseStaticNativeCaches()
        {
            if (_fallbackSprite != null)
            {
                UnityEngine.Object.DestroyImmediate(_fallbackSprite);
                _fallbackSprite = null;
            }
            if (_fallbackTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(_fallbackTexture);
                _fallbackTexture = null;
            }
        }
#endif

        #endregion

        #region Unity Lifecycle

        protected override void Awake()
        {
            base.Awake();

            // Try to find pre-existing components — don't create yet (may never be needed)
            if (_shapeMeshFilter   == null) _shapeMeshFilter   = GetComponent<MeshFilter>();
            if (_shapeMeshRenderer == null) _shapeMeshRenderer = GetComponent<MeshRenderer>();

            // Disable if found — sprite is the default visual
            if (_shapeMeshRenderer != null) _shapeMeshRenderer.enabled = false;
        }

        #endregion

        #region ProjectileVisualBase

        protected override void ApplyRotation(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return;
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, 0f, angle);
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

            // Resolve shape first
            bool hasCustomShape = cfg != null && cfg.CustomShape != null;
            Mesh shapeMesh      = hasCustomShape ? cfg.CustomShape.GetMesh() : null;
            bool needsShapeMesh = hasCustomShape && shapeMesh != null && shapeMesh.vertexCount > 0;

            // FIX: Ensure components exist at runtime (client pool prefabs don't pre-add them)
            if (needsShapeMesh) EnsureShapeMeshComponents();

            bool canUseShape = needsShapeMesh
                            && _shapeMeshFilter   != null
                            && _shapeMeshRenderer != null;

            if (canUseShape)
            {
                ApplyShapeMeshOptimised(cfg, shapeMesh);
                if (projectileSpriteRend != null) projectileSpriteRend.enabled = false;
                _usingShapeMesh = true;
            }
            else
            {
                // Disable shape renderer — it may have been created on a previous pool cycle
                if (_shapeMeshRenderer != null) _shapeMeshRenderer.enabled = false;
                _usingShapeMesh = false;
                ApplySpriteOptimised(cfg?.ProjectileSprite);
            }

            ApplyTrailOptimised(cfg);
        }

        protected override void OnReturnToPool()
        {
            _configInitialised = false;
            _trailConfigured   = false;
            _cachedSprite      = null;

            if (projectileSpriteRend != null)
            {
                projectileSpriteRend.enabled = true;
                projectileSpriteRend.sprite  = null;
                projectileSpriteRend.color   = Color.white;
            }

            if (_shapeMeshRenderer != null) _shapeMeshRenderer.enabled  = false;
            if (_shapeMeshFilter   != null) _shapeMeshFilter.sharedMesh = null;

            if (_usingShapeMesh)
            {
                transform.localScale = Vector3.one;
                _usingShapeMesh = false;
            }

            if (projectileTrailRend != null)
            {
                projectileTrailRend.emitting = false;
                projectileTrailRend.enabled  = false;
                projectileTrailRend.Clear();
            }
        }

        public override void HideProjectile()
        {
            if (projectileSpriteRend != null) projectileSpriteRend.enabled  = false;
            if (_shapeMeshRenderer   != null) _shapeMeshRenderer.enabled    = false;
            if (projectileTrailRend  != null) projectileTrailRend.emitting  = false;
        }

        #endregion

        #region Shape Mesh

        /// <summary>
        /// Creates MeshFilter and MeshRenderer dynamically if not already present.
        /// This allows pool prefabs to omit these components — they are added the
        /// first time a shape config is used on this pooled instance.
        /// </summary>
        private void EnsureShapeMeshComponents()
        {
            if (_shapeMeshFilter == null)
                _shapeMeshFilter = GetComponent<MeshFilter>() ?? gameObject.AddComponent<MeshFilter>();

            if (_shapeMeshRenderer == null)
            {
                _shapeMeshRenderer = GetComponent<MeshRenderer>() ?? gameObject.AddComponent<MeshRenderer>();

                // Assign material — prefer inspector-assigned, then Sprites/Default
                if (_shapeMeshRenderer.sharedMaterial == null)
                {
                    if (_fallbackShapeMaterial != null)
                    {
                        _shapeMeshRenderer.sharedMaterial = _fallbackShapeMaterial;
                    }
                    else
                    {
                        // Sprites/Default is always available (Built-in and URP)
                        var shader = Shader.Find("Sprites/Default");
                        if (shader == null) shader = Shader.Find("Unlit/Transparent");
                        if (shader != null)
                            _shapeMeshRenderer.sharedMaterial = new Material(shader)
                                { name = "DynamicShapeFallback" };
                    }
                }

                _shapeMeshRenderer.enabled = false;
            }
        }

        /// <summary>
        /// Applies the CustomShape mesh to MeshFilter/MeshRenderer.
        /// Scale matches ProjectileRenderer2D: (FullSizeX, FullSizeY, 1).
        /// </summary>
        private void ApplyShapeMeshOptimised(ProjectileConfigSO cfg, Mesh mesh)
        {
            _shapeMeshFilter.sharedMesh = mesh;

            // Scale to world size — matches what ProjectileRenderer2D computes for the instanced path
            transform.localScale = new Vector3(cfg.FullSizeX, cfg.FullSizeY, 1f);

            // Apply sprite texture via MPB — avoids material instance allocation
            if (cfg.ProjectileSprite?.texture != null)
            {
                if (_shapeMpb == null) _shapeMpb = new MaterialPropertyBlock();
                _shapeMpb.SetTexture("_MainTex", cfg.ProjectileSprite.texture);
                _shapeMeshRenderer.SetPropertyBlock(_shapeMpb);
            }

            _shapeMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _shapeMeshRenderer.receiveShadows     = false;
            _shapeMeshRenderer.sortingOrder       = _shapeSortingOrder;
            _shapeMeshRenderer.enabled            = true;
        }

        #endregion

        #region Sprite

        private void ApplySpriteOptimised(Sprite sprite)
        {
            if (projectileSpriteRend == null) return;

            projectileSpriteRend.enabled     = true;
            projectileSpriteRend.sortingOrder = _spriteSortingOrder;

            Sprite toUse = sprite != null ? sprite : GetFallbackSprite();
            if (_cachedSprite != toUse)
            {
                projectileSpriteRend.sprite = toUse;
                _cachedSprite = toUse;
            }
        }

        #endregion

        #region Trail

        private void ApplyTrailOptimised(ProjectileConfigSO cfg)
        {
            if (projectileTrailRend == null) return;

            if (cfg == null || !cfg.HasTrail)
            {
                projectileTrailRend.enabled  = false;
                projectileTrailRend.emitting = false;
                _trailConfigured = false;
                return;
            }

            if (!_trailConfigured)
            {
                if (cfg.TrailMaterial != null)
                {
                    if (cfg.UseSharedTrailMaterial)
                        projectileTrailRend.sharedMaterial = cfg.TrailMaterial;
                    else
                        projectileTrailRend.material = cfg.TrailMaterial;
                }

                if (cfg.UseGradientOverride && cfg.TrailGradient != null)
                    projectileTrailRend.colorGradient = cfg.TrailGradient;

                projectileTrailRend.time              = cfg.TrailTime;
                projectileTrailRend.startWidth        = cfg.TrailStartWidth;
                projectileTrailRend.endWidth          = cfg.TrailEndWidth;
                projectileTrailRend.numCapVertices    = cfg.TrailCapVertices;
                projectileTrailRend.minVertexDistance = cfg.TrailMinVertexDistance;

                projectileTrailRend.shadowCastingMode         = UnityEngine.Rendering.ShadowCastingMode.Off;
                projectileTrailRend.receiveShadows             = false;
                projectileTrailRend.generateLightingData       = false;
                projectileTrailRend.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                projectileTrailRend.alignment                  = LineAlignment.View;
                projectileTrailRend.sortingOrder               = _trailSortingOrder;

                _trailConfigured = true;
            }

            projectileTrailRend.Clear();
            projectileTrailRend.enabled  = true;
            projectileTrailRend.emitting = true;
        }

        #endregion
    }
}
