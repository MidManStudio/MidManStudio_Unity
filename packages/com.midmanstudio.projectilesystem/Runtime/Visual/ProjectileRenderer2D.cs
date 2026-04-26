// ProjectileRenderer2D.cs

using UnityEngine;

namespace MidManStudio.Projectiles
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

        // Per-config mesh cache for the combined path
        // (instanced path uses per-config meshes directly)
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

            Debug.Log(
                $"[ProjectileRenderer2D] Using {(_path == RenderPath.Instanced ? "Instanced" : "CombinedMesh")}RenderPath" +
                $" | GPU: {SystemInfo.graphicsDeviceName}" +
                $" | API: {SystemInfo.graphicsDeviceType}" +
                $" | Instancing: {SystemInfo.supportsInstancing}" +
                $" | ForceDrawMesh: {_forceDrawMesh}");
        }

        void OnDestroy()
        {
            if (_combinedMesh != null) Destroy(_combinedMesh);
        }

        // ─── Called by ProjectileManager.LateUpdate ───────────────────────────

        public void Render(NativeProjectile[] projs, int count)
        {
            if (_atlasMaterial == null) return;

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

            // Build per-config mesh table once
            EnsureConfigMeshCache(reg);

            while (batchStart < count)
            {
                int n = 0, end = Mathf.Min(batchStart + BATCH_SIZE, count);

                for (int i = batchStart; i < end; i++)
                {
                    ref var p = ref projs[i];
                    if (p.Alive == 0) continue;

                    var cfg = reg.Get(p.ConfigId);
                    if (!cfg.UseSprite) continue;

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

                    // Use config mesh if available, else quad
                    Mesh mesh = (_configMeshCache != null && projs[batchStart].ConfigId < _configMeshCache.Length
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
        // Builds one large mesh per frame with atlas UVs and tint baked per vertex.
        // No GPU instancing needed. Correct path for OpenGL 3.3 / older hardware.

        private void RenderCombined(NativeProjectile[] projs, int count)
        {
            var reg = ProjectileRegistry.Instance;
            int qi  = 0;

            for (int i = 0; i < count && qi < MAX_QUADS; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = reg.Get(p.ConfigId);
                if (!cfg.UseSprite) continue;

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
                int vc = srcVerts.Length;
                int vBase = qi * 4; // NOTE: still using 4 for the output stride

                // For non-quad meshes: blit all vertices of this mesh into our combined array.
                // We use a flat per-mesh blit instead of the 4-vertex quad scheme.
                // The combined mesh is rebuilt from scratch each frame so qi tracks quads
                // but we write vc verts per entry — this works as long as vc <= 4.
                // For shapes with more verts, use the oversize combined path below.

                if (vc <= 4)
                {
                    float cos = Mathf.Cos(p.AngleDeg * Mathf.Deg2Rad);
                    float sin = Mathf.Sin(p.AngleDeg * Mathf.Deg2Rad);

                    for (int v = 0; v < vc; v++)
                    {
                        _verts[vBase + v] = RotateScale(
                            p.X, p.Y, srcVerts[v].x * p.ScaleX, srcVerts[v].y * p.ScaleY, cos, sin);
                        // Remap local [0,1] UV into atlas rect
                        _uvs[vBase + v] = new Vector2(
                            uvRect.x + srcUVs[v].x * uvRect.z,
                            uvRect.y + srcUVs[v].y * uvRect.w);
                        _cols[vBase + v] = c32;
                    }

                    int tBase = qi * 6;
                    for (int t = 0; t < srcTris.Length && t < 6; t++)
                        _tris[tBase + t] = vBase + srcTris[t];
                }
                else
                {
                    // Fallback: render as plain quad if custom mesh has too many verts
                    // for the pre-allocated stride. Upgrade MAX_QUADS array sizes if needed.
                    float cos = Mathf.Cos(p.AngleDeg * Mathf.Deg2Rad);
                    float sin = Mathf.Sin(p.AngleDeg * Mathf.Deg2Rad);
                    float hx = p.ScaleX * 0.5f, hy = p.ScaleY * 0.5f;
                    _verts[vBase+0] = RotateScale(p.X,p.Y,-hx,-hy,cos,sin);
                    _verts[vBase+1] = RotateScale(p.X,p.Y, hx,-hy,cos,sin);
                    _verts[vBase+2] = RotateScale(p.X,p.Y, hx, hy,cos,sin);
                    _verts[vBase+3] = RotateScale(p.X,p.Y,-hx, hy,cos,sin);
                    _uvs[vBase+0] = new Vector2(uvRect.x,         uvRect.y        );
                    _uvs[vBase+1] = new Vector2(uvRect.x+uvRect.z,uvRect.y        );
                    _uvs[vBase+2] = new Vector2(uvRect.x+uvRect.z,uvRect.y+uvRect.w);
                    _uvs[vBase+3] = new Vector2(uvRect.x,         uvRect.y+uvRect.w);
                    _cols[vBase+0]=_cols[vBase+1]=_cols[vBase+2]=_cols[vBase+3]=c32;
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
            // Prevent frustum culling from hiding projectiles near screen edge
            _combinedMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);

            Graphics.DrawMesh(
                _combinedMesh, Matrix4x4.identity, _atlasMaterial, gameObject.layer);
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
            m.uv = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
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
                _configMeshCache[i] = cfg.CustomShape != null
                    ? cfg.CustomShape.GetMesh()
                    : GetOrBuildDefaultQuad();
            }
        }
    }
}