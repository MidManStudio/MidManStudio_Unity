using UnityEngine;
using MidManStudio.InGame.ProjectileConfigs;
using MidManStudio.Core.HelperFunctions;
using System;

namespace MidManStudio.InGame.ProjectileSystem
{
    /// <summary>
    /// Optimized trail renderer handler for pooled projectile visuals
    /// CRITICAL: Only updates material/properties when they actually change
    /// Prevents redundant calculations and material assignments for pooled objects
    /// </summary>
    [RequireComponent(typeof(TrailRenderer))]
    public class ProjectileTrailOptimizer : MonoBehaviour
    {
        #region Cached Components
        private TrailRenderer _trailRenderer;
        private MaterialPropertyBlock _propertyBlock;
        #endregion

        #region Cached State - Prevents Redundant Updates
        private struct TrailState
        {
            // Material tracking
            public Material cachedMaterial;
            public int cachedMaterialInstanceId;

            // Gradient tracking
            public Gradient cachedGradient;
            public int cachedGradientHash;

            // Width tracking
            public float cachedStartWidth;
            public float cachedEndWidth;

            // Performance settings tracking
            public float cachedMinVertexDistance;
            public float cachedTime;

            // Configuration tracking
            public bool cachedGenerateLighting;
            public bool cachedReceiveShadows;

            // Flags
            public bool isInitialized;
            public bool useSharedMaterial;

            public void Clear()
            {
                cachedMaterial = null;
                cachedMaterialInstanceId = 0;
                cachedGradient = null;
                cachedGradientHash = 0;
                cachedStartWidth = 0;
                cachedEndWidth = 0;
                cachedMinVertexDistance = 0;
                cachedTime = 0;
                cachedGenerateLighting = false;
                cachedReceiveShadows = false;
                isInitialized = false;
                useSharedMaterial = false;
            }
        }

        private TrailState _currentState;
        #endregion

        #region Configuration
        [Header("Optimization Settings")]
        [SerializeField] private bool _enableLogs = false;
        [SerializeField] private bool _enablePerformanceTracking = false;

        [Header("Performance Stats")]
        [SerializeField] private int _materialChanges = 0;
        [SerializeField] private int _gradientChanges = 0;
        [SerializeField] private int _widthChanges = 0;
        [SerializeField] private int _skippedUpdates = 0;
        #endregion

        #region Initialization

        private void Awake()
        {
            _trailRenderer = GetComponent<TrailRenderer>();

            if (_trailRenderer == null)
            {
                RunLogError("TrailRenderer component not found!", nameof(Awake));
                enabled = false;
                return;
            }

            // Initialize property block for per-instance properties
            _propertyBlock = new MaterialPropertyBlock();

            _currentState = new TrailState();

            RunLog("ProjectileTrailOptimizer initialized", nameof(Awake));
        }

        /// <summary>
        /// Reset trail state when returning to pool
        /// CRITICAL: Call this before returning object to pool
        /// </summary>
        public void ResetForPool()
        {
            if (_trailRenderer != null)
            {
                _trailRenderer.Clear();
                _trailRenderer.enabled = true; // Re-enable for next use
            }

            _currentState.Clear();

            // Reset performance counters
            if (_enablePerformanceTracking)
            {
                RunLog($"Pool Reset - Material Changes: {_materialChanges}, " +
                       $"Gradient Changes: {_gradientChanges}, " +
                       $"Width Changes: {_widthChanges}, " +
                       $"Skipped Updates: {_skippedUpdates}",
                       nameof(ResetForPool));

                _materialChanges = 0;
                _gradientChanges = 0;
                _widthChanges = 0;
                _skippedUpdates = 0;
            }
        }

        #endregion

        #region Public API - Apply Trail Configuration

