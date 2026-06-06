// ProjectileShapeSO.cs
// CHANGES vs previous:
//   + Preset.Formula — parametric mesh generation from X(t) / Y(t) expressions.
//     The evaluator samples _formulaSampleCount points around t ∈ [0,1) and
//     constructs a center-fan triangulation — works for any star-shaped closed
//     curve. Curve must wind CCW for correct normals (cos/sin-based curves do).
//   + _formulaX, _formulaY, _formulaSampleCount fields + public read accessors.
//   + BuildFormula() private method.
//   + CreateDefaultShapes() gets two formula examples (circle, petal-star).
//   + using MidManStudio.Projectiles.Config added for FormulaContext.
//
// Combined-mesh path limits (unchanged):
//   MAX_SHAPE_VERTS = 12 (Cross / LetterI = 12 verts)
//   MAX_SHAPE_TRIS  = 30 (Cross = 10 tris × 3 = 30 indices)

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles
{
    [CreateAssetMenu(
        fileName = "ProjectileShape",
        menuName  = "MidManStudio/Projectile System/Projectile Shape",
        order     = 12)]
    public class ProjectileShapeSO : ScriptableObject
    {
        public enum Preset
        {
            Quad,
            Needle,
            Diamond,
            Arrow,
            Cross,
            Chevron,
            Star4,
            Boomerang,
            LetterI,
            LetterT,
            LetterL,
            Custom,
            Formula,   // NEW — must remain at end to avoid breaking serialised int values
        }

        [Tooltip("Choose a built-in shape, Custom to define vertices, or Formula for parametric.")]
        public Preset Shape = Preset.Quad;

        [Tooltip("X:Y aspect ratio. 1 = square, 2 = twice as wide.")]
        [Range(0.1f, 8f)]
        public float AspectRatio = 2f;

        [Header("Custom shape (only when Shape = Custom)")]
        public List<Vector2> Vertices  = new();
        public List<int>     Triangles = new();
        public List<Vector2> UVs       = new();

        // ── Formula shape (only when Shape = Formula) ─────────────────────────
        // t ∈ [0, 1) — the curve is assumed closed (sample[0] connects back to
        // sample[n-1] via the center-fan triangulation).
        // Both formulas must wind CCW for correct normals.  cos/sin circles do. ✓

        [Header("Formula Shape (only when Shape = Formula)")]
        [Tooltip("X(t) expression. t ∈ [0,1). Variables: t, i (index 0..n-1), n.\n" +
                 "Example circle: cos(t * tau) * 0.5")]
        [SerializeField] private string _formulaX = "cos(t * tau) * 0.5";

        [Tooltip("Y(t) expression.\n" +
                 "Example circle: sin(t * tau) * 0.5")]
        [SerializeField] private string _formulaY = "sin(t * tau) * 0.5";

        [Tooltip("Number of sample points around the parametric curve.\n" +
                 "3 minimum. Higher values produce smoother curves with more vertices.")]
        [SerializeField, Range(3, 128)] private int _formulaSampleCount = 16;

        // Public read accessors — used by ProjectileShapeEditor for live validation.
        public string FormulaX           => _formulaX;
        public string FormulaY           => _formulaY;
        public int    FormulaSampleCount => _formulaSampleCount;

        // ── Runtime mesh cache ────────────────────────────────────────────────

        private Mesh _cached;

        public Mesh GetMesh()
        {
            if (_cached != null && _cached.vertexCount > 0) return _cached;
            _cached = BuildMesh();
            return _cached;
        }

        private void OnValidate() => _cached = null;

        // ── Mesh builder dispatch ─────────────────────────────────────────────

        public Mesh BuildMesh()
        {
            return Shape switch
            {
                Preset.Needle    => BuildNeedle(),
                Preset.Diamond   => BuildDiamond(),
                Preset.Arrow     => BuildArrow(),
                Preset.Cross     => BuildCross(),
                Preset.Chevron   => BuildChevron(),
                Preset.Star4     => BuildStar4(),
                Preset.Boomerang => BuildBoomerang(),
                Preset.LetterI   => BuildLetterI(),
                Preset.LetterT   => BuildLetterT(),
                Preset.LetterL   => BuildLetterL(),
                Preset.Formula   => BuildFormula(),
                Preset.Custom    => BuildCustom(),
                _                => BuildQuad(),
            };
        }

        // ── UV helpers ────────────────────────────────────────────────────────

        private static Vector2[] AutoUV(Vector3[] verts, float hw)
        {
            float safeHW     = Mathf.Max(hw, 0.001f);
            float invDoubleHW = 0.5f / safeHW;
            var   uvs         = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                uvs[i] = new Vector2(verts[i].x * invDoubleHW + 0.5f,
                                     verts[i].y + 0.5f);
            return uvs;
        }

        private static Mesh Assemble(Vector3[] verts, int[] tris, float hw, string meshName)
            => Assemble(verts, AutoUV(verts, hw), tris, meshName);

        // ── Quad ──────────────────────────────────────────────────────────────

        private Mesh BuildQuad()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            return Assemble(
                new Vector3[] {
                    new(-hw,-hh,0), new(hw,-hh,0), new(hw,hh,0), new(-hw,hh,0)
                },
                new Vector2[] { new(0,0), new(1,0), new(1,1), new(0,1) },
                new int[]     { 0,1,2, 0,2,3 },
                "ProjQuad");
        }

        // ── Needle ────────────────────────────────────────────────────────────

        private Mesh BuildNeedle()
        {
            float l = AspectRatio * 0.5f, w = 0.12f;
            return Assemble(
                new Vector3[] {
                    new( l, 0, 0), new(-l, -w, 0), new(-l, w, 0),
                    new(-l * 0.7f, -w * 0.4f, 0), new(-l * 0.7f, w * 0.4f, 0),
                },
                new Vector2[] {
                    new(1,0.5f), new(0,0), new(0,1),
                    new(0.3f,0.25f), new(0.3f,0.75f)
                },
                new int[] { 2,4,0, 4,3,0, 3,1,0 },
                "ProjNeedle");
        }

        // ── Diamond ───────────────────────────────────────────────────────────

        private Mesh BuildDiamond()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            return Assemble(
                new Vector3[] {
                    new( hw, 0, 0), new(0,-hh, 0), new(-hw, 0, 0), new(0, hh, 0),
                },
                new Vector2[] {
                    new(1,0.5f), new(0.5f,0), new(0,0.5f), new(0.5f,1)
                },
                new int[] { 0,3,2, 0,2,1 },
                "ProjDiamond");
        }

        // ── Arrow ─────────────────────────────────────────────────────────────

        private Mesh BuildArrow()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float shaftH = hh * 0.25f;
            return Assemble(
                new Vector3[] {
                    new( hw,          0,       0),
                    new( hw * 0.15f, -hh,      0),
                    new( hw * 0.15f, -shaftH,  0),
                    new(-hw,         -shaftH,  0),
                    new(-hw,          shaftH,  0),
                    new( hw * 0.15f,  shaftH,  0),
                    new( hw * 0.15f,  hh,      0),
                },
                new Vector2[] {
                    new(1,0.5f), new(0.65f,0), new(0.65f,0.25f),
                    new(0,0.25f), new(0,0.75f), new(0.65f,0.75f), new(0.65f,1),
                },
                new int[] { 2,1,0, 5,2,0, 6,5,0, 4,3,2, 5,4,2 },
                "ProjArrow");
        }

        // ── Cross ─────────────────────────────────────────────────────────────

        private Mesh BuildCross()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float aw = Mathf.Min(hw, hh) * 0.35f;
            var v = new Vector3[]
            {
                new(-hw,  aw, 0), new(-aw,  aw, 0), new(-aw,  hh, 0),
                new( aw,  hh, 0), new( aw,  aw, 0), new( hw,  aw, 0),
                new( hw, -aw, 0), new( aw, -aw, 0), new( aw, -hh, 0),
                new(-aw, -hh, 0), new(-aw, -aw, 0), new(-hw, -aw, 0),
            };
            var t = new int[]
            {
                11,10,1,  11,1, 0,
                 1, 4,3,   1,3, 2,
                10, 7,4,  10,4, 1,
                 7, 6,5,   7,5, 4,
                 9, 8,7,   9,7,10,
            };
            return Assemble(v, t, hw, "ProjCross");
        }

        // ── Chevron ───────────────────────────────────────────────────────────

        private Mesh BuildChevron()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            var v = new Vector3[]
            {
                new(-hw, -hh, 0), new(-hw,  hh, 0),
                new(  0,  hh, 0), new( hw,   0, 0), new(  0, -hh, 0),
            };
            return Assemble(v, new int[] { 0,4,3, 0,3,2, 0,2,1 }, hw, "ProjChevron");
        }

        // ── Star4 ─────────────────────────────────────────────────────────────

        private Mesh BuildStar4()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float ir = Mathf.Min(hw, hh) * 0.28f;
            var v = new Vector3[]
            {
                new( hw,   0, 0), new( ir,  ir, 0), new(  0,  hh, 0),
                new(-ir,  ir, 0), new(-hw,   0, 0), new(-ir, -ir, 0),
                new(  0, -hh, 0), new( ir, -ir, 0), new(  0,   0, 0),
            };
            var t = new int[]
            {
                8,0,1, 8,1,2, 8,2,3, 8,3,4,
                8,4,5, 8,5,6, 8,6,7, 8,7,0,
            };
            return Assemble(v, t, hw, "ProjStar4");
        }

        // ── Boomerang ─────────────────────────────────────────────────────────

        private Mesh BuildBoomerang()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float at = Mathf.Min(hw, hh) * 0.28f;
            var v = new Vector3[]
            {
                new(-at, -at, 0), new( hw, -at, 0), new( hw,  at, 0),
                new( at,  at, 0), new( at,  hh, 0), new(-at,  hh, 0), new(-at,  at, 0),
            };
            return Assemble(v,
                new int[] { 0,1,2, 0,2,6, 6,3,4, 6,4,5 }, hw, "ProjBoomerang");
        }

        // ── LetterI ───────────────────────────────────────────────────────────

        private Mesh BuildLetterI()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float sw = hw, mw = hw * 0.28f, serh = hh * 0.22f;
            float serifBot = hh - serh;
            var v = new Vector3[]
            {
                new(-sw,  hh,       0), new( sw,  hh,       0),
                new( sw,  serifBot, 0), new( mw,  serifBot, 0),
                new( mw, -serifBot, 0), new( sw, -serifBot, 0),
                new( sw, -hh,       0), new(-sw, -hh,       0),
                new(-sw, -serifBot, 0), new(-mw, -serifBot, 0),
                new(-mw,  serifBot, 0), new(-sw,  serifBot, 0),
            };
            var t = new int[]
            {
                11,2,1, 11,1, 0,
                 9,4,3,  9,3,10,
                 7,6,5,  7,5, 8,
            };
            return Assemble(v, t, hw, "ProjLetterI");
        }

        // ── LetterT ───────────────────────────────────────────────────────────

        private Mesh BuildLetterT()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float bt = hh * 0.28f, mw = hw * 0.32f;
            var v = new Vector3[]
            {
                new(-hw,  hh,    0), new( hw,  hh,    0),
                new( hw,  hh-bt, 0), new( mw,  hh-bt, 0),
                new( mw, -hh,    0), new(-mw, -hh,    0),
                new(-mw,  hh-bt, 0), new(-hw,  hh-bt, 0),
            };
            return Assemble(v,
                new int[] { 7,2,1, 7,1,0, 5,4,3, 5,3,6 }, hw, "ProjLetterT");
        }

        // ── LetterL ───────────────────────────────────────────────────────────

        private Mesh BuildLetterL()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float sw = hw * 0.38f, bh = hh * 0.28f;
            float sx = -hw + sw;
            var v = new Vector3[]
            {
                new(-hw,  hh,    0), new( sx,  hh,    0),
                new( sx, -hh,    0), new(-hw, -hh,    0),
                new( sx, -hh+bh, 0), new( hw, -hh+bh, 0), new( hw, -hh, 0),
            };
            return Assemble(v,
                new int[] { 3,2,1, 3,1,0, 2,6,5, 2,5,4 }, hw, "ProjLetterL");
        }

        // ── Formula ── NEW ─────────────────────────────────────────────────────
        // Samples _formulaX(t) and _formulaY(t) for t ∈ [0,1) to produce
        // _formulaSampleCount perimeter vertices, then adds a center vertex
        // and builds a center-fan triangulation (n triangles).
        //
        // Winding: the curve must go CCW for normals to face forward (+Z).
        // cos(t*tau), sin(t*tau) goes CCW — all trig-based circles do. ✓
        //
        // Non-convex shapes: fan triangulation from center works for any
        // star-shaped region (every perimeter point visible from the center).
        // For concave shapes use the Custom preset with manual triangulation.

        private Mesh BuildFormula()
        {
            if (string.IsNullOrWhiteSpace(_formulaX) || string.IsNullOrWhiteSpace(_formulaY))
            {
                Debug.LogWarning(
                    $"[ProjectileShapeSO] '{name}' Formula preset has empty X or Y — falling back to quad.");
                return BuildQuad();
            }

            int    n      = Mathf.Clamp(_formulaSampleCount, 3, 128);
            var    perim  = new Vector3[n];
            float  maxAbs = 0.001f;
            string err    = null;

            for (int idx = 0; idx < n; idx++)
            {
                float t   = (float)idx / n;
                var   ctx = new FormulaContext { t = t, i = idx, n = n };

                float x = MathFormulaEvaluator.Evaluate(_formulaX, ctx, out err);
                if (err != null) break;
                float y = MathFormulaEvaluator.Evaluate(_formulaY, ctx, out err);
                if (err != null) break;

                perim[idx] = new Vector3(x, y, 0f);
                maxAbs     = Mathf.Max(maxAbs, Mathf.Abs(x), Mathf.Abs(y));
            }

            if (err != null)
            {
                Debug.LogWarning($"[ProjectileShapeSO] '{name}' Formula error: {err}");
                return BuildQuad();
            }

            // Mesh: verts[0] = center, verts[1..n] = perimeter
            var allVerts = new Vector3[n + 1];
            allVerts[0]  = Vector3.zero;
            for (int i = 0; i < n; i++) allVerts[i + 1] = perim[i];

            // Center-fan: n triangles (center, perim[i], perim[(i+1)%n])
            var tris = new int[n * 3];
            for (int i = 0; i < n; i++)
            {
                tris[i * 3]     = 0;
                tris[i * 3 + 1] = i + 1;
                tris[i * 3 + 2] = (i + 1) % n + 1;
            }

            return Assemble(allVerts, tris, maxAbs, "ProjFormula");
        }

        // ── Custom ────────────────────────────────────────────────────────────

        private Mesh BuildCustom()
        {
            if (Vertices == null || Vertices.Count < 3)
            {
                Debug.LogWarning(
                    $"[ProjectileShapeSO] '{name}' Custom has < 3 vertices, falling back to quad.");
                return BuildQuad();
            }
            var v3 = new Vector3[Vertices.Count];
            for (int i = 0; i < Vertices.Count; i++)
                v3[i] = new Vector3(Vertices[i].x, Vertices[i].y, 0f);

            var uvArr = (UVs != null && UVs.Count == Vertices.Count)
                ? UVs.ToArray()
                : GeneratePlanarUVs(Vertices);

            return Assemble(v3, uvArr, Triangles.ToArray(), "ProjCustom");
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        private static Mesh Assemble(
            Vector3[] verts, Vector2[] uvs, int[] tris, string meshName)
        {
            var m = new Mesh { name = meshName };
            m.vertices  = verts;
            m.uv        = uvs;
            m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
            m.UploadMeshData(false);
            return m;
        }

        private static Vector2[] GeneratePlanarUVs(List<Vector2> verts)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var v in verts)
            {
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
            }
            float rw = maxX - minX; if (rw < 0.0001f) rw = 1f;
            float rh = maxY - minY; if (rh < 0.0001f) rh = 1f;
            var uvs = new Vector2[verts.Count];
            for (int i = 0; i < verts.Count; i++)
                uvs[i] = new Vector2((verts[i].x - minX) / rw, (verts[i].y - minY) / rh);
            return uvs;
        }

        public static Vector2[] GeneratePlanarUVsPublic(List<Vector2> v)
            => GeneratePlanarUVs(v);

        // ── Editor: create default shape assets ───────────────────────────────

