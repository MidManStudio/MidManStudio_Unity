// ProjectileRenderer3D.cs
//
// FIX (shaky / wrong-looking 3D visuals):
//   Previous implementation used MeshFilter + MeshRenderer on this GameObject.
//   The mesh vertices were in WORLD SPACE, but Unity applies the GameObject's
//   transform ON TOP of the mesh — so any non-zero position/rotation on the
//   hosting object caused all projectiles to be double-offset and shake as
//   the object moved.
//
//   Fix: switched to Graphics.DrawMesh with Matrix4x4.identity — identical to
//   ProjectileRenderer2D's approach. World-space vertices are submitted directly
//   with no additional transform applied.
//
// FIX (orientation):
//   Previous code: LookRotation(vel) perpendicular to velocity made the quad
//   show its EDGE to the camera — you saw a thin sliver, not the bullet face.
//   New approach: elongated billboard along travel direction, thin axis toward
//   camera. The quad's long axis = travel direction, short axis = cross(travel,
//   camToProj). This gives the classic tracer/streak appearance from all angles.
//
// FIX (texture):
//   MPB.SetTexture("_MainTex") applied per DrawMesh call — correct sprite atlas
//   texture is used instead of the material's static default.

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

        // Property block for per-draw-call texture override
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
        /// </summary>
        public void Render(NativeProjectile3D[] projs, int count)
        {
            if (_atlasMaterial == null || projs == null || count == 0)
            {
                _mesh.Clear();
                return;
            }

            Camera    cam      = _renderCamera != null ? _renderCamera : Camera.main;
            var       reg      = ProjectileRegistry.Instance;
            int       qi       = 0;
            Texture2D firstTex = null;

            for (int i = 0; i < count && qi < _maxQuads; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = reg.Get(p.ConfigId);
                if (cfg == null || !cfg.UseSprite) continue;

                if (firstTex == null && cfg.ProjectileSprite?.texture != null)
                    firstTex = cfg.ProjectileSprite.texture;

                Vector3 vel = new Vector3(p.Vx, p.Vy, p.Vz);
                Vector3 pos = new Vector3(p.X,  p.Y,  p.Z);

                // ── Billboard elongated along travel direction ─────────────────
                // forward  = travel direction  (long axis of the bullet streak)
                // perpAxis = cross(forward, camToProj)  →  camera-facing thin axis
                Vector3 forward, perpAxis;

                if (vel.sqrMagnitude > 0.0001f)
                {
                    forward = vel.normalized;

                    // Direction from camera to this projectile (not normalized — we normalize after cross)
                    Vector3 camToProj = cam != null
                        ? (pos - cam.transform.position)
                        : Vector3.back;
                    camToProj = camToProj.sqrMagnitude > 0.0001f ? camToProj.normalized : Vector3.back;

                    perpAxis = Vector3.Cross(forward, camToProj);

                    // Fallbacks when forward ≈ camToProj (shooting straight at/away from camera)
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

                float hx = p.ScaleX * 0.5f;       // half-length along travel
                float hy = cfg.FullSizeY * 0.5f;   // half-width perpendicular to travel

                int vBase = qi * 4;

                // Quad: elongated along forward, thin along perpAxis
                //   0 = tail-sideA   1 = tip-sideA
                //   3 = tail-sideB   2 = tip-sideB
                _verts[vBase + 0] = pos - forward * hx - perpAxis * hy;
                _verts[vBase + 1] = pos + forward * hx - perpAxis * hy;
                _verts[vBase + 2] = pos + forward * hx + perpAxis * hy;
                _verts[vBase + 3] = pos - forward * hx + perpAxis * hy;

                // Atlas UVs
                Vector4 uv = reg.GetUVRect(p.ConfigId);
                _uvs[vBase + 0] = new Vector2(uv.x,        uv.y);
                _uvs[vBase + 1] = new Vector2(uv.x + uv.z, uv.y);
                _uvs[vBase + 2] = new Vector2(uv.x + uv.z, uv.y + uv.w);
                _uvs[vBase + 3] = new Vector2(uv.x,        uv.y + uv.w);

                // Lifetime fade
                Color32 col = ComputeTint(p.Lifetime, p.MaxLifetime, _fadeInFraction, _fadeOutFraction);
                _cols[vBase + 0] = col;
                _cols[vBase + 1] = col;
                _cols[vBase + 2] = col;
                _cols[vBase + 3] = col;

                // CCW winding
                int tBase = qi * 6;
                _tris[tBase + 0] = vBase;
                _tris[tBase + 1] = vBase + 1;
                _tris[tBase + 2] = vBase + 2;
                _tris[tBase + 3] = vBase;
                _tris[tBase + 4] = vBase + 2;
                _tris[tBase + 5] = vBase + 3;

                qi++;
            }

            _mesh.Clear();
            if (qi == 0) return;

            _mesh.SetVertices(_verts, 0, qi * 4);
            _mesh.SetUVs(0, _uvs,    0, qi * 4);
            _mesh.SetColors(_cols,    0, qi * 4);
            _mesh.SetTriangles(_tris, 0, qi * 6, 0);

            // Expanded bounds so the mesh is never frustum-culled at camera edges
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);

            // Apply sprite texture via MPB — avoids creating a material instance
            if (firstTex != null)
                _mpb.SetTexture("_MainTex", firstTex);

            // FIX: Graphics.DrawMesh with Matrix4x4.identity — vertices are already
            // in world space. No secondary transform applied, no shaking.
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
