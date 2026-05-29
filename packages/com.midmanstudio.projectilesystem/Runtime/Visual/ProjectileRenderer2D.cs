// ProjectileRenderer2D.cs
// FIXES (combined-mesh path — primary path on hardware without GPU instancing):
//   + Two-pass render now uses TWO separate Mesh objects (_spriteMesh, _shapeMesh)
//     instead of sharing one. Previously Pass 2 called mesh.Clear() after Pass 1
//     already submitted Graphics.DrawMesh() — Unity holds a reference not a copy
//     so Pass 1 rendered an empty mesh. Now each pass owns its mesh entirely.
//   + RenderCombinedGroup signature takes the mesh as a parameter.
//   + OnDestroy destroys both meshes.
//   + Awake logs which path is active so hardware issues are immediately visible.
//
// NOTE: Instanced path (DrawMeshInstanced) is unchanged — it uses per-configId
// groups with MaterialPropertyBlock and never touches the combined meshes.
// On hardware without GPU instancing (_forceDrawMesh=true or
// SystemInfo.supportsInstancing=false) the combined path is the ONLY path.

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
                 "Enable when GPU instancing is not supported on target hardware.\n" +
                 "The combined path is ~4-10x slower than instanced but works everywhere.")]
        [SerializeField] private bool _forceDrawMesh;

        // ── Instanced path ─────────────────────────────────────────────────
        private const int BATCH_SIZE = 1023;
        private Matrix4x4[]           _matrices;
        private Vector4[]             _uvRects;
        private Vector4[]             _colors;
        private MaterialPropertyBlock _mpb;

        // ── Combined mesh path ─────────────────────────────────────────────
        // TWO separate meshes — one per pass — so DrawMesh(pass1) is never
        // clobbered by pass2.Clear() before the GPU processes it.
        private const int MAX_QUADS = 2048;

        private Mesh      _spriteMesh;   // Pass 1: configs with sprites (atlas texture)
        private Mesh      _shapeMesh;    // Pass 2: configs without sprites (white texture)

        // Shared CPU-side arrays — written by each pass into its own mesh
        // These are re-used across passes (pass 2 overwrites after pass 1 uploads)
        // which is safe because SetVertices/SetTriangles copies into the mesh.
        private Vector3[] _verts;
        private Vector2[] _uvs;
        private Color32[] _cols;
        private int[]     _tris;

        private MaterialPropertyBlock _combinedMpb;

        // ── Per-configId index grouping (instanced path) ───────────────────
        private readonly Dictionary<ushort, List<int>> _configGroups = new(32);

        private RenderPath _path;
        private Mesh       _defaultQuad;

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
                // Two separate meshes — critical so pass 2 doesn't clobber pass 1
                _spriteMesh = new Mesh { name = "ProjectileSprite2D" };
                _spriteMesh.MarkDynamic();
                _shapeMesh  = new Mesh { name = "ProjectileShape2D" };
                _shapeMesh.MarkDynamic();

                _verts = new Vector3[MAX_QUADS * 4];
                _uvs   = new Vector2[MAX_QUADS * 4];
                _cols  = new Color32[MAX_QUADS * 4];
                _tris  = new int[MAX_QUADS * 6];
            }

            _combinedMpb = new MaterialPropertyBlock();

            Debug.Log(
                $"[ProjectileRenderer2D] Path={_path}" +
                $" | HW Instancing:{SystemInfo.supportsInstancing}" +
                $" | ForceDrawMesh:{_forceDrawMesh}");
        }

        private void OnDestroy()
        {
            if (_spriteMesh  != null) Destroy(_spriteMesh);
            if (_shapeMesh   != null) Destroy(_shapeMesh);
            if (_defaultQuad != null) Destroy(_defaultQuad);
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Render(NativeProjectile[] projs, int count)
        {
            if (_atlasMaterial == null)
            {
                Debug.LogWarning("[ProjectileRenderer2D] _atlasMaterial is not assigned.", this);
                return;
            }
            if (count == 0) return;

            if (_path == RenderPath.Instanced)
                RenderInstanced(projs, count);
            else
                RenderCombined(projs, count);
        }

        // ── Instanced path ────────────────────────────────────────────────────

        private void RenderInstanced(NativeProjectile[] projs, int count)
        {
            var reg = ProjectileRegistry.Instance;

            _configGroups.Clear();
            for (int i = 0; i < count; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;
                var cfg = reg.Get(p.ConfigId);
                if (cfg == null) continue;

                if (!_configGroups.TryGetValue(p.ConfigId, out var lst))
                {
                    lst = new List<int>(64);
                    _configGroups[p.ConfigId] = lst;
                }
                lst.Add(i);
            }

            foreach (var kv in _configGroups)
            {
                var cfg = reg.Get(kv.Key);
                if (cfg == null) continue;

                Mesh mesh = GetMeshForConfig(cfg);

                Texture2D tex;
                Vector4   uvRect;

                if (cfg.UseSprite && cfg.ProjectileSprite?.texture != null)
                {
                    tex    = cfg.ProjectileSprite.texture;
                    uvRect = ComputeSpriteUVRect(cfg);
                }
                else
                {
                    tex    = Texture2D.whiteTexture;
                    uvRect = new Vector4(0f, 0f, 1f, 1f);
                }

                float aspectY = cfg.FullSizeX > 0.001f
                                ? cfg.FullSizeY / cfg.FullSizeX : 1f;

                var idxList = kv.Value;
                int start   = 0;

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
        // Two separate passes, each writing into its OWN Mesh object.
        // Pass 1 (sprite)  → _spriteMesh
        // Pass 2 (shape)   → _shapeMesh
        // DrawMesh is called immediately after each mesh is built so the GPU
        // command is queued before the next pass clears its own separate mesh.

        private void RenderCombined(NativeProjectile[] projs, int count)
        {
            RenderCombinedGroup(projs, count, spritePass: true,  mesh: _spriteMesh);
            RenderCombinedGroup(projs, count, spritePass: false, mesh: _shapeMesh);
        }

        private void RenderCombinedGroup(
            NativeProjectile[] projs, int count, bool spritePass, Mesh mesh)
        {
            var       reg      = ProjectileRegistry.Instance;
            int       qi       = 0;
            Texture2D firstTex = spritePass ? null : Texture2D.whiteTexture;

            for (int i = 0; i < count && qi < MAX_QUADS; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = reg.Get(p.ConfigId);
                if (cfg == null) continue;

                bool hasSprite = cfg.UseSprite && cfg.ProjectileSprite?.texture != null;
                if ( spritePass && !hasSprite) continue;
                if (!spritePass &&  hasSprite) continue;

                if (spritePass && firstTex == null)
                    firstTex = cfg.ProjectileSprite.texture;

                float aspectY = cfg.FullSizeX > 0.001f ? cfg.FullSizeY / cfg.FullSizeX : 1f;
                float sx      = p.ScaleX;
                float sy      = p.ScaleX * aspectY;

                Vector4 uvRect = spritePass ? ComputeSpriteUVRect(cfg)
                                            : new Vector4(0f, 0f, 1f, 1f);

                Vector4 tint = ComputeTint(ref p);
                var c32 = new Color32(
                    (byte)(tint.x * 255f), (byte)(tint.y * 255f),
                    (byte)(tint.z * 255f), (byte)(tint.w * 255f));

                Mesh   srcMesh  = GetMeshForConfig(cfg);
                var    srcVerts = srcMesh.vertices;
                var    srcUVs   = srcMesh.uv;
                var    srcTris  = srcMesh.triangles;
                int    vc       = srcVerts.Length;
                int    vBase    = qi * 4;

                float cos = Mathf.Cos(p.AngleDeg * Mathf.Deg2Rad);
                float sin = Mathf.Sin(p.AngleDeg * Mathf.Deg2Rad);

                if (vc <= 4)
                {
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
                    // Pad remaining slots to degenerate (invisible)
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
                    // > 4 verts (Arrow=7, Needle=5, custom shapes):
                    // Use bounding quad fallback for combined path.
                    // Instanced path handles these shapes correctly at full fidelity.
                    // If you need exact shapes on non-instancing hardware, use
                    // the instanced path (remove _forceDrawMesh or upgrade GPU).
                    float hx = sx * 0.5f, hy = sy * 0.5f;
                    _verts[vBase+0] = RotateScale(p.X, p.Y, -hx, -hy, cos, sin);
                    _verts[vBase+1] = RotateScale(p.X, p.Y,  hx, -hy, cos, sin);
                    _verts[vBase+2] = RotateScale(p.X, p.Y,  hx,  hy, cos, sin);
                    _verts[vBase+3] = RotateScale(p.X, p.Y, -hx,  hy, cos, sin);
                    _uvs[vBase+0] = new Vector2(uvRect.x,            uvRect.y);
                    _uvs[vBase+1] = new Vector2(uvRect.x + uvRect.z, uvRect.y);
                    _uvs[vBase+2] = new Vector2(uvRect.x + uvRect.z, uvRect.y + uvRect.w);
                    _uvs[vBase+3] = new Vector2(uvRect.x,            uvRect.y + uvRect.w);
                    _cols[vBase+0] = _cols[vBase+1] = _cols[vBase+2] = _cols[vBase+3] = c32;
                    int tBase = qi * 6;
                    _tris[tBase+0]=vBase;   _tris[tBase+1]=vBase+1; _tris[tBase+2]=vBase+2;
                    _tris[tBase+3]=vBase;   _tris[tBase+4]=vBase+2; _tris[tBase+5]=vBase+3;
                }

                qi++;
            }

            if (qi == 0) return;

            // Upload into THIS pass's dedicated mesh — never touches the other pass's mesh
            mesh.Clear();
            mesh.SetVertices(_verts, 0, qi * 4);
            mesh.SetUVs(0, _uvs,    0, qi * 4);
            mesh.SetColors(_cols,    0, qi * 4);
            mesh.SetTriangles(_tris, 0, qi * 6, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);

            _combinedMpb.SetTexture("_MainTex", firstTex ?? Texture2D.whiteTexture);

            Graphics.DrawMesh(
                mesh, Matrix4x4.identity, _atlasMaterial,
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
                new Vector3(-0.5f, -0.5f, 0f), new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f), new Vector3(-0.5f,  0.5f, 0f),
            };
            _defaultQuad.uv = new[] {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            _defaultQuad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _defaultQuad.RecalculateBounds();
            return _defaultQuad;
        }

        private static Vector4 ComputeSpriteUVRect(ProjectileConfigSO cfg)
        {
            var sprite = cfg.ProjectileSprite;
            if (sprite == null) return new Vector4(0f, 0f, 1f, 1f);
            var tex = sprite.texture;
            if (tex == null)   return new Vector4(0f, 0f, 1f, 1f);
            return new Vector4(
                sprite.rect.x      / tex.width,
                sprite.rect.y      / tex.height,
                sprite.rect.width  / tex.width,
                sprite.rect.height / tex.height);
        }

        private static Vector3 RotateScale(
            float cx, float cy, float lx, float ly, float cos, float sin)
            => new(cx + cos * lx - sin * ly,
                   cy + sin * lx + cos * ly, 0f);

        private static Vector4 ComputeTint(ref NativeProjectile p)
        {
            float f = p.Lifetime / Mathf.Max(p.MaxLifetime, 0.0001f);
            float a = f < 0.15f ? f / 0.15f : 1f;
            return new Vector4(1f, 1f, 1f, a);
        }
    }
}
