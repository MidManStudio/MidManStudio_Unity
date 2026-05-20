// ProjectileRenderer2D.cs
//
// FIX 1: Added _combinedMeshMpb — a MaterialPropertyBlock initialized with
//         _UVRect = (0,0,1,1) and _Color = (1,1,1,1).  It is passed to
//         Graphics.DrawMesh so the material's serialized default _UVRect
//         (which may be wrong, e.g. (0.24, 0, 4.08, 1.2) in the provided mat)
//         never reaches the shader during the combined-mesh draw call.
//         The combined-mesh path bakes atlas UVs per-vertex, so the shader must
//         receive an identity UV rect; otherwise every sprite shows the wrong
//         region of the atlas (or nothing at all).
//
// FIX 2: Added early-out log when _atlasMaterial is null so the absence of a
//         material assignment is immediately visible in the console.

using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;
using UnityEngine;

namespace MidManStudio.Projectiles.Visuals
{
    [RequireComponent(typeof(ProjectileManager))]
    public class ProjectileRenderer2D : MonoBehaviour
    {
        private enum RenderPath { Instanced, CombinedMesh }

        [Header("Rendering")]
        [SerializeField] private Material _atlasMaterial;

        [Tooltip("Force the combined-mesh draw path even on hardware that supports GPU instancing. " +
                 "Useful if you hit driver bugs or want guaranteed behaviour on all platforms.")]
        [SerializeField] private bool _forceDrawMesh = false;

        // ── Instanced path ────────────────────────────────────────────────────
        private const int   BATCH_SIZE = 1023;
        private Matrix4x4[] _matrices;
        private Vector4[]   _uvRects;
        private Vector4[]   _colors;
        private MaterialPropertyBlock _mpb;

        // ── Combined mesh path ────────────────────────────────────────────────
        private const int MAX_QUADS = 2048;
        private Mesh      _combinedMesh;
        private Vector3[] _verts;
        private Vector2[] _uvs;
        private Color32[] _cols;
        private int[]     _tris;

        // FIX: MPB with identity UV rect so the combined-mesh DrawMesh call
        // is never affected by the material's serialized _UVRect default.
        private MaterialPropertyBlock _combinedMeshMpb;

        private Mesh[] _configMeshCache;
        private RenderPath _path;

        // ─────────────────────────────────────────────────────────────────────

        void Awake()
        {
            bool canInstance = !_forceDrawMesh && SystemInfo.supportsInstancing;
            _path = canInstance ? RenderPath.Instanced : RenderPath.CombinedMesh;

            if (_path == RenderPath.Instanced)
            {
                _matrices = new Matrix4x4[BATCH_SIZE];
                _uvRects  = new Vector4[BATCH_SIZE];
                _colors   = new Vector4[BATCH_SIZE];
                _mpb      = new MaterialPropertyBlock();
            }
            else
            {
                _combinedMesh = new Mesh { name = "ProjectileCombined" };
                _combinedMesh.MarkDynamic();
                _verts = new Vector3[MAX_QUADS * 4];
                _uvs   = new Vector2[MAX_QUADS * 4];
                _cols  = new Color32[MAX_QUADS * 4];
                _tris  = new int[MAX_QUADS * 6];
            }

            // FIX: always create this MPB — used by the combined-mesh DrawMesh to
            // override any wrong _UVRect / _Color defaults on the material.
            _combinedMeshMpb = new MaterialPropertyBlock();
            _combinedMeshMpb.SetVector("_UVRect", new Vector4(0f, 0f, 1f, 1f));
            _combinedMeshMpb.SetVector("_Color",  new Vector4(1f, 1f, 1f, 1f));

            Debug.Log(
                $"[ProjectileRenderer2D] Path={(_path == RenderPath.Instanced ? "Instanced" : "CombinedMesh")}" +
                $" | GPU: {SystemInfo.graphicsDeviceName}" +
                $" | API: {SystemInfo.graphicsDeviceType}" +
                $" | Instancing: {SystemInfo.supportsInstancing}" +
                $" | ForceDrawMesh: {_forceDrawMesh}");
        }

        void OnDestroy()
        {
            if (_combinedMesh != null) Destroy(_combinedMesh);
        }

        // ─── Called by LocalProjectileManager.LateUpdate ─────────────────────