#if UNITY_EDITOR
        [UnityEditor.MenuItem(
            "MidManStudio/Projectile System/Create Default Shape Assets",
            priority = 51)]
        public static void CreateDefaultShapes()
        {
            const string dir = "Assets/MidManStudio/ProjectileSystem/Shapes";
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            Create(dir, "Quad_Default",       p => { p.Shape = Preset.Quad;     p.AspectRatio = 2f; });
            Create(dir, "Needle_Default",     p => { p.Shape = Preset.Needle;   p.AspectRatio = 3f; });
            Create(dir, "Diamond_Default",    p => { p.Shape = Preset.Diamond;  p.AspectRatio = 1.5f; });
            Create(dir, "Arrow_Default",      p => { p.Shape = Preset.Arrow;    p.AspectRatio = 2f; });
            Create(dir, "Cross_Default",      p => { p.Shape = Preset.Cross;    p.AspectRatio = 1f; });
            Create(dir, "Chevron_Default",    p => { p.Shape = Preset.Chevron;  p.AspectRatio = 1.5f; });
            Create(dir, "Star4_Default",      p => { p.Shape = Preset.Star4;    p.AspectRatio = 1f; });
            Create(dir, "Boomerang_Default",  p => { p.Shape = Preset.Boomerang;p.AspectRatio = 1.5f; });
            Create(dir, "LetterI_Default",    p => { p.Shape = Preset.LetterI;  p.AspectRatio = 0.6f; });
            Create(dir, "LetterT_Default",    p => { p.Shape = Preset.LetterT;  p.AspectRatio = 1.4f; });
            Create(dir, "LetterL_Default",    p => { p.Shape = Preset.LetterL;  p.AspectRatio = 1.2f; });

            // Formula presets — circle and 5-petal star
            Create(dir, "Formula_Circle_16", p =>
            {
                p.Shape               = Preset.Formula;
                p.AspectRatio         = 1f;
                p._formulaX           = "cos(t * tau) * 0.5";
                p._formulaY           = "sin(t * tau) * 0.5";
                p._formulaSampleCount = 16;
            });
            Create(dir, "Formula_PetalStar_64", p =>
            {
                p.Shape               = Preset.Formula;
                p.AspectRatio         = 1f;
                p._formulaX           = "cos(t * tau) * (0.5 + 0.15 * cos(t * tau * 5))";
                p._formulaY           = "sin(t * tau) * (0.5 + 0.15 * cos(t * tau * 5))";
                p._formulaSampleCount = 64;
            });

            UnityEditor.AssetDatabase.SaveAssets();
            UnityEditor.AssetDatabase.Refresh();
            Debug.Log($"[ProjectileShapeSO] Created default shape assets in {dir}");
        }

        private static void Create(
            string dir, string assetName,
            System.Action<ProjectileShapeSO> cfg)
        {
            string path = $"{dir}/{assetName}.asset";
            if (System.IO.File.Exists(path)) return;
            var so = UnityEditor.AssetDatabase.LoadAssetAtPath<ProjectileShapeSO>(path)
                  ?? ScriptableObject.CreateInstance<ProjectileShapeSO>();
            cfg(so);
            UnityEditor.AssetDatabase.CreateAsset(so, path);
        }
#endif
    }
}
