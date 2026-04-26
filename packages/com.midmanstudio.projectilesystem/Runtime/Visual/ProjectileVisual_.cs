using UnityEngine;
using MidManStudio.InGame.GameItemData;
using MidManStudio.InGame.ProjectileConfigs;
using MidManStudio.InGame.Managers;
using MidManStudio.Core.PoolSystems;

namespace MidManStudio.InGame.ProjectileSystem
{
    /// <summary>
    /// OPTIMIZED: Simplified projectile visual - NO NetworkBehaviour
    /// Pure client-side rendering for ServerProjectileManager
    /// - Sprite change detection (only updates if different)
    /// - Integrated ProjectileTrailOptimizer
    /// - Minimal overhead, maximum performance
    /// </summary>
    public class ProjectileVisual_ : MonoBehaviour
    {
        #region Serialized Fields
        [Header("Configuration")]
        [SerializeField] private ProjectileConfigScriptableObject m_ProjectileConfig;
        [SerializeField] private LocalPoolReturn localPoolReturn;

        [Header("Renderers")]
        public SpriteRenderer projectileSpriteRend;
        public TrailRenderer projectileTrailRend;
        #endregion

        #region Optimized Components
        private ProjectileTrailOptimizer _trailOptimizer;
        private MaterialPropertyBlock _propertyBlock;
        #endregion

        #region Cached State - Prevents Redundant Updates
        private Sprite _currentSprite;
        private Color _currentSpriteColor = Color.white;
        private bool hasBeenInitialized = false;
        #endregion

        #region Initialization

        /// <summary>
        /// Initialize visual for client-side display (no network manager needed!)
        /// OPTIMIZED: Minimal setup, maximum efficiency
        /// </summary>
        public void InitializeClientVisual(
            MID_AllProjectileNames projectileName,
            Vector2 origin,
            Vector2 direction,
            float speed)
        {
            if (localPoolReturn == null) localPoolReturn = GetComponent<LocalPoolReturn>();

            // Get config
            GetProjectileConfig(projectileName);

            // Set transform
            transform.position = origin;
            transform.rotation = Quaternion.LookRotation(Vector3.forward, direction);

            // Apply visuals
            ApplyProjectileVisuals();

            hasBeenInitialized = true;
        }

        /// <summary>
        /// Get projectile configuration from manager
        /// </summary>
        private void GetProjectileConfig(MID_AllProjectileNames projectileName)
        {
            m_ProjectileConfig = ProjectileConfigManager.GetProjectileConfig(projectileName);

            if (m_ProjectileConfig == null)
            {
                Debug.LogWarning($"[ProjectileVisual_] Config not found for: {projectileName}");
                m_ProjectileConfig = GetFallbackConfig();
            }
        }

        /// <summary>
        /// OPTIMIZED: Apply visuals only if they changed
        /// </summary>
        private void ApplyProjectileVisuals()
        {
            if (m_ProjectileConfig != null)
            {
                // OPTIMIZED: Only set sprite if different
                Sprite newSprite = m_ProjectileConfig.get_ProjectileVisual;
                SetSpriteOptimized(newSprite);

                // OPTIMIZED: Use trail optimizer
                SetupTrailOptimized();
            }
            else
            {
                Debug.LogError("[ProjectileVisual_] Projectile config missing!");
            }
        }

        /// <summary>
        /// OPTIMIZED: Setup trail using ProjectileTrailOptimizer
        /// Zero redundant material/gradient assignments
        /// </summary>
        private void SetupTrailOptimized()
        {
            if (projectileTrailRend == null) return;

            // Get or add trail optimizer component
            if (_trailOptimizer == null)
            {
                _trailOptimizer = GetComponent<ProjectileTrailOptimizer>();
                if (_trailOptimizer == null)
                {
                    _trailOptimizer = gameObject.AddComponent<ProjectileTrailOptimizer>();
                }
            }

            // Apply configuration through optimizer (handles change detection)
            _trailOptimizer.ApplyTrailConfiguration(m_ProjectileConfig);
        }

