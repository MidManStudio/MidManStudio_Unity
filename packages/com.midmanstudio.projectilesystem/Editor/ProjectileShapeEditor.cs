// ProjectileShapeEditor.cs — Editor only
// Draws a live preview of the projectile shape in the Inspector and
// provides an optional vertex editor for the Custom preset.

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using MidManStudio.Projectiles;

[CustomEditor(typeof(ProjectileShapeSO))]
public class ProjectileShapeEditor : Editor
{
    private const float CANVAS = 160f;
    private const float GRID   = 40f;   // pixels per 0.5 world unit
    private int _selectedVert = -1;
    private bool _showVertEditor;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var so = (ProjectileShapeSO)target;

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Shape Preview", EditorStyles.boldLabel);

        // ── Canvas rect ──────────────────────────────────────────────────────
        Rect canvas = GUILayoutUtility.GetRect(CANVAS * 2, CANVAS);
        DrawGrid(canvas);
        DrawShape(canvas, so);

        if (so.Shape == ProjectileShapeSO.Preset.Custom)
        {
            EditorGUILayout.Space(4);
            _showVertEditor = EditorGUILayout.Foldout(_showVertEditor, "Vertex Editor", true);
            if (_showVertEditor)
                DrawVertexEditor(so);
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Rebuild Mesh Cache"))
        {
            so.BuildMesh();
            EditorUtility.SetDirty(so);
        }
    }

    // ─── Grid + axes ──────────────────────────────────────────────────────────

    private static void DrawGrid(Rect r)
    {
        var old = Handles.color;
        Handles.color = new Color(0.25f, 0.25f, 0.25f, 0.6f);
        EditorGUI.DrawRect(r, new Color(0.12f, 0.12f, 0.12f, 1f));

        Vector2 c = r.center;
        for (float x = -CANVAS; x <= CANVAS; x += GRID)
        {
            float px = c.x + x;
            Handles.DrawLine(new Vector3(px, r.yMin), new Vector3(px, r.yMax));
        }
        for (float y = -CANVAS; y <= CANVAS; y += GRID)
        {
            float py = c.y + y;
            Handles.DrawLine(new Vector3(r.xMin, py), new Vector3(r.xMax, py));
        }
        // Axes
        Handles.color = new Color(0.9f, 0.3f, 0.3f, 0.8f);
        Handles.DrawLine(new Vector3(c.x, r.yMin), new Vector3(c.x, r.yMax));
        Handles.color = new Color(0.3f, 0.9f, 0.3f, 0.8f);
        Handles.DrawLine(new Vector3(r.xMin, c.y), new Vector3(r.xMax, c.y));

        Handles.color = old;
    }

    // ─── Shape drawing ────────────────────────────────────────────────────────

    private static void DrawShape(Rect canvas, ProjectileShapeSO so)
    {
        Mesh m = so.BuildMesh();
        if (m == null || m.vertexCount == 0) return;

        var verts = m.vertices;
        var tris  = m.triangles;
        Vector2 c = canvas.center;

        var old = Handles.color;
        Handles.color = new Color(0.4f, 0.8f, 1f, 0.25f);

        // Fill triangles
        for (int t = 0; t < tris.Length; t += 3)
        {
            var a = WorldToCanvas(verts[tris[t]],   c);
            var b = WorldToCanvas(verts[tris[t+1]], c);
            var cc2 = WorldToCanvas(verts[tris[t+2]], c);
            Handles.DrawAAConvexPolygon(a, b, cc2);
        }

        // Outline
        Handles.color = new Color(0.4f, 0.85f, 1f, 1f);
        DrawWireframe(verts, tris, c);

        // Vertex dots
        Handles.color = Color.white;
        foreach (var v in verts)
        {
            var p = WorldToCanvas(v, c);
            Handles.DrawSolidDisc(p, Vector3.forward, 3f);
        }

        Handles.color = old;

        // Size label
        Rect label = new Rect(canvas.x + 2, canvas.yMax - 16, 200, 16);
        EditorGUI.LabelField(label,
            $"aspect {so.AspectRatio:F2}  verts {verts.Length}  tris {tris.Length/3}",
            EditorStyles.miniLabel);
    }

    private static void DrawWireframe(Vector3[] verts, int[] tris, Vector2 centre)
    {
        var drawn = new HashSet<long>();
        for (int t = 0; t < tris.Length; t += 3)
        {
            DrawEdge(tris[t],   tris[t+1], verts, centre, drawn);
            DrawEdge(tris[t+1], tris[t+2], verts, centre, drawn);
            DrawEdge(tris[t+2], tris[t],   verts, centre, drawn);
        }
    }

    private static void DrawEdge(int a, int b, Vector3[] verts, Vector2 c, HashSet<long> drawn)
    {
        long key = ((long)Mathf.Min(a,b) << 32) | (uint)Mathf.Max(a,b);
        if (!drawn.Add(key)) return;
        Handles.DrawLine(WorldToCanvas(verts[a], c), WorldToCanvas(verts[b], c));
    }

    // ─── Vertex editor (Custom preset only) ──────────────────────────────────

    private void DrawVertexEditor(ProjectileShapeSO so)
    {
        if (so.Vertices == null) so.Vertices = new List<Vector2>();
        if (so.Triangles == null) so.Triangles = new List<int>();
        if (so.UVs == null) so.UVs = new List<Vector2>();

        EditorGUI.BeginChangeCheck();

        for (int i = 0; i < so.Vertices.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.color = (i == _selectedVert) ? Color.cyan : Color.white;
                EditorGUILayout.LabelField($"V{i}", GUILayout.Width(24));
                GUI.color = Color.white;
                so.Vertices[i] = EditorGUILayout.Vector2Field("", so.Vertices[i]);
                if (GUILayout.Button("×", GUILayout.Width(20)))
                {
                    so.Vertices.RemoveAt(i);
                    if (i < so.UVs.Count) so.UVs.RemoveAt(i);
                    _selectedVert = -1;
                    EditorUtility.SetDirty(so);
                    return;
                }
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("+ Vertex"))
            {
                so.Vertices.Add(Vector2.zero);
                so.UVs.Add(Vector2.zero);
                EditorUtility.SetDirty(so);
            }
            if (GUILayout.Button("Auto Triangulate") && so.Vertices.Count >= 3)
            {
                // Simple fan triangulation from vertex 0 — works for convex polygons
                so.Triangles.Clear();
                for (int i = 1; i < so.Vertices.Count - 1; i++)
                { so.Triangles.Add(0); so.Triangles.Add(i); so.Triangles.Add(i + 1); }
                so.UVs.Clear();
                foreach (var uv in ProjectileShapeSO.GeneratePlanarUVsPublic(so.Vertices))
                    so.UVs.Add(uv);
                EditorUtility.SetDirty(so);
            }
        }

        if (EditorGUI.EndChangeCheck())
            EditorUtility.SetDirty(so);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static Vector3 WorldToCanvas(Vector3 v, Vector2 centre)
    {
        // GRID pixels per 0.5 world units → 1 world unit = GRID*2 pixels
        return new Vector3(centre.x + v.x * GRID * 2f, centre.y - v.y * GRID * 2f, 0f);
    }

    public override bool RequiresConstantRepaint() => true;
}
#endif