        public void Render(NativeProjectile[] projs, int count)
        {
            if (_atlasMaterial == null)
            {
                // FIX: surface the missing material assignment clearly.
                Debug.LogWarning(
                    "[ProjectileRenderer2D] _atlasMaterial is not assigned. " +
                    "Assign a material using InstancedProjectile_URP.shader (URP) " +
                    "or InstancedProjectile.shader (Built-in) in the inspector.",
                    this);
                return;
            }

            if (_path == RenderPath.Instanced)
                RenderInstanced(projs, count);
            else
                RenderCombined(projs, count);
        }

        // ─── Instanced ────────────────────────────────────────────────────────

        private void RenderInstanced(NativeProjectile[] projs, int count)
        {
            if (count == 0) return;
            var reg = ProjectileRegistry.Instance;
            int batchStart = 0;

            EnsureConfigMeshCache(reg);

            while (batchStart < count)
            {
                int n = 0, end = Mathf.Min(batchStart + BATCH_SIZE, count);

                for (int i = batchStart; i < end; i++)
                {
                    ref var p = ref projs[i];
                    if (p.Alive == 0) continue;

                    var cfg = reg.Get(p.ConfigId);
                    if (cfg == null || !cfg.UseSprite) continue;

                    _matrices[n] = Matrix4x4.TRS(
                        new Vector3(p.X, p.Y, 0f),
                        Quaternion.Euler(0f, 0f, p.AngleDeg),
                        new Vector3(p.ScaleX, p.ScaleY, 1f));

                    _uvRects[n] = reg.GetUVRect(p.ConfigId);
                    _colors[n]  = ComputeTint(ref p);
                    n++;
                }

                if (n > 0)
                {
                    _mpb.SetVectorArray("_UVRect", _uvRects);
                    _mpb.SetVectorArray("_Color",  _colors);

                    Mesh mesh = (_configMeshCache != null
                        && projs[batchStart].ConfigId < _configMeshCache.Length
                        && _configMeshCache[projs[batchStart].ConfigId] != null)
                        ? _configMeshCache[projs[batchStart].ConfigId]
                        : GetOrBuildDefaultQuad();

                    Graphics.DrawMeshInstanced(
                        mesh, 0, _atlasMaterial, _matrices, n, _mpb,
                        UnityEngine.Rendering.ShadowCastingMode.Off,
                        false, gameObject.layer);
                }

                batchStart = end;
            }
        }

        // ─── Combined mesh ────────────────────────────────────────────────────