        /// <summary>
        /// Apply trail configuration with intelligent change detection
        /// ONLY updates properties that have actually changed
        /// </summary>
        public void ApplyTrailConfiguration(ProjectileConfigScriptableObject config)
        {
            if (config == null)
            {
                RunLogWarning("Null config provided - disabling trail", nameof(ApplyTrailConfiguration));
                DisableTrail();
                return;
            }

            // Check if we should use shared material
            bool useSharedMaterial = config.get_UseSharedTrailMaterial && config.get_SharedTrailMaterial != null;

            // Apply configuration based on material strategy
            if (useSharedMaterial)
            {
                ApplySharedMaterialConfiguration(config);
            }
            else
            {
                ApplyInstancedMaterialConfiguration(config);
            }

            // Apply performance settings (always check for changes)
            ApplyPerformanceSettings(config);
        }

        /// <summary>
        /// Apply configuration using shared material (GPU instancing)
        /// BEST PERFORMANCE - used when multiple projectiles use same material
        /// </summary>
        private void ApplySharedMaterialConfiguration(ProjectileConfigScriptableObject config)
        {
            Material sharedMaterial = config.get_SharedTrailMaterial;

            // OPTIMIZATION: Only change material if different
            if (!IsSameMaterial(sharedMaterial, true))
            {
                _trailRenderer.sharedMaterial = sharedMaterial;
                _currentState.cachedMaterial = sharedMaterial;
                _currentState.cachedMaterialInstanceId = sharedMaterial.GetInstanceID();
                _currentState.useSharedMaterial = true;

                if (_enablePerformanceTracking) _materialChanges++;
                RunLog($"Applied shared material: {sharedMaterial.name}", nameof(ApplySharedMaterialConfiguration));
            }
            else
            {
                if (_enablePerformanceTracking) _skippedUpdates++;
                RunLog("Shared material unchanged - skipped", nameof(ApplySharedMaterialConfiguration));
            }

            // Apply gradient override if enabled
            if (config.get_UseGradientOverride)
            {
                ApplyGradient(config.get_TrailGradient);
            }
            else
            {
                // Use material's default color (no gradient change needed)
                if (_enablePerformanceTracking) _skippedUpdates++;
            }

            // Apply width settings
            ApplyWidth(config.get_TrailMat?.GetFloat("_StartWidth") ?? 0.2f,
                      config.get_TrailMat?.GetFloat("_EndWidth") ?? 0.05f);
        }

        /// <summary>
        /// Apply configuration using per-projectile material instance
        /// FALLBACK - used when projectile has unique material settings
        /// </summary>
        private void ApplyInstancedMaterialConfiguration(ProjectileConfigScriptableObject config)
        {
            Material instancedMaterial = config.get_TrailMat;

            if (instancedMaterial == null)
            {
                RunLogWarning("No trail material configured - disabling trail", nameof(ApplyInstancedMaterialConfiguration));
                DisableTrail();
                return;
            }

            // OPTIMIZATION: Only change material if different
            if (!IsSameMaterial(instancedMaterial, false))
            {
                _trailRenderer.material = instancedMaterial;
                _currentState.cachedMaterial = instancedMaterial;
                _currentState.cachedMaterialInstanceId = instancedMaterial.GetInstanceID();
                _currentState.useSharedMaterial = false;

                if (_enablePerformanceTracking) _materialChanges++;
                RunLog($"Applied instanced material: {instancedMaterial.name}", nameof(ApplyInstancedMaterialConfiguration));
            }
            else
            {
                if (_enablePerformanceTracking) _skippedUpdates++;
                RunLog("Instanced material unchanged - skipped", nameof(ApplyInstancedMaterialConfiguration));
            }

            // Apply gradient
            ApplyGradient(config.get_TrailGradient);

            // Width settings will be applied by ApplyPerformanceSettings
        }

        #endregion

        #region Material Change Detection

