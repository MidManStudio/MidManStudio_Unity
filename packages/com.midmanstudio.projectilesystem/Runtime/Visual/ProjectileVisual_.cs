// ProjectileVisual_.cs  — 2D pool visual
//
// FIX (Shape not applied on client): Added optional MeshFilter + MeshRenderer
//   support. When a config has CustomShape set, the shape mesh is applied to the
//   MeshRenderer (if present) and SpriteRenderer is hidden. On pool return both
//   are reset. This matches the shape rendering done by ProjectileRenderer2D on
//   the host/server.
//   PREFAB SETUP: Optionally add MeshFilter and MeshRenderer components to the
//   prefab (or assign them in the inspector). Assign a compatible material
//   (e.g. Unlit/Transparent or the InstancedProjectile shader) to the
//   MeshRenderer. If no MeshFilter/MeshRenderer is found, shapes fall back to
//   SpriteRenderer only.
//
// FIX: ApplySpriteOptimised always enables the SpriteRenderer, using a
//   generated 1x1 white sprite as fallback when no sprite is assigned.
// FIX: Correct 2D rotation via atan2 → Z-Euler.
// FIX: _trailConfigured flag properly gates re-application on recycled objects.

using UnityEngine;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Visuals
{
    public class ProjectileVisual_ : ProjectileVisualBase
    {
        #region Inspector

        [Header("2D Renderers")]
        [SerializeField] public SpriteRenderer projectileSpriteRend;
        [SerializeField] public TrailRenderer  projectileTrailRend;

        [Header("Draw Order")]
        [SerializeField] private int _spriteSortingOrder = 1;
        [SerializeField] private int _trailSortingOrder  = 0;

        [Header("Shape Mesh (optional — for CustomShape configs)")]
        [Tooltip("MeshFilter to receive the CustomShape mesh. Auto-found on this GO if null.")]
        [SerializeField] private MeshFilter   _shapeMeshFilter;
        [Tooltip("MeshRenderer to draw the CustomShape mesh. Auto-found on this GO if null. " +
                 "Assign a compatible material (Unlit/Transparent or InstancedProjectile shader).")]
        [SerializeField] private MeshRenderer _shapeMeshRenderer;
        [Tooltip("Sorting order for the shape MeshRenderer.")]
        [SerializeField] private int _shapeSortingOrder = 1;

        #endregion

        #region State

        private Sprite _cachedSprite;
        private bool   _trailConfigured;
        private ushort _cachedConfigId;
        private bool   _configInitialised;
        private bool   _usingShapeMesh;

        private MaterialPropertyBlock _shapeMpb;

        // Shared across all instances — created once, never destroyed
        private static Sprite _fallbackSprite;

        #endregion

        #region Fallback Sprite

        /// <summary>
        /// Returns a 1×1 white sprite used when the config has no sprite assigned.
        /// Ensures the SpriteRenderer is always visible for configs with UseSprite=false.
        /// </summary>
        private static Sprite GetFallbackSprite()
        {
            if (_fallbackSprite != null) return _fallbackSprite;

            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp
            };
            Color32[] pixels = new Color32[16];
            for (int i = 0; i < 16; i++) pixels[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            _fallbackSprite = Sprite.Create(
                tex,
                new Rect(0, 0, 4, 4),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit: 4f);
            _fallbackSprite.name = "FallbackProjectileSprite";
            return _fallbackSprite;
        }

        #endregion

        #region Unity Lifecycle

        protected override void Awake()
        {
            base.Awake();

            // Auto-find shape mesh components if not assigned in inspector.
            // If neither is present on the prefab, shape mesh is not supported
            // for this visual — silently falls back to SpriteRenderer.
            if (_shapeMeshFilter   == null) _shapeMeshFilter   = GetComponent<MeshFilter>();
            if (_shapeMeshRenderer == null) _shapeMeshRenderer = GetComponent<MeshRenderer>();

            // Initially disable shape renderer; sprite is the default visual.
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

            // Attempt shape mesh path first if config has a CustomShape.
            bool hasCustomShape = cfg != null && cfg.CustomShape != null;
            Mesh shapeMesh      = hasCustomShape ? cfg.CustomShape.GetMesh() : null;
            bool canUseShape    = hasCustomShape
                               && shapeMesh != null && shapeMesh.vertexCount > 0
                               && _shapeMeshFilter != null && _shapeMeshRenderer != null;

            if (canUseShape)
            {
                ApplyShapeMeshOptimised(cfg, shapeMesh);
                // Hide sprite so both don't render simultaneously.
                if (projectileSpriteRend != null) projectileSpriteRend.enabled = false;
                _usingShapeMesh = true;
            }
            else
            {
                // Disable shape renderer in case it was active from a previous pool cycle.
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

            // Restore sprite renderer to its default enabled state.
            if (projectileSpriteRend != null)
            {
                projectileSpriteRend.enabled = true;
                projectileSpriteRend.sprite  = null;
                projectileSpriteRend.color   = Color.white;
            }

            // Disable shape mesh renderer and release the mesh reference.
            if (_shapeMeshRenderer != null) _shapeMeshRenderer.enabled  = false;
            if (_shapeMeshFilter   != null) _shapeMeshFilter.sharedMesh = null;

            // Reset transform scale in case shape mesh changed it.
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

        #region Visual Setup

        /// <summary>
        /// Apply the CustomShape mesh to the MeshFilter/MeshRenderer.
        ///
        /// Scale is set to (FullSizeX, FullSizeY, 1) to match the Matrix4x4.TRS scale
        /// used by ProjectileRenderer2D: (ScaleX, ScaleX * aspectY, 1) at full size,
        /// which equals (FullSizeX, FullSizeY, 1). Shape mesh vertices are already in
        /// AspectRatio-relative space (e.g. BuildQuad: [-hw,hw] × [-0.5,0.5] where
        /// hw = AspectRatio * 0.5), so this scale correctly maps them to world units.
        /// </summary>
        private void ApplyShapeMeshOptimised(ProjectileConfigSO cfg, Mesh mesh)
        {
            _shapeMeshFilter.sharedMesh = mesh;

            // Scale to match ProjectileRenderer2D: (FullSizeX, FullSizeY, 1)
            transform.localScale = new Vector3(cfg.FullSizeX, cfg.FullSizeY, 1f);

            // Apply sprite texture via MaterialPropertyBlock — no material instance alloc.
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

        private void ApplySpriteOptimised(Sprite sprite)
        {
            if (projectileSpriteRend == null) return;

            // Always enable — use fallback white sprite when none assigned.
            projectileSpriteRend.enabled     = true;
            projectileSpriteRend.sortingOrder = _spriteSortingOrder;

            Sprite toUse = sprite != null ? sprite : GetFallbackSprite();
            if (_cachedSprite != toUse)
            {
                projectileSpriteRend.sprite = toUse;
                _cachedSprite = toUse;
            }
        }

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
