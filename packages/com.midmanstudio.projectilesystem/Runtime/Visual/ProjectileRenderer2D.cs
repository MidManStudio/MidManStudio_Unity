
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

        [Tooltip("Force combined-mesh fallback path even on instancing-capable hardware.")]
        [SerializeField] private bool _forceDrawMesh;

        // ── Sorting workaround ──────────────────────────────────────────────
        //
        // Graphics.DrawMesh / DrawMeshInstanced are immediate-mode calls — they
        // don't go through a Renderer component, so there is no
        // sortingLayerID/sortingOrder to set on them at all, full stop. That's
        // a genuine Unity limitation of this API, not something this package
        // was missing an accessor for.
        //
        // WORKAROUND: nudge each config-group's Z position by an amount derived
        // from ProjectileConfigSO.SortingPriority (which folds SortingLayerName
        // + SortingOrderInLayer into one comparable number). This DOES give you
        // correct, stable ordering between RustSim-drawn projectiles.
        //
        // CAVEAT — please actually check this: whether it also sorts correctly
        // against your OTHER SpriteRenderer-based objects (enemies, player, UI-
        // in-world, etc.) depends on your 2D camera's "Transparency Sort Mode"
        // (Camera Inspector, or Project Settings → Graphics for the project
        // default). It needs to be set to sort by distance along an axis that
        // this Z offset actually moves things along — e.g. Orthographic mode
        // with sort axis (0,0,1), which is the standard 2D-camera setup. If
        // your project's other sprites don't vary in Z at all today, this will
        // still self-consistently order every RustSim projectile against every
        // other RustSim projectile; the "does it also correctly land in front
        // of / behind a specific enemy sprite" part is the piece I can't verify
        // without seeing your camera/render pipeline settings.
        [Header("Sorting Workaround")]
        [Tooltip("World-Z distance per SortingPriority unit, used to fake sorting " +
                 "layer/order for these DrawMesh calls. See the class header comment.")]
        [SerializeField] private float _sortDepthStep = 0.0001f;

        // ── Instanced path ─────────────────────────────────────────────────
        private const int BATCH_SIZE = 1023;
        private Matrix4x4[]           _matrices;
        private Vector4[]             _uvRects;
        private Vector4[]             _colors;
        private MaterialPropertyBlock _mpb;

        // ── Combined mesh path ─────────────────────────────────────────────
        private const int MAX_QUADS = 2048;

        // UPDATED: was 7 / 15. Cross = 12 verts, 10 tris × 3 = 30 indices.
        // Custom shapes can exceed these — the overflow guard in RenderCombinedGroup
        // will skip any projectile whose shape would overflow the arrays.
        private const int MAX_SHAPE_VERTS = 12;
        private const int MAX_SHAPE_TRIS  = 30;

        // Two separate meshes — one per pass — so DrawMesh(pass1) is never
        // clobbered by pass2.Clear() before the GPU processes it.
        private Mesh _spriteMesh;
        private Mesh _shapeMesh;

        // CPU-side arrays sized for worst-case shape per slot.
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
                _spriteMesh = new Mesh { name = "ProjectileSprite2D" };
                _spriteMesh.MarkDynamic();
                _shapeMesh  = new Mesh { name = "ProjectileShape2D" };
                _shapeMesh.MarkDynamic();

                _verts = new Vector3[MAX_QUADS * MAX_SHAPE_VERTS];
                _uvs   = new Vector2[MAX_QUADS * MAX_SHAPE_VERTS];
                _cols  = new Color32[MAX_QUADS * MAX_SHAPE_VERTS];
                _tris  = new int   [MAX_QUADS * MAX_SHAPE_TRIS];
            }

            _combinedMpb = new MaterialPropertyBlock();

            Debug.Log(
                $"[ProjectileRenderer2D] Path={_path}" +
                $" | HW Instancing:{SystemInfo.supportsInstancing}" +
                $" | ForceDrawMesh:{_forceDrawMesh}" +
                $" | MaxShapeVerts:{MAX_SHAPE_VERTS} MaxShapeTris:{MAX_SHAPE_TRIS}");
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

                // Same Z for every projectile in this group — they share cfg,
                // so they share SortingPriority. Negated so a HIGHER priority
                // (further-forward layer/order) gets a smaller/negative Z,
                // i.e. nearer the camera under a standard orthographic 2D setup.
                float z = -(float)cfg.SortingPriority * _sortDepthStep;

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
                            new Vector3(p.X, p.Y, z),
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

        private void RenderCombined(NativeProjectile[] projs, int count)
        {
            RenderCombinedGroup(projs, count, spritePass: true,  mesh: _spriteMesh);
            RenderCombinedGroup(projs, count, spritePass: false, mesh: _shapeMesh);
        }

        // FIX (response 3): vBase and tBase are tracked dynamically using each
        // shape's actual vertex / triangle-index count, so Arrow (7 verts),
        // Needle (5 verts), Cross (12 verts), LetterI (12 verts), and any Custom
        // shape all render correctly. The previous code used fixed qi*4 / qi*6
        // which assumed 4 verts per shape and silently corrupted geometry for
        // any shape with more verts.
        private void RenderCombinedGroup(
            NativeProjectile[] projs, int count, bool spritePass, Mesh mesh)
        {
            var       reg      = ProjectileRegistry.Instance;
            int       qi       = 0;      // slot count (for MAX_QUADS guard)
            int       vBase    = 0;      // next free vertex index
            int       tBase    = 0;      // next free triangle-index slot
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

                Mesh   srcMesh = GetMeshForConfig(cfg);
                var    srcV    = srcMesh.vertices;
                var    srcUV   = srcMesh.uv;
                var    srcT    = srcMesh.triangles;
                int    vc      = srcV.Length;
                int    tc      = srcT.Length;

                // Skip if this shape would overflow either array
                if (vBase + vc > _verts.Length) break;
                if (tBase + tc > _tris.Length)  break;

                float aspectY = cfg.FullSizeX > 0.001f ? cfg.FullSizeY / cfg.FullSizeX : 1f;
                float sx      = p.ScaleX;
                float sy      = p.ScaleX * aspectY;

                // Unlike the instanced path, this loop mixes projectiles from
                // several different configs into one combined mesh — so the Z
                // offset has to be resolved per-projectile, not once per group.
                // See the class header comment / the field above for the full
                // explanation and the camera-setting caveat.
                float z = -(float)cfg.SortingPriority * _sortDepthStep;

                Vector4 uvRect = spritePass ? ComputeSpriteUVRect(cfg)
                                            : new Vector4(0f, 0f, 1f, 1f);

                Vector4 tint = ComputeTint(ref p);
                var c32 = new Color32(
                    (byte)(tint.x * 255f), (byte)(tint.y * 255f),
                    (byte)(tint.z * 255f), (byte)(tint.w * 255f));

                float cos = Mathf.Cos(p.AngleDeg * Mathf.Deg2Rad);
                float sin = Mathf.Sin(p.AngleDeg * Mathf.Deg2Rad);

                // Write all vertices for this shape into the dynamic arrays
                for (int v = 0; v < vc; v++)
                {
                    _verts[vBase + v] = RotateScale(
                        p.X, p.Y, z,
                        srcV[v].x * sx, srcV[v].y * sy,
                        cos, sin);
                    _uvs[vBase + v] = new Vector2(
                        uvRect.x + srcUV[v].x * uvRect.z,
                        uvRect.y + srcUV[v].y * uvRect.w);
                    _cols[vBase + v] = c32;
                }

                // Write triangle indices, offset by vBase so they reference
                // the correct section of the vertex arrays
                for (int t = 0; t < tc; t++)
                    _tris[tBase + t] = vBase + srcT[t];

                vBase += vc;
                tBase += tc;
                qi++;
            }

            if (qi== 0) return;mesh.Clear();
        mesh.SetVertices(_verts, 0, vBase);
        mesh.SetUVs(0, _uvs,    0, vBase);
        mesh.SetColors(_cols,    0, vBase);
        mesh.SetTriangles(_tris, 0, tBase, 0);
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
        float cx, float cy, float z, float lx, float ly, float cos, float sin)
        => new(cx + cos * lx - sin * ly,
               cy + sin * lx + cos * ly, z);

    private static Vector4 ComputeTint(ref NativeProjectile p)
    {
        float f = p.Lifetime / Mathf.Max(p.MaxLifetime, 0.0001f);
        float a = f < 0.15f ? f / 0.15f : 1f;
        return new Vector4(1f, 1f, 1f, a);
    }
}}