        /// <summary>
        /// CRITICAL: Check if material is actually different before applying
        /// Prevents unnecessary material changes on pooled objects
        /// </summary>
        private bool IsSameMaterial(Material newMaterial, bool isShared)
        {
            if (newMaterial == null) return false;
            if (_currentState.cachedMaterial == null) return false;

            // Check if it's the exact same material instance
            if (_currentState.cachedMaterialInstanceId == newMaterial.GetInstanceID())
            {
                // Also verify shared/instanced mode matches
                return _currentState.useSharedMaterial == isShared;
            }

            return false;
        }

        #endregion

        #region Gradient Management

        /// <summary>
        /// Apply gradient with change detection
        /// ONLY updates if gradient actually changed
        /// </summary>
        private void ApplyGradient(Gradient newGradient)
        {
            if (newGradient == null)
            {
                RunLogWarning("Null gradient provided - skipping", nameof(ApplyGradient));
                return;
            }

            // OPTIMIZATION: Check if gradient actually changed
            int newGradientHash = ComputeGradientHash(newGradient);

            if (_currentState.cachedGradientHash == newGradientHash && _currentState.cachedGradient != null)
            {
                if (_enablePerformanceTracking) _skippedUpdates++;
                RunLog("Gradient unchanged - skipped", nameof(ApplyGradient));
                return;
            }

            // Apply new gradient
            _trailRenderer.colorGradient = newGradient;
            _currentState.cachedGradient = newGradient;
            _currentState.cachedGradientHash = newGradientHash;

            if (_enablePerformanceTracking) _gradientChanges++;
            RunLog("Applied new gradient", nameof(ApplyGradient));
        }

        /// <summary>
        /// Compute hash for gradient to detect changes
        /// Uses color keys and alpha keys for comparison
        /// </summary>
        private int ComputeGradientHash(Gradient gradient)
        {
            if (gradient == null) return 0;

            unchecked
            {
                int hash = 17;

                // Hash color keys
                var colorKeys = gradient.colorKeys;
                for (int i = 0; i < colorKeys.Length; i++)
                {
                    hash = hash * 31 + colorKeys[i].color.GetHashCode();
                    hash = hash * 31 + colorKeys[i].time.GetHashCode();
                }

                // Hash alpha keys
                var alphaKeys = gradient.alphaKeys;
                for (int i = 0; i < alphaKeys.Length; i++)
                {
                    hash = hash * 31 + alphaKeys[i].alpha.GetHashCode();
                    hash = hash * 31 + alphaKeys[i].time.GetHashCode();
                }

                return hash;
            }
        }

        #endregion

        #region Width Management

        /// <summary>
        /// Apply width settings with change detection
        /// </summary>
        private void ApplyWidth(float startWidth, float endWidth)
        {
            bool widthChanged = false;

            // OPTIMIZATION: Check start width
            if (!Mathf.Approximately(_currentState.cachedStartWidth, startWidth))
            {
                _trailRenderer.startWidth = startWidth;
                _currentState.cachedStartWidth = startWidth;
                widthChanged = true;
            }

            // OPTIMIZATION: Check end width
            if (!Mathf.Approximately(_currentState.cachedEndWidth, endWidth))
            {
                _trailRenderer.endWidth = endWidth;
                _currentState.cachedEndWidth = endWidth;
                widthChanged = true;
            }

            if (widthChanged)
            {
                if (_enablePerformanceTracking) _widthChanges++;
                RunLog($"Applied width: {startWidth} -> {endWidth}", nameof(ApplyWidth));
            }
            else
            {
                if (_enablePerformanceTracking) _skippedUpdates++;
                RunLog("Width unchanged - skipped", nameof(ApplyWidth));
            }
        }

        #endregion

        #region Performance Settings

