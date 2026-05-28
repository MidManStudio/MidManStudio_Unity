// ProjectileVisual_.cs  — 2D pool visual
//
// FIX: ApplySpriteOptimised no longer disables the SpriteRenderer when the
//   config has no sprite (UseSprite=false or ProjectileSprite not assigned).
//   Instead a 1×1 white sprite is generated at runtime and used as a fallback,
//   so raycast and client-prediction pool visuals are always visible.
//   The _fallbackSprite is static and created once, shared across all instances.
//
// FIX: Correct 2D rotation via atan2 → Z-Euler (not LookRotation).
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

        #endregion

        #region State

        private Sprite _cachedSprite;
        private bool   _trailConfigured;
        private ushort _cachedConfigId;
        private bool   _configInitialised;

        // Shared across all instances — created once, never destroyed
        private static Sprite _fallbackSprite;

        #endregion

        #region Fallback Sprite

        /// <summary>
        /// Returns a 1×1 white sprite used when the config has no sprite assigned.
        /// Ensures the SpriteRenderer is always visible so raycast / pool visuals
        /// can be seen even for configs with UseSprite = false.
        /// </summary>
        private static Sprite GetFallbackSprite()
        {
            if (_fallbackSprite != null) return _fallbackSprite;

            // Build a 4×4 solid-white texture
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp
            };

            Color32[] pixels = new Color32[16];
            for (int i = 0; i < 16; i++) pixels[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            // PPU = 4 → sprite is 1 world-unit wide/tall at default scale
            _fallbackSprite = Sprite.Create(
                tex,
                new Rect(0, 0, 4, 4),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit: 4f);
            _fallbackSprite.name = "FallbackProjectileSprite";
            return _fallbackSprite;
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

            ApplySpriteOptimised(cfg?.ProjectileSprite);
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

            if (projectileTrailRend != null)
            {
                projectileTrailRend.emitting = false;
                projectileTrailRend.enabled  = false;
                projectileTrailRend.Clear();
            }
        }

        public override void HideProjectile()
        {
            if (projectileSpriteRend != null) projectileSpriteRend.enabled = false;
            if (projectileTrailRend  != null) projectileTrailRend.emitting = false;
        }

        #endregion

        #region Visual Setup

        private void ApplySpriteOptimised(Sprite sprite)
        {
            if (projectileSpriteRend == null) return;

            // FIX: always enable — use fallback white sprite when none assigned
            projectileSpriteRend.enabled      = true;
            projectileSpriteRend.sortingOrder  = _spriteSortingOrder;

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
