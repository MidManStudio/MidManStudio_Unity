// ProjectileRenderer3D.cs
//
// FIX (UseSprite = false / custom shape):
//   Removed !cfg.UseSprite skip so non-sprite projectiles still render.
//   Two-pass draw: sprite configs use their sprite texture; non-sprite configs
//   use Texture2D.whiteTexture with full UV (0,0,1,1) so the shape is visible.
//
// Previous fixes retained:
//   World-space vertices via Graphics.DrawMesh with Matrix4x4.identity (no shake).
//   Billboard orientation: elongated quad along travel direction.
//   MPB.SetTexture per draw call.

using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;
using UnityEngine;

namespace MidManStudio.Projectiles.Visuals
{
    public sealed class ProjectileRenderer3D : MonoBehaviour
    {
        #region Inspector

        [Header("Rendering")]
        [SerializeField] private Material _atlasMaterial;

        [Tooltip("Maximum 3D projectiles rendered per frame.")]
        [SerializeField] private int _maxQuads = 512;

        [Header("Camera (for billboard orientation)")]
        [Tooltip("Camera used to compute perpendicular billboard axis.\n" +
                 "Falls back to Camera.main when not assigned.")]
        [SerializeField] private Camera _renderCamera;

        [Header("Fade")]
        [SerializeField, Range(0f, 0.3f)] private float _fadeInFraction  = 0.10f;
        [SerializeField, Range(0f, 0.3f)] private float _fadeOutFraction = 0.15f;

        #endregion

        #region Mesh State

        private Mesh      _mesh;
        private Vector3[] _verts;
        private Vector2[] _uvs;
        private Color32[] _cols;
        private int[]     _tris;

        private MaterialPropertyBlock _mpb;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            _mesh = new Mesh { name = "ProjectileCombined3D" };
            _mesh.MarkDynamic();

            _mpb   = new MaterialPropertyBlock();
            _verts = new Vector3[_maxQuads * 4];
            _uvs   = new Vector2[_maxQuads * 4];
            _cols  = new Color32[_maxQuads * 4];
            _tris  = new int[_maxQuads * 6];
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Build and submit the combined world-space mesh for all alive 3D projectiles.
        /// Call from LateUpdate every display frame.
        /// Two passes: sprite configs then non-sprite configs, to keep textures separate.
        /// </summary>
        public void Render(NativeProjectile3D[] projs, int count)
        {
            if (_atlasMaterial == null || projs == null || count == 0)
            {
                _mesh.Clear();
                return;
            }

            // Pass 1: sprite projectiles
            RenderPass(projs, count, spritePass: true);
            // Pass 2: non-sprite / custom-shape projectiles
            RenderPass(projs, count, spritePass: false);
        }

        #endregion

        #region Render Pass

