// ProjectileShapeSO.cs
// ScriptableObject that defines the mesh shape used to render a projectile type.
// Reference from ProjectileConfigSO.CustomShape — leave null for default quad.

using System.Collections.Generic;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    [CreateAssetMenu(
        fileName = "ProjectileShape",
        menuName  = "MidMan/Projectile Shape",
        order     = 11)]
    public class ProjectileShapeSO : ScriptableObject
    {
        public enum Preset
        {
            Quad,     // plain square
            Needle,   // long thin triangle pointing right
            Diamond,  // rhombus
            Arrow,    // arrowhead
            Custom,   // user-defined vertices below
        }

        [Tooltip("Choose a built-in shape or Custom to define your own vertices.")]
        public Preset Shape = Preset.Quad;

        [Tooltip("X:Y aspect ratio applied to all presets (1 = square, 2 = twice as wide, 0.5 = tall).")]
        [Range(0.1f, 8f)]
        public float AspectRatio = 2f;   // most projectiles look better wider than tall

        [Header("Custom shape (only used when Shape = Custom)")]
        [Tooltip("Vertices in local normalised space. Keep within roughly -0.5 to 0.5.")]
        public List<Vector2> Vertices = new();
        [Tooltip("Triangle indices into Vertices list (3 per triangle).")]
        public List<int>     Triangles = new();
        [Tooltip("UV for each vertex (parallel to Vertices list).")]
        public List<Vector2> UVs = new();

        // ─── Runtime mesh cache ───────────────────────────────────────────────
        // Built once per SO, reused across all projectiles of this type.
        private Mesh _cached;

        public Mesh GetMesh()
        {
            if (_cached != null && _cached.vertexCount > 0) return _cached;
            _cached = BuildMesh();
            return _cached;
        }

        private void OnValidate() => _cached = null; // re-bake on change

        // ─── Mesh builders ────────────────────────────────────────────────────

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

        private Mesh BuildNeedle()
        {
            // Thin triangle: tip at right, base on left
            float l = AspectRatio * 0.5f, w = 0.12f;
            return Assemble(
                new Vector3[] {
                    new(l,   0,   0),  // tip
                    new(-l, -w,  0),   // BL base
                    new(-l,  w,  0),   // TL base
                    new(-l*0.7f, -w*0.4f, 0), // inner BL for UV
                    new(-l*0.7f,  w*0.4f, 0), // inner TL for UV
                },
                new Vector2[] { new(1,0.5f), new(0,0), new(0,1), new(0.3f,0.25f), new(0.3f,0.75f) },
                new int[] { 0,4,2, 0,3,4, 0,1,3 },
                "ProjNeedle");
        }

        private Mesh BuildDiamond()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            return Assemble(
                new Vector3[] {
                    new(hw,  0, 0),   // right
                    new(0,  -hh, 0),  // bottom
                    new(-hw, 0, 0),   // left
                    new(0,   hh, 0),  // top
                },
                new Vector2[] { new(1,0.5f), new(0.5f,0), new(0,0.5f), new(0.5f,1) },
                new int[] { 0,1,2, 0,2,3 },
                "ProjDiamond");
        }

        private Mesh BuildArrow()
        {
            float hw = AspectRatio * 0.5f, hh = 0.5f;
            float shaft = hw * 0.35f, shaftH = hh * 0.25f;
            return Assemble(
                new Vector3[] {
                    new( hw,  0,    0),         // 0 tip
                    new( hw*0.15f, -hh,  0),    // 1 wing BR
                    new( hw*0.15f, -shaftH, 0), // 2 shaft BR
                    new(-hw,       -shaftH, 0), // 3 shaft BL
                    new(-hw,        shaftH, 0), // 4 shaft TL
                    new( hw*0.15f,  shaftH, 0), // 5 shaft TR
                    new( hw*0.15f,  hh,   0),   // 6 wing TR
                },
                new Vector2[] {
                    new(1,0.5f), new(0.65f,0), new(0.65f,0.25f),
                    new(0,0.25f), new(0,0.75f), new(0.65f,0.75f), new(0.65f,1),
                },
                new int[] { 0,1,2, 0,2,5, 0,5,6, 2,3,4, 2,4,5 },
                "ProjArrow");
        }

        private Mesh BuildCustom()
        {
            if (Vertices == null || Vertices.Count < 3)
            {
                Debug.LogWarning($"[ProjectileShapeSO] '{name}' Custom shape has < 3 vertices, falling back to quad.");
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

        private static Mesh Assemble(Vector3[] verts, Vector2[] uvs, int[] tris, string meshName)
        {
            var m = new Mesh { name = meshName };
            m.vertices  = verts;
            m.uv        = uvs;
            m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
            m.UploadMeshData(false); // keep readable for editor handles
            return m;
        }

        private static Vector2[] GeneratePlanarUVs(List<Vector2> verts)
        {
            // Simple planar UV: remap bounding box to [0,1]
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var v in verts) { minX = Mathf.Min(minX,v.x); maxX = Mathf.Max(maxX,v.x); minY = Mathf.Min(minY,v.y); maxY = Mathf.Max(maxY,v.y); }
            float rw = maxX - minX, rh = maxY - minY;
            if (rw < 0.0001f) rw = 1f; if (rh < 0.0001f) rh = 1f;
            var uvs = new Vector2[verts.Count];
            for (int i = 0; i < verts.Count; i++)
                uvs[i] = new Vector2((verts[i].x - minX) / rw, (verts[i].y - minY) / rh);
            return uvs;
        }
// Add inside ProjectileShapeSO class body, next to GeneratePlanarUVs:
public static Vector2[] GeneratePlanarUVsPublic(List<Vector2> v) => GeneratePlanarUVs(v);

    }
}