        /// <summary>
        /// OPTIMIZED: Only set sprite if it's different from current
        /// CRITICAL: Prevents redundant sprite assignments when pulling from pool
        /// </summary>
        private void SetSpriteOptimized(Sprite sprite)
        {
            if (sprite == null)
            {
                Debug.LogWarning("[ProjectileVisual_] Null sprite provided");
                return;
            }

            // OPTIMIZATION: Check if sprite actually changed
            if (_currentSprite == sprite)
            {
                // Same sprite - skip assignment
                return;
            }

            // Different sprite - update
            if (_propertyBlock == null)
            {
                _propertyBlock = new MaterialPropertyBlock();
            }

            projectileSpriteRend.sprite = sprite;
            _currentSprite = sprite;

            // Optional: Apply material property block for per-instance properties
            // projectileSpriteRend.SetPropertyBlock(_propertyBlock);
        }

        /// <summary>
        /// OPTIMIZED: Only set sprite color if different
        /// </summary>
        private void SetSpriteColorOptimized(Color color)
        {
            if (_currentSpriteColor == color)
            {
                return; // Same color - skip
            }

            projectileSpriteRend.color = color;
            _currentSpriteColor = color;
        }

        /// <summary>
        /// Get fallback config if primary lookup fails
        /// </summary>
        private ProjectileConfigScriptableObject GetFallbackConfig()
        {
            var allNames = ProjectileConfigManager.GetAllProjectileNames();
            foreach (var name in allNames)
            {
                var config = ProjectileConfigManager.GetProjectileConfig(name);
                if (config != null)
                {
                    return config;
                }
            }

            return null;
        }

        #endregion

        #region Pooling

        /// <summary>
        /// Hide projectile visuals
        /// </summary>
        public void HideProjectile()
        {
            if (this == null) return;
            projectileSpriteRend.enabled = false;
            projectileTrailRend.enabled = false;
        }

        /// <summary>
        /// OPTIMIZED: Return to pool with complete state cleanup
        /// </summary>
        public void ReturnToPoolImmediate()
        {
            if (this == null) return;

            CleanupForPoolReturn();

            if (localPoolReturn != null)
            {
                localPoolReturn.ManualReturn();
            }
        }

        /// <summary>
        /// OPTIMIZED: Complete cleanup for pool return
        /// Resets ALL cached state to prevent leaks between uses
        /// </summary>
        private void CleanupForPoolReturn()
        {
            // Reset state flags
            hasBeenInitialized = false;
            m_ProjectileConfig = null;

            // Reset cached sprite state
            _currentSprite = null;
            _currentSpriteColor = Color.white;

            // Reset sprite renderer
            if (projectileSpriteRend != null)
            {
                projectileSpriteRend.enabled = true;
                projectileSpriteRend.sprite = null;
                projectileSpriteRend.color = Color.white;
            }

            // CRITICAL: Reset trail optimizer (clears cached state)
            if (_trailOptimizer != null)
            {
                _trailOptimizer.ResetForPool();
            }
            else if (projectileTrailRend != null)
            {
                // Fallback if optimizer not present
                projectileTrailRend.enabled = true;
                projectileTrailRend.Clear();
            }
        }

        #endregion

        #region Debug Helpers

        [ContextMenu("Log Visual State")]
        private void LogVisualState()
        {
            Debug.Log($"=== ProjectileVisual_ State ===\n" +
                     $"Initialized: {hasBeenInitialized}\n" +
                     $"Current Sprite: {(_currentSprite != null ? _currentSprite.name : "None")}\n" +
                     $"Current Color: {_currentSpriteColor}\n" +
                     $"Config: {(m_ProjectileConfig != null ? m_ProjectileConfig.name : "None")}\n" +
                     $"Trail Optimizer: {(_trailOptimizer != null ? "Present" : "Missing")}");
        }

        #endregion
    }
}