        private void RenderPass(NativeProjectile3D[] projs, int count, bool spritePass)
        {
            Camera    cam      = _renderCamera != null ? _renderCamera : Camera.main;
            var       reg      = ProjectileRegistry.Instance;
            int       qi       = 0;
            Texture2D firstTex = spritePass ? null : Texture2D.whiteTexture;

            for (int i = 0; i < count && qi < _maxQuads; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = reg.Get(p.ConfigId);
                if (cfg == null) continue;   // FIX: removed !cfg.UseSprite skip

                // Route to correct pass
                bool hasSprite = cfg.UseSprite && cfg.ProjectileSprite?.texture != null;
                if (spritePass  && !hasSprite) continue;
                if (!spritePass &&  hasSprite) continue;

                // Track first sprite texture for sprite pass
                if (spritePass && firstTex == null)
                    firstTex = cfg.ProjectileSprite.texture;

                Vector3 vel = new Vector3(p.Vx, p.Vy, p.Vz);
                Vector3 pos = new Vector3(p.X,  p.Y,  p.Z);

                Vector3 forward, perpAxis;

                if (vel.sqrMagnitude > 0.0001f)
                {
                    forward = vel.normalized;

                    Vector3 camToProj = cam != null
                        ? (pos - cam.transform.position)
                        : Vector3.back;
                    camToProj = camToProj.sqrMagnitude > 0.0001f
                        ? camToProj.normalized : Vector3.back;

                    perpAxis = Vector3.Cross(forward, camToProj);

                    if (perpAxis.sqrMagnitude < 0.001f)
                        perpAxis = Vector3.Cross(forward, Vector3.up);
                    if (perpAxis.sqrMagnitude < 0.001f)
                        perpAxis = Vector3.Cross(forward, Vector3.right);

                    perpAxis = perpAxis.normalized;
                }
                else
                {
                    forward  = Vector3.forward;
                    perpAxis = Vector3.right;
                }

                float hx = p.ScaleX * 0.5f;
                float hy = cfg.FullSizeY * 0.5f;

                int vBase = qi * 4;

                _verts[vBase + 0] = pos - forward * hx - perpAxis * hy;
                _verts[vBase + 1] = pos + forward * hx - perpAxis * hy;
                _verts[vBase + 2] = pos + forward * hx + perpAxis * hy;
                _verts[vBase + 3] = pos - forward * hx + perpAxis * hy;

                // FIX: UV rect — sprite pass uses atlas rect; non-sprite uses full (0,0,1,1)
                Vector4 uv = hasSprite
                    ? reg.GetUVRect(p.ConfigId)
                    : new Vector4(0f, 0f, 1f, 1f);

                _uvs[vBase + 0] = new Vector2(uv.x,        uv.y);
                _uvs[vBase + 1] = new Vector2(uv.x + uv.z, uv.y);
                _uvs[vBase + 2] = new Vector2(uv.x + uv.z, uv.y + uv.w);
                _uvs[vBase + 3] = new Vector2(uv.x,        uv.y + uv.w);

                Color32 col = ComputeTint(p.Lifetime, p.MaxLifetime,
                    _fadeInFraction, _fadeOutFraction);
                _cols[vBase + 0] = col;
                _cols[vBase + 1] = col;
                _cols[vBase + 2] = col;
                _cols[vBase + 3] = col;

                int tBase = qi * 6;
                _tris[tBase + 0] = vBase;
                _tris[tBase + 1] = vBase + 1;
                _tris[tBase + 2] = vBase + 2;
                _tris[tBase + 3] = vBase;
                _tris[tBase + 4] = vBase + 2;
                _tris[tBase + 5] = vBase + 3;

                qi++;
            }

            if (qi == 0) return;

            _mesh.Clear();
            _mesh.SetVertices(_verts, 0, qi * 4);
            _mesh.SetUVs(0, _uvs,    0, qi * 4);
            _mesh.SetColors(_cols,    0, qi * 4);
            _mesh.SetTriangles(_tris, 0, qi * 6, 0);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);

            // Always set a valid texture — never leave it unset
            _mpb.SetTexture("_MainTex", firstTex ?? Texture2D.whiteTexture);

            Graphics.DrawMesh(
                _mesh,
                Matrix4x4.identity,
                _atlasMaterial,
                gameObject.layer,
                camera: null,
                submeshIndex: 0,
                properties: _mpb,
                castShadows: false,
                receiveShadows: false);
        }

        #endregion

        #region Helpers

        private static Color32 ComputeTint(
            float lifetime, float maxLifetime,
            float fadeInFrac, float fadeOutFrac)
        {
            if (maxLifetime <= 0f) return new Color32(255, 255, 255, 255);

            float progress     = 1f - lifetime / maxLifetime;
            float fadeOutStart = 1f - fadeOutFrac;

            float alpha = 1f;
            if (progress < fadeInFrac && fadeInFrac > 0f)
                alpha = progress / fadeInFrac;
            else if (progress > fadeOutStart && fadeOutFrac > 0f)
                alpha = 1f - (progress - fadeOutStart) / fadeOutFrac;

            byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);
            return new Color32(255, 255, 255, a);
        }

        #endregion
    }
}