        /// <summary>
        /// Apply critical performance settings
        /// These settings are ALWAYS applied regardless of material type
        /// </summary>
        private void ApplyPerformanceSettings(ProjectileConfigScriptableObject config)
        {
            bool settingsChanged = false;

            // Min vertex distance (affects vertex generation)
            float targetMinVertexDistance = GetOptimalMinVertexDistance(config);
            if (!Mathf.Approximately(_currentState.cachedMinVertexDistance, targetMinVertexDistance))
            {
                _trailRenderer.minVertexDistance = targetMinVertexDistance;
                _currentState.cachedMinVertexDistance = targetMinVertexDistance;
                settingsChanged = true;
            }

            // Trail time (affects trail length)
            float targetTime = GetOptimalTrailTime(config);
            if (!Mathf.Approximately(_currentState.cachedTime, targetTime))
            {
                _trailRenderer.time = targetTime;
                _currentState.cachedTime = targetTime;
                settingsChanged = true;
            }

            // CRITICAL: Lighting settings (huge performance impact)
            if (_currentState.cachedGenerateLighting != false)
            {
                _trailRenderer.generateLightingData = false;
                _currentState.cachedGenerateLighting = false;
                settingsChanged = true;
            }

            if (_currentState.cachedReceiveShadows != false)
            {
                _trailRenderer.receiveShadows = false;
                _currentState.cachedReceiveShadows = false;
                settingsChanged = true;
            }

            // Additional performance-critical settings (set once)
            if (!_currentState.isInitialized)
            {
                _trailRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _trailRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                _trailRenderer.alignment = LineAlignment.View;
                _trailRenderer.textureMode = LineTextureMode.Stretch;
                _trailRenderer.numCornerVertices = 0;
                _trailRenderer.numCapVertices = 0;

                _currentState.isInitialized = true;
                settingsChanged = true;
            }

            if (settingsChanged)
            {
                RunLog("Applied performance settings", nameof(ApplyPerformanceSettings));
            }
            else
            {
                if (_enablePerformanceTracking) _skippedUpdates++;
            }
        }

        /// <summary>
        /// Get optimal min vertex distance based on projectile speed
        /// Fast projectiles = higher distance (fewer vertices)
        /// Slow projectiles = lower distance (smoother trail)
        /// </summary>
        private float GetOptimalMinVertexDistance(ProjectileConfigScriptableObject config)
        {
            float speed = config.get_MaxSpeed;

            if (speed > 50f)
                return 0.5f;  // Very fast - reduce vertices significantly
            else if (speed > 30f)
                return 0.3f;  // Fast - moderate reduction
            else if (speed > 15f)
                return 0.2f;  // Medium - balanced
            else
                return 0.15f; // Slow - smooth trail
        }

        /// <summary>
        /// Get optimal trail time based on projectile type
        /// Explosive projectiles = longer trails
        /// Basic projectiles = shorter trails
        /// </summary>
        private float GetOptimalTrailTime(ProjectileConfigScriptableObject config)
        {
            switch (config.get_ProjectileClass)
            {
                case ProjectileConfigScriptableObject.ProjectileClass.basicLengendary:
                case ProjectileConfigScriptableObject.ProjectileClass.basicRunic:
                    return 0.4f; // Legendary - longer trail

                case ProjectileConfigScriptableObject.ProjectileClass.exploder:
                    return 0.35f; // Rocket - medium-long trail

                default:
                    return 0.25f; // Basic - standard trail
            }
        }

        #endregion

        #region Trail Control

        /// <summary>
        /// Disable trail renderer
        /// </summary>
        public void DisableTrail()
        {
            if (_trailRenderer != null)
            {
                _trailRenderer.enabled = false;
            }

            _currentState.Clear();

            RunLog("Trail disabled", nameof(DisableTrail));
        }

        /// <summary>
        /// Enable trail renderer
        /// </summary>
        public void EnableTrail()
        {
            if (_trailRenderer != null)
            {
                _trailRenderer.enabled = true;
            }

            RunLog("Trail enabled", nameof(EnableTrail));
        }

        /// <summary>
        /// Clear trail immediately (for teleporting projectiles)
        /// </summary>
        public void ClearTrail()
        {
            if (_trailRenderer != null)
            {
                _trailRenderer.Clear();
            }
        }

        #endregion

        #region Manual Property Updates (Advanced)

