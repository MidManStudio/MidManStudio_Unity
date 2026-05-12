// ProjectileShapeSO.cs
// Fixed: Diamond, Needle, and Arrow triangle winding corrected to CCW (front-face).
// Quad was already correct. Custom path unchanged (user-controlled).

using System.Collections.Generic;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    [CreateAssetMenu(
        fileName = "ProjectileShape",
        menuName  = "MidManStudio/Netcode/Projectile Shape",
        order     = 12)]
    public class ProjectileShapeSO : ScriptableObject
    {
        public enum Preset
        {
            Quad,
            Needle,
            Diamond,
            Arrow,
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

        // ── Mesh builders ─────────────────────────────────────────────────────

        public Mesh BuildMesh()
        {
            return Shape switch
            {
                Preset.Needle  => BuildNeedle(),
                Preset.Diamond => BuildDiamond(),
                Preset.Arrow   => BuildArrow(),
                Preset.Custom  => BuildCustom(),
                _              => BuildQuad(),
            };
        }

        // ── Quad — CCW ✓ (unchanged, was already correct) ────────────────────

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
                new int[] { 0,1,2, 0,2,3 },   // CCW ✓
                "ProjQuad");
        }

        // ── Needle — fixed CW→CCW (reversed each triangle) ───────────────────
        // Vertices: 0=tip(right), 1=BL, 2=TL, 3=innerBL, 4=innerTL
        // Old (CW):  0,4,2  0,3,4  0,1,3
        // Fixed(CCW):2,4,0  4,3,0  3,1,0

        private Mesh BuildNeedle()
        {
            float l = AspectRatio * 0.5f, w = 0.12f;
            return Assemble(
                new Vector3[] {
                    new( l,          0,       0),  // 0 tip
                    new(-l,         -w,       0),  // 1 BL base
                    new(-l,          w,       0),  // 2 TL base
                    new(-l * 0.7f, -w * 0.4f, 0), // 3 inner BL
                    new(-l * 0.7f,  w * 0.4f, 0), // 4 inner TL
                },
                new Vector2[] {
                    new(1, 0.5f), new(0,0), new(0,1),
                    new(0.3f, 0.25f), new(0.3f, 0.75f)
                },
                new int[] { 2,4,0,  4,3,0,  3,1,0 },   // CCW ✓
                "ProjNeedle");
        }

        // ── Diamond — fixed CW→CCW ────────────────────────────────────────────
        // Vertices: 0=right, 1=bottom, 2=left, 3=top  (original CW visual order)
        // Old (CW):  0,1,2  0,2,3
        // Fixed(CCW):0,3,2  0,2,1

        private Mesh BuildDiamond()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            return Assemble(
                new Vector3[] {
                    new( hw,  0, 0),   // 0 right
                    new(  0,-hh, 0),   // 1 bottom
                    new(-hw,  0, 0),   // 2 left
                    new(  0, hh, 0),   // 3 top
                },
                new Vector2[] {
                    new(1, 0.5f), new(0.5f, 0), new(0, 0.5f), new(0.5f, 1)
                },
                new int[] { 0,3,2,  0,2,1 },   // CCW ✓
                "ProjDiamond");
        }

        // ── Arrow — fixed CW→CCW (each triangle reversed) ────────────────────
        // Vertices: 0=tip, 1=wingBR, 2=shaftBR, 3=shaftBL, 4=shaftTL, 5=shaftTR, 6=wingTR
        // Old (CW):  0,1,2  0,2,5  0,5,6  2,3,4  2,4,5
        // Fixed(CCW):2,1,0  5,2,0  6,5,0  4,3,2  5,4,2

        private Mesh BuildArrow()
        {
            float hw    = AspectRatio * 0.5f, hh = 0.5f;
            float shaft = hw * 0.35f, shaftH = hh * 0.25f;
            return Assemble(
                new Vector3[] {
                    new( hw,          0,       0), // 0 tip
                    new( hw * 0.15f, -hh,      0), // 1 wing BR
                    new( hw * 0.15f, -shaftH,  0), // 2 shaft BR
                    new(-hw,         -shaftH,  0), // 3 shaft BL
                    new(-hw,          shaftH,  0), // 4 shaft TL
                    new( hw * 0.15f,  shaftH,  0), // 5 shaft TR
                    new( hw * 0.15f,  hh,      0), // 6 wing TR
                },
                new Vector2[] {
                    new(1,0.5f), new(0.65f,0),   new(0.65f,0.25f),
                    new(0,0.25f),new(0,0.75f),   new(0.65f,0.75f), new(0.65f,1),
                },
                new int[] { 2,1,0,  5,2,0,  6,5,0,  4,3,2,  5,4,2 },   // CCW ✓
                "ProjArrow");
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
    }
}
