// ProjectileVisual_.cs
//
// FIX (rotation): InitializeClientVisual now uses angle-based Z rotation for
//   2D visuals (dir.z ≈ 0) instead of LookRotation(forward, dir).
//   LookRotation interprets the second argument as the UP vector, which
//   rotated the sprite 90° incorrectly for horizontal bullets.
//   For 3D visuals (non-zero Z component), LookRotation(dir) is used.
//
// FIX (draw order): SpriteRenderer.sortingOrder = 1, TrailRenderer.sortingOrder = 0.
//   The sprite now renders in front of the trail within the same sorting layer.
//   Adjust these values in the inspector if you have multiple sorting layers.
//
// FIX (trail bug): ApplyTrailOptimised condition was checking
//   "_cachedConfigId != _cachedConfigId" (always false). Fixed to check
//   the _trailConfigured flag which is reset on pool return and config change.

using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Visuals
{
    public class ProjectileVisual_ : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Renderers")]
        [SerializeField] public SpriteRenderer   projectileSpriteRend;
        [SerializeField] public TrailRenderer    projectileTrailRend;

        [Header("Draw Order")]
        [Tooltip("Sorting order for the sprite renderer. Higher = in front.")]
        [SerializeField] private int _spriteSortingOrder = 1;
        [Tooltip("Sorting order for the trail renderer. Should be lower than sprite.")]
        [SerializeField] private int _trailSortingOrder  = 0;

        [Header("Pool Return")]
        [SerializeField] private LocalPoolReturn localPoolReturn;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        #endregion

        #region Cached State

        private ProjectileConfigSO _config;
        private Sprite             _cachedSprite;
        private bool               _trailConfigured;
        private ushort             _cachedConfigId;
        private bool               _initialised;

        #endregion

        private void Awake()
        {
            if (localPoolReturn == null)
                localPoolReturn = GetComponent<LocalPoolReturn>();
        }

        #region Public API

        public void InitializeClientVisual(
            ushort  configId,
            Vector3 origin,
            Vector3 direction,
            float   speed)
        {
            if (localPoolReturn == null)
                localPoolReturn = GetComponent<LocalPoolReturn>();

            bool configChanged = !_initialised || _cachedConfigId != configId;

            if (configChanged)
            {
                _config = ProjectileRegistry.HasInstance
                    ? ProjectileRegistry.Instance.Get(configId)
                    : null;

                _cachedConfigId  = configId;
                _trailConfigured = false;
            }

            if (_config == null)
                MID_Logger.LogWarning(_logLevel,
                    $"ProjectileVisual_: no config for id={configId}.",
                    nameof(ProjectileVisual_));

            transform.position = origin;

            // FIX: correct 2D/3D rotation
            if (direction.sqrMagnitude > 0.001f)
            {
                if (Mathf.Abs(direction.z) < 0.01f)
                {
                    // 2D projectile — rotate around Z axis
                    float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
                    transform.rotation = Quaternion.Euler(0f, 0f, angle);
                }
                else
                {
                    // 3D projectile — align forward to travel direction
                    transform.rotation = Quaternion.LookRotation(direction.normalized);
                }
            }

            ApplySpriteOptimised(_config?.ProjectileSprite);
            ApplyTrailOptimised(_config);

            _initialised = true;

            MID_Logger.LogDebug(_logLevel,
                $"Initialised configId={configId} origin={origin}",
                nameof(ProjectileVisual_));
        }

        public void ReturnToPoolImmediate()
        {
            if (this == null) return;
            CleanupForPoolReturn();
            if (localPoolReturn != null)
                localPoolReturn.ReturnToPoolNow();
        }

        public void HideProjectile()
        {
            if (projectileSpriteRend != null) projectileSpriteRend.enabled = false;
            if (projectileTrailRend  != null) projectileTrailRend.emitting = false;
        }

        #endregion

        #region Visual Setup

        private void ApplySpriteOptimised(Sprite sprite)
        {
            if (projectileSpriteRend == null) return;

            bool shouldShow = sprite != null;
            projectileSpriteRend.enabled = shouldShow;
            // FIX: sprite draws in front of trail
            projectileSpriteRend.sortingOrder = _spriteSortingOrder;

            if (!shouldShow) return;

            if (_cachedSprite != sprite)
            {
                projectileSpriteRend.sprite = sprite;
                _cachedSprite = sprite;
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

            // FIX: _trailConfigured flag correctly gates re-application on recycled objects
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

                projectileTrailRend.time               = cfg.TrailTime;
                projectileTrailRend.startWidth         = cfg.TrailStartWidth;
                projectileTrailRend.endWidth           = cfg.TrailEndWidth;
                projectileTrailRend.numCapVertices     = cfg.TrailCapVertices;
                projectileTrailRend.minVertexDistance  = cfg.TrailMinVertexDistance;

                projectileTrailRend.shadowCastingMode         = UnityEngine.Rendering.ShadowCastingMode.Off;
                projectileTrailRend.receiveShadows             = false;
                projectileTrailRend.generateLightingData       = false;
                projectileTrailRend.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                projectileTrailRend.alignment                  = LineAlignment.View;

                // FIX: trail behind sprite
                projectileTrailRend.sortingOrder = _trailSortingOrder;

                _trailConfigured = true;
            }

            projectileTrailRend.Clear();
            projectileTrailRend.enabled  = true;
            projectileTrailRend.emitting = true;
        }

        #endregion

        #region Pool Cleanup

        private void CleanupForPoolReturn()
        {
            _initialised     = false;
            _config          = null;
            _trailConfigured = false;
            _cachedSprite    = null;

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

            MID_Logger.LogDebug(_logLevel, "Cleaned up for pool return.",
                nameof(ProjectileVisual_));
        }

        #endregion
    }
}
