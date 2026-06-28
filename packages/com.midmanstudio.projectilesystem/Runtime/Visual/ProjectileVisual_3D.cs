
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
        [Tooltip("Used when config has no sprite/atlas. Assign a simple Lit or Unlit material.")]
        [SerializeField] private Material _fallbackMaterial;

        #endregion

        #region State

        private static Mesh _defaultCapsuleMesh;
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

            Texture2D tex = cfg?.ProjectileSprite?.texture;
            if (tex != null)
            {
                if (_instancedMaterial == null)
                {
                    var src = _meshRenderer.sharedMaterial ?? _fallbackMaterial;
                    if (src != null) _instancedMaterial = new Material(src);
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

        #region Default Capsule Mesh

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

        #endregion
    }
}
