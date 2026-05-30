// ProjectileShapeSO.cs
// Fixed: Diamond, Needle, Arrow winding corrected to CCW.
// NEW: Cross, Chevron, Star4, Boomerang, LetterI, LetterT, LetterL presets.
//
// Combined-mesh path in ProjectileRenderer2D must have:
//   MAX_SHAPE_VERTS = 12  (Cross / LetterI = 12 verts)
//   MAX_SHAPE_TRIS  = 30  (Cross = 10 tris × 3 = 30 indices)
//
// All shapes fire to the right (+X). The renderer rotates them to match
// projectile direction, so they look correct at any angle.

using System.Collections.Generic;
using UnityEngine;

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
            Cross,      // Plus / cross sign
            Chevron,    // Thick arrowhead ► pointing right
            Star4,      // 4-pointed star / sparkle
            Boomerang,  // Two rectangular arms at 90° (L-shape)
            LetterI,    // Capital I with serifs  (12 verts)
            LetterT,    // Capital T              ( 8 verts)
            LetterL,    // Capital L              ( 7 verts)
            Custom,
        }

        [Tooltip("Choose a built-in shape or Custom to define your own vertices.")]
        public Preset Shape = Preset.Quad;

        [Tooltip("X:Y aspect ratio. 1 = square, 2 = twice as wide, 0.5 = tall.")]
        [Range(0.1f, 8f)]
        public float AspectRatio = 2f;

        [Header("Custom shape (only when Shape = Custom)")]
        public List<Vector2> Vertices  = new();
        public List<int>     Triangles = new();
        public List<Vector2> UVs       = new();

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
                Preset.Custom    => BuildCustom(),
                _                => BuildQuad(),
            };
        }

        // ── UV helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Auto-computes UVs from vertex XY positions using the shape's bounding
        /// box [-hw, hw] × [-0.5, 0.5] → UV [0,1] × [0,1].
        /// </summary>
        private static Vector2[] AutoUV(Vector3[] verts, float hw)
        {
            float safeHW  = Mathf.Max(hw, 0.001f);
            float invDoubleHW = 0.5f / safeHW;   // = 1 / (2*hw)
            var uvs = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                uvs[i] = new Vector2(verts[i].x * invDoubleHW + 0.5f,
                                     verts[i].y + 0.5f);
            return uvs;
        }

        /// <summary>
        /// Assemble overload that computes UVs automatically from vertex positions.
        /// Use this for all new built-in shapes.
        /// </summary>
        private static Mesh Assemble(Vector3[] verts, int[] tris, float hw, string meshName)
            => Assemble(verts, AutoUV(verts, hw), tris, meshName);

        // ── Quad — CCW ✓ ──────────────────────────────────────────────────────

        private Mesh BuildQuad()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            return Assemble(
                new Vector3[] {
                    new(-hw,-hh,0), new(hw,-hh,0), new(hw,hh,0), new(-hw,hh,0)
                },
                new Vector2[] {
                    new(0,0), new(1,0), new(1,1), new(0,1)
                },
                new int[] { 0,1,2, 0,2,3 },
                "ProjQuad");
        }

        // ── Needle — CCW ✓ ────────────────────────────────────────────────────

        private Mesh BuildNeedle()
        {
            float l = AspectRatio * 0.5f, w = 0.12f;
            return Assemble(
                new Vector3[] {
                    new( l,          0,       0),
                    new(-l,         -w,       0),
                    new(-l,          w,       0),
                    new(-l * 0.7f, -w * 0.4f, 0),
                    new(-l * 0.7f,  w * 0.4f, 0),
                },
                new Vector2[] {
                    new(1, 0.5f), new(0,0), new(0,1),
                    new(0.3f, 0.25f), new(0.3f, 0.75f)
                },
                new int[] { 2,4,0,  4,3,0,  3,1,0 },
                "ProjNeedle");
        }

        // ── Diamond — CCW ✓ ───────────────────────────────────────────────────

        private Mesh BuildDiamond()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            return Assemble(
                new Vector3[] {
                    new( hw,  0, 0),
                    new(  0,-hh, 0),
                    new(-hw,  0, 0),
                    new(  0, hh, 0),
                },
                new Vector2[] {
                    new(1, 0.5f), new(0.5f, 0), new(0, 0.5f), new(0.5f, 1)
                },
                new int[] { 0,3,2,  0,2,1 },
                "ProjDiamond");
        }

        // ── Arrow — CCW ✓ ─────────────────────────────────────────────────────

        private Mesh BuildArrow()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float shaft = hw * 0.35f, shaftH = hh * 0.25f;
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
                    new(1,0.5f), new(0.65f,0),   new(0.65f,0.25f),
                    new(0,0.25f),new(0,0.75f),   new(0.65f,0.75f), new(0.65f,1),
                },
                new int[] { 2,1,0,  5,2,0,  6,5,0,  4,3,2,  5,4,2 },
                "ProjArrow");
        }

        // ── Cross — NEW — 12 verts, 10 tris (30 indices) — CCW ✓ ─────────────
        //
        //        v2  v3
        //        |    |
        // v0  v1      v4  v5
        // v11 v10     v7  v6
        //        |    |
        //        v9  v8
        //
        // Each arm is a quad; the centre square is a fifth quad.
        // Arm half-width (aw) scales with the smaller of hw / hh so the cross
        // always looks proportional regardless of aspect ratio.

        private Mesh BuildCross()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float aw = Mathf.Min(hw, hh) * 0.35f;

            var v = new Vector3[]
            {
                new(-hw,  aw, 0), // v0  left arm TL
                new(-aw,  aw, 0), // v1  centre TL
                new(-aw,  hh, 0), // v2  top arm TL
                new( aw,  hh, 0), // v3  top arm TR
                new( aw,  aw, 0), // v4  centre TR
                new( hw,  aw, 0), // v5  right arm TR
                new( hw, -aw, 0), // v6  right arm BR
                new( aw, -aw, 0), // v7  centre BR
                new( aw, -hh, 0), // v8  bottom arm BR
                new(-aw, -hh, 0), // v9  bottom arm BL
                new(-aw, -aw, 0), // v10 centre BL
                new(-hw, -aw, 0), // v11 left arm BL
            };

            // Each quad uses (BL,BR,TR),(BL,TR,TL) → CCW.
            // left (11,10,1,0) top (1,4,3,2) centre (10,7,4,1) right (7,6,5,4) bottom (9,8,7,10)
            var t = new int[]
            {
                11,10,1,   11,1, 0,   // left arm
                 1, 4,3,    1,3, 2,   // top arm
                10, 7,4,   10,4, 1,   // centre
                 7, 6,5,    7,5, 4,   // right arm
                 9, 8,7,    9,7,10,   // bottom arm
            };

            return Assemble(v, t, hw, "ProjCross");
        }

        // ── Chevron — NEW — 5 verts, 3 tris (9 indices) — CCW ✓ ─────────────
        //
        // Filled pentagon ► : BL, lower-right, tip, upper-right, TL.
        // Fan triangles from BL covering the whole shape.
        //
        // v1 (TL)
        //   \
        //    v2 (upper-right)
        //      \
        //       v3 (tip)
        //      /
        //    v4 (lower-right)
        //   /
        // v0 (BL)

        private Mesh BuildChevron()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;

            var v = new Vector3[]
            {
                new(-hw, -hh, 0), // v0 BL
                new(-hw,  hh, 0), // v1 TL
                new(  0,  hh, 0), // v2 upper-right corner
                new( hw,   0, 0), // v3 right tip
                new(  0, -hh, 0), // v4 lower-right corner
            };

            // Fan from v0: (0,4,3),(0,3,2),(0,2,1) — all CCW ✓
            var t = new int[] { 0,4,3,  0,3,2,  0,2,1 };

            return Assemble(v, t, hw, "ProjChevron");
        }

        // ── Star4 — NEW — 9 verts, 8 tris (24 indices) — CCW ✓ ──────────────
        //
        // 4-pointed star/sparkle. Outer tips on cardinal axes, inner points on
        // diagonals. Fan triangulation from centre (v8).
        //
        //        v2 (top)
        //       / \
        //   v3    v1 (inner)
        //   |      |
        //  v4    v0 (right tip)
        //   \    /
        //   v5  v7 (inner)
        //    \ /
        //    v6 (bottom)

        private Mesh BuildStar4()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float ir = Mathf.Min(hw, hh) * 0.28f; // inner-point radius

            var v = new Vector3[]
            {
                new( hw,   0, 0), // v0 right tip
                new( ir,  ir, 0), // v1 inner upper-right
                new(  0,  hh, 0), // v2 top tip
                new(-ir,  ir, 0), // v3 inner upper-left
                new(-hw,   0, 0), // v4 left tip
                new(-ir, -ir, 0), // v5 inner lower-left
                new(  0, -hh, 0), // v6 bottom tip
                new( ir, -ir, 0), // v7 inner lower-right
                new(  0,   0, 0), // v8 centre
            };

            // Fan from v8 going CCW around star (0→1→2→…→7→0) — all CCW ✓
            var t = new int[]
            {
                8,0,1,  8,1,2,  8,2,3,  8,3,4,
                8,4,5,  8,5,6,  8,6,7,  8,7,0,
            };

            return Assemble(v, t, hw, "ProjStar4");
        }

        // ── Boomerang — NEW — 7 verts, 4 tris (12 indices) — CCW ✓ ──────────
        //
        // Two rectangular arms at 90° forming an L / elbow shape.
        // Right arm goes along +X; upper arm rises along +Y from the junction.
        // `at` = arm half-thickness.
        //
        //          v5 v4
        //          |   |
        //  v6  v3  v4  ← upper arm
        //  v0  v1  v2
        //  └────────┘  ← right arm

        private Mesh BuildBoomerang()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float at = Mathf.Min(hw, hh) * 0.28f; // arm half-thickness

            var v = new Vector3[]
            {
                new(-at, -at, 0), // v0 junction BL
                new( hw, -at, 0), // v1 right arm BR
                new( hw,  at, 0), // v2 right arm TR
                new( at,  at, 0), // v3 junction inner TR
                new( at,  hh, 0), // v4 upper arm TR
                new(-at,  hh, 0), // v5 upper arm TL
                new(-at,  at, 0), // v6 junction TL
            };

            // Right arm:  BL=v0, BR=v1, TR=v2, TL=v6  → (0,1,2),(0,2,6) CCW ✓
            // Upper arm:  BL=v6, BR=v3, TR=v4, TL=v5  → (6,3,4),(6,4,5) CCW ✓
            var t = new int[]
            {
                0,1,2,  0,2,6,   // right arm
                6,3,4,  6,4,5,   // upper arm
            };

            return Assemble(v, t, hw, "ProjBoomerang");
        }

        // ── LetterI — NEW — 12 verts, 6 tris (18 indices) — CCW ✓ ───────────
        //
        // Capital I with serifs. Three quads: top serif, stem, bottom serif.
        // Serifs span the full width (hw); stem is narrow (mw).
        //
        //  v0 ██████ v1
        //     v11 v2
        //      |  |
        //     v10 v3
        //  v7 ██████ v6   ← note: I flipped labelling to keep consistent
        //     (etc)

        private Mesh BuildLetterI()
        {
            float hw   = AspectRatio * 0.5f, hh = 0.5f;
            float sw   = hw;              // serif half-width (= full extent)
            float mw   = hw * 0.28f;     // stem half-width
            float serh = hh * 0.22f;     // serif height

            float serifBot = hh - serh;  // Y of serif-stem junction

            var v = new Vector3[]
            {
                new(-sw,  hh,      0), // v0  top serif TL
                new( sw,  hh,      0), // v1  top serif TR
                new( sw,  serifBot,0), // v2  top serif BR
                new( mw,  serifBot,0), // v3  stem junction TR
                new( mw, -serifBot,0), // v4  stem junction BR
                new( sw, -serifBot,0), // v5  bottom serif TR
                new( sw, -hh,      0), // v6  bottom serif BR
                new(-sw, -hh,      0), // v7  bottom serif BL
                new(-sw, -serifBot,0), // v8  bottom serif TL
                new(-mw, -serifBot,0), // v9  stem junction BL
                new(-mw,  serifBot,0), // v10 stem junction TL
                new(-sw,  serifBot,0), // v11 top serif BL
            };

            // Top serif:    BL=v11, BR=v2, TR=v1, TL=v0  → (11,2,1),(11,1,0)
            // Stem:         BL=v9,  BR=v4, TR=v3, TL=v10 → (9,4,3),(9,3,10)
            // Bottom serif: BL=v7,  BR=v6, TR=v5, TL=v8  → (7,6,5),(7,5,8)
            // All CCW ✓ (BL,BR,TR + BL,TR,TL pattern)
            var t = new int[]
            {
                11,2,1,   11,1, 0,   // top serif
                 9,4,3,    9,3,10,   // stem
                 7,6,5,    7,5, 8,   // bottom serif
            };

            return Assemble(v, t, hw, "ProjLetterI");
        }

        // ── LetterT — NEW — 8 verts, 4 tris (12 indices) — CCW ✓ ────────────
        //
        // Capital T: full-width top bar + narrow centre stem going down.
        //
        //  v0 ██████████████ v1
        //     v7          v2
        //       v6      v3
        //        |      |
        //       v5      v4

        private Mesh BuildLetterT()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float bt = hh * 0.28f;   // top bar height (full)
            float mw = hw * 0.32f;   // stem half-width

            var v = new Vector3[]
            {
                new(-hw,  hh,    0), // v0 bar TL
                new( hw,  hh,    0), // v1 bar TR
                new( hw,  hh-bt, 0), // v2 bar BR
                new( mw,  hh-bt, 0), // v3 stem junction TR
                new( mw, -hh,    0), // v4 stem BR
                new(-mw, -hh,    0), // v5 stem BL
                new(-mw,  hh-bt, 0), // v6 stem junction TL
                new(-hw,  hh-bt, 0), // v7 bar BL
            };

            // Top bar: BL=v7,BR=v2,TR=v1,TL=v0  → (7,2,1),(7,1,0)  CCW ✓
            // Stem:    BL=v5,BR=v4,TR=v3,TL=v6  → (5,4,3),(5,3,6)  CCW ✓
            var t = new int[]
            {
                7,2,1,  7,1,0,   // top bar
                5,4,3,  5,3,6,   // stem
            };

            return Assemble(v, t, hw, "ProjLetterT");
        }

        // ── LetterL — NEW — 7 verts, 4 tris (12 indices) — CCW ✓ ────────────
        //
        // Capital L: full-height left stem + short base extending right.
        // v2 is a shared junction corner (stem BR = base BL).
        //
        //  v0 █ v1
        //     |
        //     |
        //  v3 █ v2 ██████ v6
        //         v4     v5  ← actually v4 is top of base, v5 is corner...

        private Mesh BuildLetterL()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float sw = hw * 0.38f;   // stem full width
            float bh = hh * 0.28f;  // base bar full height
            float sx = -hw + sw;     // stem right-edge X

            var v = new Vector3[]
            {
                new(-hw,  hh, 0), // v0 stem TL
                new( sx,  hh, 0), // v1 stem TR
                new( sx, -hh, 0), // v2 junction: stem BR = base inner-left bottom
                new(-hw, -hh, 0), // v3 stem BL = base far-left bottom
                new( sx, -hh+bh,0),// v4 base inner TL (top of rightward extension)
                new( hw, -hh+bh,0),// v5 base TR
                new( hw, -hh, 0), // v6 base BR
            };

            // Stem: BL=v3,BR=v2,TR=v1,TL=v0  → (3,2,1),(3,1,0)  CCW ✓
            // Base: BL=v2,BR=v6,TR=v5,TL=v4  → (2,6,5),(2,5,4)  CCW ✓
            var t = new int[]
            {
                3,2,1,  3,1,0,   // vertical stem
                2,6,5,  2,5,4,   // horizontal base extension
            };

            return Assemble(v, t, hw, "ProjLetterL");
        }

        // ── Custom ────────────────────────────────────────────────────────────

        private Mesh BuildCustom()
        {
            if (Vertices == null || Vertices.Count < 3)
            {
                Debug.LogWarning($"[ProjectileShapeSO] '{name}' Custom has < 3 vertices, falling back to quad.");
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

        private static Mesh Assemble(Vector3[] verts, Vector2[] uvs, int[] tris, string meshName)
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

        public static Vector2[] GeneratePlanarUVsPublic(List<Vector2> v) => GeneratePlanarUVs(v);

        // ─────────────────────────────────────────────────────────────────────
        //  Editor: create one asset per preset
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        [UnityEditor.MenuItem(
            "MidManStudio/Projectile System/Create Default Shape Assets",
            priority = 51)]
        public static void CreateDefaultShapes()
        {
            const string dir = "Assets/MidManStudio/ProjectileSystem/Shapes";
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            // Built-in presets — one asset per preset, default aspect ratios
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

            UnityEditor.AssetDatabase.SaveAssets();
            UnityEditor.AssetDatabase.Refresh();
            Debug.Log($"[ProjectileShapeSO] Created default shape assets in {dir}");
        }

        private static void Create(
            string dir, string assetName, System.Action<ProjectileShapeSO> cfg)
        {
            string path = $"{dir}/{assetName}.asset";
            if (System.IO.File.Exists(path)) return;
            var so = UnityEditor.AssetDatabase.LoadAssetAtPath<ProjectileShapeSO>(path)
                  ?? UnityEngine.ScriptableObject.CreateInstance<ProjectileShapeSO>();
            cfg(so);
            UnityEditor.AssetDatabase.CreateAsset(so, path);
        }
#endif
    }
}
