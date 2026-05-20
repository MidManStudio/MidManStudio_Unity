// ProjectileVisual_.cs
// Client-side cosmetic projectile visual.
// Spawned from LocalObjectPool (utilities). No NetworkBehaviour.
// Trail is configured directly from ProjectileConfigSO package properties.
//
// FIX: ApplyTrailOptimised had the condition
//        if (!_trailConfigured || _cachedConfigId != _cachedConfigId)
//      The second operand is always false (comparing a field to itself), so trail
//      settings were never re-applied on a pool-recycled object with a different
//      config.  Changed to simply:
//        if (!_trailConfigured)
//      _trailConfigured is reset to false in CleanupForPoolReturn(), so every
//      freshly-acquired pool object re-applies the full trail config from the SO.

using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Visuals
{
    /// <summary>
    /// Pooled cosmetic projectile visual. Works offline and as a client-side
    /// prediction visual. No game-specific config or enum references.
    /// </summary>
    public class ProjectileVisual_ : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Renderers")]
        [SerializeField] public SpriteRenderer   projectileSpriteRend;
        [SerializeField] public TrailRenderer    projectileTrailRend;

        [Header("Pool Return")]
        [SerializeField] private LocalPoolReturn localPoolReturn;

        [Header("Debug")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        #endregion

        #region Cached State

        private ProjectileConfigSO _config;

        private Sprite _cachedSprite;
        private Color  _cachedSpriteColor = Color.white;
        private bool   _trailConfigured;
        private ushort _cachedConfigId;
        private bool   _initialised;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (localPoolReturn == null)
                localPoolReturn = GetComponent<LocalPoolReturn>();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Initialise for client-side display from a registered config ID.
        /// Called by ClientPredictionManager and RaycastProjectileHandler.
        /// </summary>
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
                _trailConfigured = false;   // force trail re-apply whenever config changes
            }

            if (_config == null)
            {
                MID_Logger.LogWarning(_logLevel,
                    $"ProjectileVisual_: no config registered for id={configId}.",
                    nameof(ProjectileVisual_));
            }

            transform.position = origin;
            if (direction.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(Vector3.forward, direction);

            ApplySpriteOptimised(_config?.ProjectileSprite);
            ApplyTrailOptimised(_config);

            _initialised = true;

            MID_Logger.LogDebug(_logLevel,
                $"Initialised configId={configId} origin={origin}",
                nameof(ProjectileVisual_));
        }

        /// <summary>Immediately return to LocalObjectPool and reset all state.</summary>
        public void ReturnToPoolImmediate()
        {
            if (this == null) return;
            CleanupForPoolReturn();

            if (localPoolReturn != null)
                localPoolReturn.ReturnToPoolNow();
        }

        /// <summary>Hide visuals without returning to pool (e.g. on server-confirmed hit).</summary>
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

            // FIX: was "_trailConfigured || _cachedConfigId != _cachedConfigId"
            //      — second operand was always false so recycled objects never
            //        got trail settings re-applied.  Now we simply check the flag
            //        which is reset to false in CleanupForPoolReturn() and also
            //        whenever the configId changes (see InitializeClientVisual).
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
            _trailConfigured = false;   // ensure trail re-applies on next use
            _cachedSprite    = null;
            _cachedSpriteColor = Color.white;

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