        /// <summary>
        /// Manually update trail width (bypasses change detection)
        /// Use for dynamic width changes during flight
        /// </summary>
        public void SetWidthImmediate(float startWidth, float endWidth)
        {
            if (_trailRenderer == null) return;

            _trailRenderer.startWidth = startWidth;
            _trailRenderer.endWidth = endWidth;
            _currentState.cachedStartWidth = startWidth;
            _currentState.cachedEndWidth = endWidth;

            if (_enablePerformanceTracking) _widthChanges++;
        }

        /// <summary>
        /// Manually update trail gradient (bypasses change detection)
        /// Use for dynamic color changes during flight
        /// </summary>
        public void SetGradientImmediate(Gradient gradient)
        {
            if (_trailRenderer == null || gradient == null) return;

            _trailRenderer.colorGradient = gradient;
            _currentState.cachedGradient = gradient;
            _currentState.cachedGradientHash = ComputeGradientHash(gradient);

            if (_enablePerformanceTracking) _gradientChanges++;
        }

        #endregion

        #region Performance Statistics

        /// <summary>
        /// Get performance statistics for debugging
        /// </summary>
        public string GetPerformanceStats()
        {
            return $"Trail Optimizer Stats:\n" +
                   $"Material Changes: {_materialChanges}\n" +
                   $"Gradient Changes: {_gradientChanges}\n" +
                   $"Width Changes: {_widthChanges}\n" +
                   $"Skipped Updates: {_skippedUpdates}\n" +
                   $"Current Material: {(_currentState.cachedMaterial != null ? _currentState.cachedMaterial.name : "None")}\n" +
                   $"Using Shared: {_currentState.useSharedMaterial}";
        }

        /// <summary>
        /// Reset performance counters
        /// </summary>
        [ContextMenu("Reset Performance Counters")]
        public void ResetPerformanceCounters()
        {
            _materialChanges = 0;
            _gradientChanges = 0;
            _widthChanges = 0;
            _skippedUpdates = 0;

            RunLog("Performance counters reset", nameof(ResetPerformanceCounters));
        }

        #endregion

        #region Debug Helpers

        [ContextMenu("Log Current State")]
        private void LogCurrentState()
        {
            Debug.Log($"=== Trail Optimizer State ===\n" +
                     $"Initialized: {_currentState.isInitialized}\n" +
                     $"Material: {(_currentState.cachedMaterial != null ? _currentState.cachedMaterial.name : "None")}\n" +
                     $"Shared Material: {_currentState.useSharedMaterial}\n" +
                     $"Start Width: {_currentState.cachedStartWidth}\n" +
                     $"End Width: {_currentState.cachedEndWidth}\n" +
                     $"Min Vertex Distance: {_currentState.cachedMinVertexDistance}\n" +
                     $"Trail Time: {_currentState.cachedTime}\n" +
                     $"Generate Lighting: {_currentState.cachedGenerateLighting}\n" +
                     $"Receive Shadows: {_currentState.cachedReceiveShadows}\n" +
                     GetPerformanceStats());
        }

        [ContextMenu("Force Clear Cache")]
        private void ForceClearCache()
        {
            _currentState.Clear();
            ResetPerformanceCounters();
            RunLog("Cache forcibly cleared", nameof(ForceClearCache));
        }

        #endregion

        #region Logging

        private void RunLog(string message, string methodName = "")
        {
            if (!_enableLogs) return;
            MID_HelperFunctions.LogDebug(message, nameof(ProjectileTrailOptimizer), methodName);
        }

        private void RunLogWarning(string message, string methodName = "")
        {
            if (!_enableLogs) return;
            MID_HelperFunctions.LogWarning(message, nameof(ProjectileTrailOptimizer), methodName);
        }

        private void RunLogError(string message, string methodName = "", Exception e = null)
        {
            MID_HelperFunctions.LogError(message, nameof(ProjectileTrailOptimizer), methodName, e);
        }

        #endregion
    }
}