// ProjectileRenderer3D.cs
//
// FIX (orientation): Previous code built the quad's right/up axes from
//   LookRotation(vel), making the quad perpendicular to travel — you would
//   see the END of the bullet (a dot/square) rather than the side (a streak).
//
//   New approach: billboard elongated along the travel direction.
//     • Long axis  = velocity direction   (FullSizeX / ScaleX)
//     • Short axis = Cross(vel, camToProj) (FullSizeY, camera-facing billboard)
//   Result: tracer/streak effect visible from all camera angles.
//
// FIX (texture): first alive projectile's sprite texture is set via
//   MeshRenderer.SetPropertyBlock(_mpb) so the MeshRenderer uses the right
//   _MainTex rather than the material's static default.
//
// USAGE NOTE: assign _renderCamera in the inspector for dedicated render
//   cameras (splitscreen, etc.).  Falls back to Camera.main.

using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;
using UnityEngine;

namespace MidManStudio.Projectiles.Visuals
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class ProjectileRenderer3D : MonoBehaviour
    {
        #region Inspector

        [Header("Rendering")]
        [SerializeField] private Material _atlasMaterial;

        [Tooltip("Maximum 3D projectiles rendered per frame (each = 4 verts + 6 indices).")]
        [SerializeField] private int _maxQuads = 512;

        [Header("Camera (for billboard orientation)")]
        [Tooltip("Camera used to compute perpendicular billboard axis.\n" +
                 "Falls back to Camera.main when not assigned.")]
        [SerializeField] private Camera _renderCamera;

        [Header("Fade")]
        [Tooltip("Fraction of lifetime over which alpha fades IN at spawn.")]
        [SerializeField, Range(0f, 0.3f)] private float _fadeInFraction  = 0.10f;

        [Tooltip("Fraction of lifetime over which alpha fades OUT before expiry.")]
        [SerializeField, Range(0f, 0.3f)] private float _fadeOutFraction = 0.15f;

        #endregion

        #region Mesh State

        private Mesh      _mesh;
        private Vector3[] _verts;
        private Vector2[] _uvs;
        private Color32[] _cols;
        private int[]     _tris;

        private MeshFilter            _filter;
        private MeshRenderer          _rend;
        private MaterialPropertyBlock _mpb;

        #endregion

        #region Lifecycle

        private void Awake()
        {
            _filter = GetComponent<MeshFilter>();
            _rend   = GetComponent<MeshRenderer>();

            _mesh = new Mesh { name = "ProjectileCombined3D" };
            _mesh.MarkDynamic();
            _filter.mesh = _mesh;

            if (_atlasMaterial != null)
                _rend.sharedMaterial = _atlasMaterial;

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
        /// Build and submit the combined mesh for all alive 3D projectiles.
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

                // Capture first texture found for this frame's batch
                if (firstTex == null && cfg.ProjectileSprite?.texture != null)
                    firstTex = cfg.ProjectileSprite.texture;

                Vector3 vel = new Vector3(p.Vx, p.Vy, p.Vz);
                Vector3 pos = new Vector3(p.X,  p.Y,  p.Z);

                // ── Billboard elongated along travel direction ─────────────────
                // forward  = travel direction (the long axis of the bullet quad)
                // perpAxis = cross(forward, camToProj) → perpendicular axis that
                //            faces the camera; makes the quad a camera-facing ribbon

                Vector3 forward, perpAxis;

                if (vel.sqrMagnitude > 0.0001f)
                {
                    forward = vel.normalized;

                    Vector3 camToProj = cam != null
                        ? (pos - cam.transform.position).normalized
                        : Vector3.back;

                    perpAxis = Vector3.Cross(forward, camToProj);

                    // Fallbacks when forward is parallel to view direction
                    // (shooting directly toward or away from camera)
                    if (perpAxis.sqrMagnitude < 0.001f)
                        perpAxis = Vector3.Cross(forward, Vector3.up);
                    if (perpAxis.sqrMagnitude < 0.001f)
                        perpAxis = Vector3.Cross(forward, Vector3.right);

                    perpAxis = perpAxis.normalized;
                }
                else
                {
                    // Stationary / just spawned — use world axes
                    forward  = Vector3.forward;
                    perpAxis = Vector3.right;
                }

                float hx = p.ScaleX * 0.5f;       // half-length along travel
                float hy = cfg.FullSizeY * 0.5f;   // half-width perpendicular

                int vBase = qi * 4;

                // Quad elongated along forward, thin along perpAxis:
                //   0 = tail-side1   1 = tip-side1
                //   3 = tail-side2   2 = tip-side2
                _verts[vBase + 0] = pos - forward * hx - perpAxis * hy;
                _verts[vBase + 1] = pos + forward * hx - perpAxis * hy;
                _verts[vBase + 2] = pos + forward * hx + perpAxis * hy;
                _verts[vBase + 3] = pos - forward * hx + perpAxis * hy;

                // Atlas UVs — U=0 at tail, U=1 at tip; sprite long axis = travel
                Vector4 uv = reg.GetUVRect(p.ConfigId);
                _uvs[vBase + 0] = new Vector2(uv.x,        uv.y);
                _uvs[vBase + 1] = new Vector2(uv.x + uv.z, uv.y);
                _uvs[vBase + 2] = new Vector2(uv.x + uv.z, uv.y + uv.w);
                _uvs[vBase + 3] = new Vector2(uv.x,        uv.y + uv.w);

                // Lifetime fade
                Color32 col = ComputeTint(p.Lifetime, p.MaxLifetime,
                    _fadeInFraction, _fadeOutFraction);
                _cols[vBase + 0] = col;
                _cols[vBase + 1] = col;
                _cols[vBase + 2] = col;
                _cols[vBase + 3] = col;

                // CCW winding when viewed from perpAxis cross forward direction
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
            _mesh.SetUVs(0,    _uvs,  0, qi * 4);
            _mesh.SetColors(_cols,    0, qi * 4);
            _mesh.SetTriangles(_tris, 0, qi * 6, 0);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);

            // Apply texture via property block so we don't create a material instance
            if (firstTex != null)
            {
                _mpb.SetTexture("_MainTex", firstTex);
                _rend.SetPropertyBlock(_mpb);
            }
        }

        #endregion

        #region Helpers

        private static Color32 ComputeTint(
            float lifetime, float maxLifetime,
            float fadeInFrac, float fadeOutFrac)
        {
            if (maxLifetime <= 0f) return new Color32(255, 255, 255, 255);

            float progress     = 1f - lifetime / maxLifetime; // 0=just spawned, 1=dying
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