        private void RenderCombined(NativeProjectile[] projs, int count)
        {
            var reg = ProjectileRegistry.Instance;
            int qi  = 0;

            for (int i = 0; i < count && qi < MAX_QUADS; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = reg.Get(p.ConfigId);
                if (cfg == null || !cfg.UseSprite) continue;

                Vector4 uvRect = reg.GetUVRect(p.ConfigId);
                Vector4 tint   = ComputeTint(ref p);
                var c32 = new Color32(
                    (byte)(tint.x * 255f), (byte)(tint.y * 255f),
                    (byte)(tint.z * 255f), (byte)(tint.w * 255f));

                Mesh srcMesh = cfg.CustomShape != null
                    ? cfg.CustomShape.GetMesh()
                    : GetOrBuildDefaultQuad();

                var srcVerts = srcMesh.vertices;
                var srcUVs   = srcMesh.uv;
                var srcTris  = srcMesh.triangles;
                int vc   = srcVerts.Length;
                int vBase = qi * 4;

                if (vc <= 4)
                {
                    float cos = Mathf.Cos(p.AngleDeg * Mathf.Deg2Rad);
                    float sin = Mathf.Sin(p.AngleDeg * Mathf.Deg2Rad);

                    for (int v = 0; v < vc; v++)
                    {
                        _verts[vBase + v] = RotateScale(
                            p.X, p.Y, srcVerts[v].x * p.ScaleX, srcVerts[v].y * p.ScaleY, cos, sin);
                        // Bake atlas UV directly — the combined-mesh shader path must NOT
                        // apply an additional UV remap (shader receives _UVRect = (0,0,1,1)).
                        _uvs[vBase + v] = new Vector2(
                            uvRect.x + srcUVs[v].x * uvRect.z,
                            uvRect.y + srcUVs[v].y * uvRect.w);
                        _cols[vBase + v] = c32;
                    }
                    // Pad unused vertex slots in the 4-slot stride to avoid stale data.
                    for (int v = vc; v < 4; v++)
                    {
                        _verts[vBase + v] = _verts[vBase];
                        _uvs[vBase + v]   = _uvs[vBase];
                        _cols[vBase + v]  = new Color32(0, 0, 0, 0);
                    }

                    int tBase = qi * 6;
                    for (int t = 0; t < srcTris.Length && t < 6; t++)
                        _tris[tBase + t] = vBase + srcTris[t];
                    // Pad unused triangle slots.
                    for (int t = srcTris.Length; t < 6; t++)
                        _tris[tBase + t] = vBase;
                }
                else
                {
                    // Fallback: render as plain quad for shapes with > 4 verts.
                    float cos = Mathf.Cos(p.AngleDeg * Mathf.Deg2Rad);
                    float sin = Mathf.Sin(p.AngleDeg * Mathf.Deg2Rad);
                    float hx = p.ScaleX * 0.5f, hy = p.ScaleY * 0.5f;
                    _verts[vBase+0] = RotateScale(p.X, p.Y, -hx, -hy, cos, sin);
                    _verts[vBase+1] = RotateScale(p.X, p.Y,  hx, -hy, cos, sin);
                    _verts[vBase+2] = RotateScale(p.X, p.Y,  hx,  hy, cos, sin);
                    _verts[vBase+3] = RotateScale(p.X, p.Y, -hx,  hy, cos, sin);
                    _uvs[vBase+0] = new Vector2(uvRect.x,          uvRect.y         );
                    _uvs[vBase+1] = new Vector2(uvRect.x + uvRect.z, uvRect.y         );
                    _uvs[vBase+2] = new Vector2(uvRect.x + uvRect.z, uvRect.y + uvRect.w);
                    _uvs[vBase+3] = new Vector2(uvRect.x,          uvRect.y + uvRect.w);
                    _cols[vBase+0] = _cols[vBase+1] = _cols[vBase+2] = _cols[vBase+3] = c32;
                    int tBase = qi * 6;
                    _tris[tBase+0]=vBase; _tris[tBase+1]=vBase+1; _tris[tBase+2]=vBase+2;
                    _tris[tBase+3]=vBase; _tris[tBase+4]=vBase+2; _tris[tBase+5]=vBase+3;
                }

                qi++;
            }

            _combinedMesh.Clear();
            if (qi == 0) return;

            _combinedMesh.SetVertices(_verts, 0, qi * 4);
            _combinedMesh.SetUVs(0, _uvs,    0, qi * 4);
            _combinedMesh.SetColors(_cols,    0, qi * 4);
            _combinedMesh.SetTriangles(_tris, 0, qi * 6, 0);
            _combinedMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);

            // FIX: pass _combinedMeshMpb so _UVRect = (0,0,1,1) reaches the shader
            // regardless of whatever default value is serialized on the material.
            // camera=null → all cameras, submeshIndex=0.
            Graphics.DrawMesh(
                _combinedMesh, Matrix4x4.identity, _atlasMaterial,
                gameObject.layer, null, 0, _combinedMeshMpb);
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static Vector3 RotateScale(
            float cx, float cy, float lx, float ly, float cos, float sin)
            => new(cx + cos * lx - sin * ly, cy + sin * lx + cos * ly, 0f);

        private static Vector4 ComputeTint(ref NativeProjectile p)
        {
            float f = p.Lifetime / Mathf.Max(p.MaxLifetime, 0.0001f);
            float a = f < 0.15f ? f / 0.15f : 1f;
            return new Vector4(1f, 1f, 1f, a);
        }

        private Mesh _defaultQuad;
        private Mesh GetOrBuildDefaultQuad()
        {
            if (_defaultQuad != null) return _defaultQuad;
            var m = new Mesh { name = "ProjectileDefaultQuad" };
            m.vertices  = new[] {
                new Vector3(-0.5f,-0.5f,0), new Vector3(0.5f,-0.5f,0),
                new Vector3( 0.5f, 0.5f,0), new Vector3(-0.5f,0.5f,0),
            };
            m.uv        = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
            m.triangles = new[] { 0,1,2, 0,2,3 };
            m.RecalculateBounds();
            _defaultQuad = m;
            return m;
        }

        private void EnsureConfigMeshCache(ProjectileRegistry reg)
        {
            if (_configMeshCache != null && _configMeshCache.Length == reg.Count) return;
            _configMeshCache = new Mesh[reg.Count];
            for (int i = 0; i < reg.Count; i++)
            {
                var cfg = reg.Get((ushort)i);
                _configMeshCache[i] = (cfg != null && cfg.CustomShape != null)
                    ? cfg.CustomShape.GetMesh()
                    : GetOrBuildDefaultQuad();
            }
        }
    }
}
