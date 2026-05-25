// ProjectileVisual3D.cs  — 3D pool visual
//
// Inherits ProjectileVisualBase.
// Handles: MeshRenderer (oriented mesh), optional TrailRenderer.
//
// ORIENTATION:
//   Uses Quaternion.LookRotation(direction) so the mesh forward (+Z) aligns
//   with the travel direction. For bullet/capsule meshes this gives the
//   correct nose-forward appearance.
//
// MESH SELECTION PRIORITY:
//   1. ProjectileConfigSO.CustomShape mesh (if assigned and Is3D)
//   2. ProjectileConfigSO.ProjectileSprite → baked quad (2D fallback for 3D)
//   3. Default unit cube (last resort — replace with your own primitive)
//
// END-USER EXTENSION:
//   Derive from this class and override OnInitialise3D(cfg) to swap in
//   custom materials, particle systems, etc.:
//
//   public class RocketVisual : ProjectileVisual3D
//   {
//       [SerializeField] ParticleSystem _exhaustFlames;
//
//       protected override void OnInitialise3D(ProjectileConfigSO cfg)
//       {
//           base.OnInitialise3D(cfg);
//           _exhaustFlames.Play();
//       }
//
//       protected override void OnReturnToPool()
//       {
//           _exhaustFlames.Stop();
//           base.OnReturnToPool();
//       }
//   }

using UnityEngine;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.Visuals
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class ProjectileVisual3D : ProjectileVisualBase
    {
        #region Inspector

        [Header("3D Renderers")]
        [SerializeField] private MeshFilter   _meshFilter;
        [SerializeField] private MeshRenderer _meshRenderer;

        [Header("Trail (optional)")]
        [SerializeField] private TrailRenderer _trailRenderer;

        [Header("Scale")]
        [Tooltip("Uniform scale multiplier applied on top of the config's FullSizeX.")]
        [SerializeField] private float _scaleMultiplier = 1f;

        [Header("Material Fallback")]
        [Tooltip("Material used when the config has no sprite/atlas. " +
                 "Should be a simple lit or unlit opaque material.")]
        [SerializeField] private Material _fallbackMaterial;

        #endregion

        #region State

        private static Mesh _defaultCubeMesh;   // shared across all instances
        private Material    _instancedMaterial; // per-instance to avoid shared-material side-effects
        private bool        _trailConfigured;
        private ushort      _cachedConfigId;
        private bool        _configInitialised;

        #endregion

        #region Unity Lifecycle

        protected override void Awake()
        {
            base.Awake();
            if (_meshFilter   == null) _meshFilter   = GetComponent<MeshFilter>();
            if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
        }

        private void OnDestroy()
        {
            // Clean up per-instance material to avoid leaks
            if (_instancedMaterial != null)
                Destroy(_instancedMaterial);
        }

        #endregion

        #region ProjectileVisualBase

        protected override void ApplyRotation(Vector3 dir)
        {
            // 3D: align mesh forward (+Z) with travel direction
            if (dir.sqrMagnitude < 0.001f) return;

            Vector3 up = Mathf.Abs(Vector3.Dot(dir.normalized, Vector3.up)) > 0.99f
                ? Vector3.forward
                : Vector3.up;

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
            ApplyTrail(cfg);

            // Sub-class hook
            OnInitialise3D(cfg);
        }

        protected override void OnReturnToPool()
        {
            _configInitialised = false;
            _trailConfigured   = false;

            if (_meshRenderer != null)
                _meshRenderer.enabled = true;

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

        /// <summary>
        /// Called at the end of <see cref="OnInitialise"/> after mesh, material,
        /// scale, and trail have been applied. Override to add game-specific setup.
        /// </summary>
        protected virtual void OnInitialise3D(ProjectileConfigSO cfg) { }

        /// <summary>
        /// Called at the start of <see cref="OnReturnToPool"/> before the
        /// base class resets renderers. Override to stop particles/FX etc.
        /// </summary>
        protected virtual void OnCleanup3D() { }

        #endregion

        #region Visual Setup

        private void ApplyMesh(ProjectileConfigSO cfg)
        {
            if (_meshFilter == null) return;

            Mesh mesh = null;

            // Priority 1: custom shape SO
            if (cfg?.CustomShape != null)
            {
                mesh = cfg.CustomShape.GetMesh();
            }

            // Priority 2: built-in default cube
            if (mesh == null || mesh.vertexCount == 0)
            {
                mesh = GetDefaultCube();
            }

            _meshFilter.sharedMesh = mesh;
        }

        private void ApplyMaterial(ProjectileConfigSO cfg)
        {
            if (_meshRenderer == null) return;

            // Try to use the config sprite's texture
            Texture2D tex = cfg?.ProjectileSprite?.texture;

            if (tex != null)
            {
                // Re-use or create a per-instance material so we don't stomp shared material
                if (_instancedMaterial == null)
                {
                    var srcMat = _meshRenderer.sharedMaterial ?? _fallbackMaterial;
                    if (srcMat != null)
                        _instancedMaterial = new Material(srcMat);
                }

                if (_instancedMaterial != null)
                {
                    _instancedMaterial.mainTexture = tex;
                    _meshRenderer.material = _instancedMaterial;
                }
            }
            else if (_fallbackMaterial != null)
            {
                _meshRenderer.sharedMaterial = _fallbackMaterial;
            }

            _meshRenderer.enabled             = true;
            _meshRenderer.shadowCastingMode   = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows       = false;
        }

        private void ApplyScale(ProjectileConfigSO cfg)
        {
            float size = cfg != null ? cfg.FullSizeX * _scaleMultiplier : _scaleMultiplier;
            float aspectY = (cfg != null && cfg.FullSizeX > 0.001f)
                ? cfg.FullSizeY / cfg.FullSizeX : 1f;

            // x = length (along forward/travel), y = height, z = width
            transform.localScale = new Vector3(size, size * aspectY, size * aspectY);
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

        #region Default Mesh

        private static Mesh GetDefaultCube()
        {
            if (_defaultCubeMesh != null && _defaultCubeMesh.vertexCount > 0)
                return _defaultCubeMesh;

            // Elongated capsule-like box: 2 units long (Z), 0.15 units wide (X/Y)
            // Tip at +Z, tail at -Z
            float hw = 0.075f, hl = 0.5f;

            _defaultCubeMesh = new Mesh { name = "DefaultProjectile3D" };
            _defaultCubeMesh.vertices = new Vector3[]
            {
                // Front (+Z)
                new(-hw, -hw,  hl), new( hw, -hw,  hl),
                new( hw,  hw,  hl), new(-hw,  hw,  hl),
                // Back (-Z)
                new(-hw, -hw, -hl), new( hw, -hw, -hl),
                new( hw,  hw, -hl), new(-hw,  hw, -hl),
            };
            _defaultCubeMesh.uv = new Vector2[]
            {
                new(0,0), new(1,0), new(1,1), new(0,1),
                new(0,0), new(1,0), new(1,1), new(0,1),
            };
            _defaultCubeMesh.triangles = new int[]
            {
                0,2,1, 0,3,2,         // front
                5,7,4, 5,6,7,         // back
                3,6,2, 3,7,6,         // top
                1,5,0, 1,4,5,         // bottom  (note: was 0,4,5 — winding corrected)
                0,7,3, 0,4,7,         // left
                2,6,1, 1,6,5,         // right
            };
            _defaultCubeMesh.RecalculateNormals();
            _defaultCubeMesh.RecalculateBounds();
            return _defaultCubeMesh;
        }

        #endregion
    }
}
