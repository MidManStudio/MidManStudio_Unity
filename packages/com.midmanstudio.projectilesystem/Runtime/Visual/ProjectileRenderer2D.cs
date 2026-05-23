// ProjectileRenderer2D.cs
// FIXES:
//   1. Instanced path now groups by configId → each config uses its correct mesh
//   2. MPB.SetTexture("_MainTex") applied per draw call → sprite texture auto-used
//   3. Sprite UV rect computed from config sprite for both paths
//   4. Combined mesh path uses first alive projectile's texture
//   5. Aspect ratio (FullSizeY/FullSizeX) correctly applied in both paths
//   6. Custom shapes with > 4 verts render correctly in instanced path;
//      fall back to bounding quad only in combined-mesh (old HW) path

using System.Collections.Generic;
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

        [Tooltip("Force combined-mesh fallback path even on instancing-capable hardware.\n" +
                 "Enable only for debugging — instanced path is 4-10× faster.")]
        [SerializeField] private bool _forceDrawMesh;

        // ── Instanced path ─────────────────────────────────────────────────
        private const int BATCH_SIZE = 1023;
        private Matrix4x4[] _matrices;
        private Vector4[]   _uvRects;
        private Vector4[]   _colors;
        private MaterialPropertyBlock _mpb;

        // ── Combined mesh path ─────────────────────────────────────────────
        private const int MAX_QUADS = 2048;
        private Mesh      _combinedMesh;
        private Vector3[] _verts;
        private Vector2[] _uvs;
        private Color32[] _cols;
        private int[]     _tris;
        private MaterialPropertyBlock _combinedMpb;

        // ── Per-configId index grouping (instanced path) ───────────────────
        private readonly Dictionary<ushort, List<int>> _configGroups = new(32);

        private RenderPath _path;
        private Mesh _defaultQuad;

        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
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
                _combinedMesh = new Mesh { name = "ProjectileCombined2D" };
                _combinedMesh.MarkDynamic();
                _verts = new Vector3[MAX_QUADS * 4];
                _uvs   = new Vector2[MAX_QUADS * 4];
                _cols  = new Color32[MAX_QUADS * 4];
                _tris  = new int[MAX_QUADS * 6];
            }

            // Identity MPB for combined path — prevents wrong material default _UVRect
            _combinedMpb = new MaterialPropertyBlock();
            _combinedMpb.SetVector("_UVRect", new Vector4(0f, 0f, 1f, 1f));
            _combinedMpb.SetVector("_Color",  new Vector4(1f, 1f, 1f, 1f));

            Debug.Log(
                $"[ProjectileRenderer2D] Path={_path}" +
                $" | HW Instancing:{SystemInfo.supportsInstancing}" +
                $" | ForceDrawMesh:{_forceDrawMesh}");
        }

        private void OnDestroy()
        {
            if (_combinedMesh != null) Destroy(_combinedMesh);
            if (_defaultQuad  != null) Destroy(_defaultQuad);
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Render(NativeProjectile[] projs, int count)
        {
            if (_atlasMaterial == null)
            {
                Debug.LogWarning(
                    "[ProjectileRenderer2D] _atlasMaterial is not assigned. " +
                    "Assign a material using InstancedProjectile_URP.shader.", this);
                return;
            }

            if (count == 0) return;

            if (_path == RenderPath.Instanced)
                RenderInstanced(projs, count);
            else
                RenderCombined(projs, count);
        }

        // ── Instanced path ────────────────────────────────────────────────────
        //
        // Groups alive projectiles by configId so each group gets:
        //   • its own mesh (Quad / Needle / Diamond / Arrow / Custom)
        //   • its own sprite texture set via MPB.SetTexture("_MainTex")
        //   • the correct UV rect for its sprite within that texture
        //
        // All instances in one DrawMeshInstanced call share the same mesh and
        // texture; this is why grouping by configId is required.

        private void RenderInstanced(NativeProjectile[] projs, int count)
        {
            var reg = ProjectileRegistry.Instance;

            // Phase 1: collect alive indices per configId
            _configGroups.Clear();
            for (int i = 0; i < count; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;
                var cfg = reg.Get(p.ConfigId);
                if (cfg == null || !cfg.UseSprite) continue;

                if (!_configGroups.TryGetValue(p.ConfigId, out var lst))
                {
                    lst = new List<int>(64);
                    _configGroups[p.ConfigId] = lst;
                }
                lst.Add(i);
            }

            // Phase 2: draw each configId group
            foreach (var kv in _configGroups)
            {
                var cfg = reg.Get(kv.Key);
                if (cfg == null) continue;

                Mesh      mesh    = GetMeshForConfig(cfg);
                Texture2D tex     = cfg.ProjectileSprite?.texture;
                Vector4   uvRect  = ComputeSpriteUVRect(cfg);
                float     aspectY = cfg.FullSizeX > 0.001f
                                    ? cfg.FullSizeY / cfg.FullSizeX : 1f;

                var   idxList = kv.Value;
                int   start   = 0;

                while (start < idxList.Count)
                {
                    int n   = 0;
                    int end = Mathf.Min(start + BATCH_SIZE, idxList.Count);

                    for (int j = start; j < end; j++)
                    {
                        ref var p = ref projs[idxList[j]];

                        _matrices[n] = Matrix4x4.TRS(
                            new Vector3(p.X, p.Y, 0f),
                            Quaternion.Euler(0f, 0f, p.AngleDeg),
                            new Vector3(p.ScaleX, p.ScaleX * aspectY, 1f));

                        _uvRects[n] = uvRect;
                        _colors[n]  = ComputeTint(ref p);
                        n++;
                    }

                    if (n > 0)
                    {
                        _mpb.SetVectorArray("_UVRect", _uvRects);
                        _mpb.SetVectorArray("_Color",  _colors);

                        // KEY FIX: set the sprite's texture for this batch
                        if (tex != null)
                            _mpb.SetTexture("_MainTex", tex);

                        Graphics.DrawMeshInstanced(
                            mesh, 0, _atlasMaterial, _matrices, n, _mpb,
                            UnityEngine.Rendering.ShadowCastingMode.Off,
                            receiveShadows: false,
                            layer: gameObject.layer);
                    }

                    start = end;
                }
            }
        }

        // ── Combined mesh path ────────────────────────────────────────────────
        //
        // Builds a single combined mesh from all alive projectiles and issues one
        // Graphics.DrawMesh call.  UVs are baked per-vertex; texture is set via MPB.
        // Meshes with > 4 verts (Needle=5, Arrow=7) fall back to bounding quad here —
        // use the instanced path for correct custom shapes on modern hardware.

        private void RenderCombined(NativeProjectile[] projs, int count)
        {
            var       reg      = ProjectileRegistry.Instance;
            int       qi       = 0;
            Texture2D firstTex = null;

            for (int i = 0; i < count && qi < MAX_QUADS; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = reg.Get(p.ConfigId);
                if (cfg == null || !cfg.UseSprite) continue;

                // Grab the first texture we find for the batch draw call
                if (firstTex == null && cfg.ProjectileSprite?.texture != null)
                    firstTex = cfg.ProjectileSprite.texture;

                float aspectY = cfg.FullSizeX > 0.001f ? cfg.FullSizeY / cfg.FullSizeX : 1f;
                float sx = p.ScaleX;
                float sy = p.ScaleX * aspectY;

                Vector4 uvRect = ComputeSpriteUVRect(cfg);
                Vector4 tint   = ComputeTint(ref p);
                var c32 = new Color32(
                    (byte)(tint.x * 255f), (byte)(tint.y * 255f),
                    (byte)(tint.z * 255f), (byte)(tint.w * 255f));

                Mesh srcMesh  = GetMeshForConfig(cfg);
                var  srcVerts = srcMesh.vertices;
                var  srcUVs   = srcMesh.uv;
                var  srcTris  = srcMesh.triangles;
                int  vc       = srcVerts.Length;
                int  vBase    = qi * 4;

                float cos = Mathf.Cos(p.AngleDeg * Mathf.Deg2Rad);
                float sin = Mathf.Sin(p.AngleDeg * Mathf.Deg2Rad);

                if (vc <= 4)
                {
                    // Exact mesh — up to 4 verts supported directly
                    for (int v = 0; v < vc; v++)
                    {
                        _verts[vBase + v] = RotateScale(
                            p.X, p.Y,
                            srcVerts[v].x * sx, srcVerts[v].y * sy,
                            cos, sin);
                        _uvs[vBase + v] = new Vector2(
                            uvRect.x + srcUVs[v].x * uvRect.z,
                            uvRect.y + srcUVs[v].y * uvRect.w);
                        _cols[vBase + v] = c32;
                    }
                    // Pad unused verts to degenerate (zero alpha, same pos)
                    for (int v = vc; v < 4; v++)
                    {
                        _verts[vBase + v] = _verts[vBase];
                        _uvs[vBase + v]   = _uvs[vBase];
                        _cols[vBase + v]  = new Color32(0, 0, 0, 0);
                    }
                    int tBase = qi * 6;
                    for (int t = 0; t < Mathf.Min(srcTris.Length, 6); t++)
                        _tris[tBase + t] = vBase + srcTris[t];
                    for (int t = Mathf.Min(srcTris.Length, 6); t < 6; t++)
                        _tris[tBase + t] = vBase;
                }
                else
                {
                    // > 4 verts (Needle, Arrow, large Custom) — approximate bounding quad.
                    // For correct custom shapes use instanced path (modern hardware).
                    float hx = sx * 0.5f, hy = sy * 0.5f;
                    _verts[vBase+0] = RotateScale(p.X, p.Y, -hx, -hy, cos, sin);
                    _verts[vBase+1] = RotateScale(p.X, p.Y,  hx, -hy, cos, sin);
                    _verts[vBase+2] = RotateScale(p.X, p.Y,  hx,  hy, cos, sin);
                    _verts[vBase+3] = RotateScale(p.X, p.Y, -hx,  hy, cos, sin);
                    _uvs[vBase+0]   = new Vector2(uvRect.x,            uvRect.y);
                    _uvs[vBase+1]   = new Vector2(uvRect.x + uvRect.z, uvRect.y);
                    _uvs[vBase+2]   = new Vector2(uvRect.x + uvRect.z, uvRect.y + uvRect.w);
                    _uvs[vBase+3]   = new Vector2(uvRect.x,            uvRect.y + uvRect.w);
                    _cols[vBase+0]  = _cols[vBase+1] = _cols[vBase+2] = _cols[vBase+3] = c32;
                    int tBase = qi * 6;
                    _tris[tBase+0]=vBase;   _tris[tBase+1]=vBase+1; _tris[tBase+2]=vBase+2;
                    _tris[tBase+3]=vBase;   _tris[tBase+4]=vBase+2; _tris[tBase+5]=vBase+3;
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

            if (firstTex != null)
                _combinedMpb.SetTexture("_MainTex", firstTex);

            Graphics.DrawMesh(
                _combinedMesh, Matrix4x4.identity, _atlasMaterial,
                gameObject.layer, null, 0, _combinedMpb);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private Mesh GetMeshForConfig(ProjectileConfigSO cfg)
        {
            if (cfg.CustomShape != null)
            {
                var m = cfg.CustomShape.GetMesh();
                if (m != null && m.vertexCount > 0) return m;
            }
            return GetDefaultQuad();
        }

        private Mesh GetDefaultQuad()
        {
            if (_defaultQuad != null) return _defaultQuad;
            _defaultQuad = new Mesh { name = "ProjDefaultQuad" };
            _defaultQuad.vertices  = new[] {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
            };
            _defaultQuad.uv = new[] {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            _defaultQuad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _defaultQuad.RecalculateBounds();
            return _defaultQuad;
        }

        /// <summary>
        /// Returns the UV rect of the config's sprite within its source texture.
        /// For a sprite packed in an atlas this maps to the atlas sub-rect.
        /// For a standalone sprite this returns (0,0,1,1).
        /// </summary>
        private static Vector4 ComputeSpriteUVRect(ProjectileConfigSO cfg)
        {
            var sprite = cfg.ProjectileSprite;
            if (sprite == null) return new Vector4(0f, 0f, 1f, 1f);
            var tex = sprite.texture;
            if (tex == null) return new Vector4(0f, 0f, 1f, 1f);
            return new Vector4(
                sprite.rect.x      / tex.width,
                sprite.rect.y      / tex.height,
                sprite.rect.width  / tex.width,
                sprite.rect.height / tex.height);
        }

        private static Vector3 RotateScale(
            float cx, float cy,
            float lx, float ly,
            float cos, float sin)
            => new(cx + cos * lx - sin * ly,
                   cy + sin * lx + cos * ly,
                   0f);

        private static Vector4 ComputeTint(ref NativeProjectile p)
        {
            float f = p.Lifetime / Mathf.Max(p.MaxLifetime, 0.0001f);
            float a = f < 0.15f ? f / 0.15f : 1f;
            return new Vector4(1f, 1f, 1f, a);
        }
    }
}
