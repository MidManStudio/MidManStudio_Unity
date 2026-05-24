// ProjectileShapeEditor.cs — COMPLETE REWRITE
//
// CHANGES:
//   + Canvas is now fully interactive — drag vertices directly in the preview.
//     Previously the canvas was draw-only; vertex editing was only in the list below.
//   + Grid with configurable spacing drawn in canvas. Grid lines every _gridSpacing
//     world units. Axis lines drawn in red/green.
//   + Snap to grid: when _snapEnabled, dragged vertices snap to _snapIncrement
//     world-unit intervals (default 0.1). Snap is per-axis independently.
//   + Selected vertex: highlighted in cyan with crosshair lines + coordinate label.
//   + Right-click on vertex → delete it (no button needed).
//   + Click empty canvas space (in Add mode) → appends new vertex at clicked position.
//   + Toolbar row: [Add Vertex toggle] [Snap toggle] [Snap size field] [Grid spacing]
//   + Hover label: shows world-space coordinates of mouse position in canvas.
//   + The vertex list below still shows for precise numeric editing.
//   + Auto-triangulate uses fan from vertex 0 (convex only) — unchanged.
//   + "Rebuild Mesh Cache" unchanged.
//   + RequiresConstantRepaint() = true so hover label updates smoothly.

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Projectiles.EditorTools
{
    [CustomEditor(typeof(ProjectileShapeSO))]
    public class ProjectileShapeEditor : Editor
    {
        // ── Canvas layout ──────────────────────────────────────────────────────
        private const float CANVAS_HEIGHT  = 220f;
        private const float PIXELS_PER_UNIT = 80f;  // screen pixels per world unit

        // ── Grid / snap settings (persisted per-editor session) ───────────────
        private float _gridSpacing   = 0.5f;   // world units between grid lines
        private float _snapIncrement = 0.1f;   // world units snap step
        private bool  _snapEnabled   = true;
        private bool  _addMode       = false;  // click canvas to add vertex

        // ── Interaction state ─────────────────────────────────────────────────
        private int     _selectedVert = -1;
        private int     _draggingVert = -1;
        private bool    _isDragging   = false;
        private Vector2 _dragStartWorld;

        // ── Colours ───────────────────────────────────────────────────────────
        private static readonly Color ColBackground = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color ColGrid       = new Color(0.28f, 0.28f, 0.28f, 0.8f);
        private static readonly Color ColAxisX      = new Color(0.85f, 0.30f, 0.30f, 0.9f);
        private static readonly Color ColAxisY      = new Color(0.30f, 0.85f, 0.30f, 0.9f);
        private static readonly Color ColMeshFill   = new Color(0.30f, 0.65f, 1.00f, 0.20f);
        private static readonly Color ColMeshEdge   = new Color(0.40f, 0.80f, 1.00f, 1.00f);
        private static readonly Color ColVert       = new Color(1.00f, 0.85f, 0.20f, 1.00f);
        private static readonly Color ColVertSel    = new Color(0.20f, 1.00f, 0.90f, 1.00f);
        private static readonly Color ColVertHover  = new Color(1.00f, 1.00f, 0.55f, 1.00f);
        private static readonly Color ColCrosshair  = new Color(0.20f, 1.00f, 0.90f, 0.50f);
        private static readonly Color ColAddMode    = new Color(0.30f, 1.00f, 0.45f, 0.25f);

        private const float VERT_RADIUS       = 6f;   // px, hit-test radius
        private const float VERT_RADIUS_DRAW  = 5f;   // px, visual radius

        // ─────────────────────────────────────────────────────────────────────

        private Rect _canvasRect;

        public override void OnInspectorGUI()
        {
            var so = (ProjectileShapeSO)target;
            serializedObject.Update();

            // ── Standard fields ────────────────────────────────────────────────
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Shape"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("AspectRatio"));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                so.BuildMesh();
                EditorUtility.SetDirty(so);
            }

            EditorGUILayout.Space(6);

            // ── Canvas toolbar ─────────────────────────────────────────────────
            DrawToolbar(so);

            // ── Interactive canvas ─────────────────────────────────────────────
            EditorGUILayout.LabelField("Shape Preview  (drag vertices, right-click to delete)",
                EditorStyles.boldLabel);

            _canvasRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(CANVAS_HEIGHT));

            if (Event.current.type == EventType.Repaint)
            {
                DrawBackground();
                DrawGrid();
                DrawAxes();
                DrawMesh(so);
                if (_addMode) DrawAddModeOverlay();
                DrawVertices(so);
                DrawHoverCoord();
            }

            HandleCanvasInput(so);

            // ── Vertex list (numeric editing) ──────────────────────────────────
            if (so.Shape == ProjectileShapeSO.Preset.Custom)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Vertices (also editable by dragging in canvas)",
                    EditorStyles.miniBoldLabel);

                DrawVertexList(so);

                EditorGUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Auto-Triangulate (convex)") && so.Vertices?.Count >= 3)
                        AutoTriangulate(so);

                    if (GUILayout.Button("Normalize UVs") && so.Vertices?.Count >= 3)
                        NormalizeUVs(so);
                }
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("Rebuild Mesh Cache", GUILayout.Height(22)))
            {
                so.BuildMesh();
                EditorUtility.SetDirty(so);
            }

            serializedObject.ApplyModifiedProperties();
        }

        // ── Toolbar ───────────────────────────────────────────────────────────

        private void DrawToolbar(ProjectileShapeSO so)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                // Add mode toggle
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = _addMode
                    ? new Color(0.3f, 1f, 0.45f) : Color.white;
                if (GUILayout.Toggle(_addMode, "✚ Add Vertex",
                    EditorStyles.toolbarButton, GUILayout.Width(90)))
                {
                    _addMode = !_addMode;
                    if (_addMode) _selectedVert = -1;
                }
                GUI.backgroundColor = oldBg;

                GUILayout.Space(6);

                // Snap toggle
                _snapEnabled = GUILayout.Toggle(_snapEnabled, "Snap",
                    EditorStyles.toolbarButton, GUILayout.Width(44));

                if (_snapEnabled)
                {
                    GUILayout.Label("Step:", EditorStyles.miniBoldLabel, GUILayout.Width(30));
                    _snapIncrement = EditorGUILayout.FloatField(
                        _snapIncrement, EditorStyles.toolbarTextField,
                        GUILayout.Width(36));
                    _snapIncrement = Mathf.Max(0.01f, _snapIncrement);
                }

                GUILayout.Space(6);

                GUILayout.Label("Grid:", EditorStyles.miniBoldLabel, GUILayout.Width(28));
                _gridSpacing = EditorGUILayout.FloatField(
                    _gridSpacing, EditorStyles.toolbarTextField, GUILayout.Width(36));
                _gridSpacing = Mathf.Max(0.1f, _gridSpacing);

                GUILayout.FlexibleSpace();

                // Selection info
                if (_selectedVert >= 0 && so.Vertices != null
                    && _selectedVert < so.Vertices.Count)
                {
                    var v = so.Vertices[_selectedVert];
                    GUILayout.Label($"V{_selectedVert}: ({v.x:F2}, {v.y:F2})",
                        EditorStyles.miniLabel);
                }
            }
        }

        // ── Drawing ───────────────────────────────────────────────────────────

        private void DrawBackground()
        {
            EditorGUI.DrawRect(_canvasRect, ColBackground);
        }

        private void DrawGrid()
        {
            Handles.color = ColGrid;
            var center    = _canvasRect.center;

            // How many lines fit in the canvas
            float halfW = _canvasRect.width  * 0.5f;
            float halfH = _canvasRect.height * 0.5f;

            float worldHalfW = halfW / PIXELS_PER_UNIT;
            float worldHalfH = halfH / PIXELS_PER_UNIT;

            // Vertical lines
            float startX = Mathf.Ceil(-worldHalfW / _gridSpacing) * _gridSpacing;
            for (float wx = startX; wx <= worldHalfW; wx += _gridSpacing)
            {
                if (Mathf.Abs(wx) < 0.001f) continue; // axis drawn separately
                Vector2 top = WorldToCanvas(new Vector2(wx,  worldHalfH));
                Vector2 bot = WorldToCanvas(new Vector2(wx, -worldHalfH));
                Handles.DrawLine(top, bot);
            }

            // Horizontal lines
            float startY = Mathf.Ceil(-worldHalfH / _gridSpacing) * _gridSpacing;
            for (float wy = startY; wy <= worldHalfH; wy += _gridSpacing)
            {
                if (Mathf.Abs(wy) < 0.001f) continue;
                Vector2 left  = WorldToCanvas(new Vector2(-worldHalfW, wy));
                Vector2 right = WorldToCanvas(new Vector2( worldHalfW, wy));
                Handles.DrawLine(left, right);
            }
        }

        private void DrawAxes()
        {
            var center = _canvasRect.center;
            float halfW = _canvasRect.width  * 0.5f / PIXELS_PER_UNIT;
            float halfH = _canvasRect.height * 0.5f / PIXELS_PER_UNIT;

            // X axis (red)
            Handles.color = ColAxisX;
            Handles.DrawLine(
                WorldToCanvas(new Vector2(-halfW, 0f)),
                WorldToCanvas(new Vector2( halfW, 0f)));

            // Y axis (green)
            Handles.color = ColAxisY;
            Handles.DrawLine(
                WorldToCanvas(new Vector2(0f, -halfH)),
                WorldToCanvas(new Vector2(0f,  halfH)));
        }

        private void DrawMesh(ProjectileShapeSO so)
        {
            Mesh m = so.BuildMesh();
            if (m == null || m.vertexCount == 0) return;

            var verts = m.vertices;
            var tris  = m.triangles;

            // Fill
            Handles.color = ColMeshFill;
            for (int t = 0; t < tris.Length; t += 3)
            {
                var a  = (Vector3)WorldToCanvas(verts[tris[t    ]]);
                var b  = (Vector3)WorldToCanvas(verts[tris[t + 1]]);
                var c2 = (Vector3)WorldToCanvas(verts[tris[t + 2]]);
                Handles.DrawAAConvexPolygon(a, b, c2);
            }

            // Outline
            Handles.color = ColMeshEdge;
            var drawn = new HashSet<long>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                DrawEdgeOnce(verts, tris[t],     tris[t + 1], drawn);
                DrawEdgeOnce(verts, tris[t + 1], tris[t + 2], drawn);
                DrawEdgeOnce(verts, tris[t + 2], tris[t],     drawn);
            }
        }

        private void DrawEdgeOnce(Vector3[] verts, int a, int b, HashSet<long> drawn)
        {
            long key = ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
            if (!drawn.Add(key)) return;
            Handles.DrawLine(WorldToCanvas(verts[a]), WorldToCanvas(verts[b]));
        }

        private void DrawVertices(ProjectileShapeSO so)
        {
            if (so.Shape != ProjectileShapeSO.Preset.Custom) return;
            if (so.Vertices == null || so.Vertices.Count == 0) return;

            Vector2 mouse = Event.current.mousePosition;

            for (int i = 0; i < so.Vertices.Count; i++)
            {
                Vector2 screenPos = WorldToCanvas(so.Vertices[i]);
                bool    isSelected = (i == _selectedVert);
                bool    isHovered  = !_isDragging
                    && Vector2.Distance(mouse, screenPos) < VERT_RADIUS + 4f
                    && _canvasRect.Contains(mouse);
                bool    isDragged  = (i == _draggingVert && _isDragging);

                // Crosshair for selected vertex
                if (isSelected)
                {
                    Handles.color = ColCrosshair;
                    float crossLen = 12f;
                    Handles.DrawLine(
                        new Vector2(screenPos.x - crossLen, screenPos.y),
                        new Vector2(screenPos.x + crossLen, screenPos.y));
                    Handles.DrawLine(
                        new Vector2(screenPos.x, screenPos.y - crossLen),
                        new Vector2(screenPos.x, screenPos.y + crossLen));
                }

                // Vertex dot
                Color col = isSelected || isDragged ? ColVertSel
                          : isHovered              ? ColVertHover
                          : ColVert;
                Handles.color = col;
                Handles.DrawSolidDisc(screenPos, Vector3.forward, VERT_RADIUS_DRAW);

                // Index label
                var labelRect = new Rect(
                    screenPos.x + VERT_RADIUS_DRAW + 2f,
                    screenPos.y - 8f, 40f, 16f);
                var labelStyle = new GUIStyle(EditorStyles.miniLabel)
                    { normal = { textColor = col } };
                GUI.Label(labelRect, $"V{i}", labelStyle);
            }
        }

        private void DrawAddModeOverlay()
        {
            EditorGUI.DrawRect(_canvasRect, ColAddMode);
            var center = _canvasRect.center;
            GUI.Label(new Rect(center.x - 80f, _canvasRect.yMin + 4f, 160f, 18f),
                "Click to add vertex", EditorStyles.centeredGreyMiniLabel);
        }

        private void DrawHoverCoord()
        {
            Vector2 mouse = Event.current.mousePosition;
            if (!_canvasRect.Contains(mouse)) return;

            Vector2 world = CanvasToWorld(mouse);
            Vector2 snapped = _snapEnabled
                ? SnapToGrid(world) : world;

            string label = _snapEnabled
                ? $"({snapped.x:F2}, {snapped.y:F2})  [snapped]"
                : $"({world.x:F2}, {world.y:F2})";

            var rect = new Rect(
                _canvasRect.xMin + 4f,
                _canvasRect.yMax - 18f,
                200f, 16f);
            GUI.Label(rect, label, EditorStyles.miniLabel);
        }

        // ── Input handling ────────────────────────────────────────────────────

        private void HandleCanvasInput(ProjectileShapeSO so)
        {
            if (so.Shape != ProjectileShapeSO.Preset.Custom) return;
            if (so.Vertices == null) so.Vertices = new List<Vector2>();

            Event e = Event.current;
            if (!_canvasRect.Contains(e.mousePosition)) return;

            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0:
                    HandleLeftMouseDown(so, e);
                    break;

                case EventType.MouseDown when e.button == 1:
                    HandleRightMouseDown(so, e);
                    break;

                case EventType.MouseDrag when e.button == 0:
                    HandleMouseDrag(so, e);
                    break;

                case EventType.MouseUp when e.button == 0:
                    HandleMouseUp(so, e);
                    break;
            }
        }

        private void HandleLeftMouseDown(ProjectileShapeSO so, Event e)
        {
            Vector2 mouse = e.mousePosition;

            if (_addMode)
            {
                // Add vertex at clicked position
                Vector2 world   = CanvasToWorld(mouse);
                Vector2 snapped = _snapEnabled ? SnapToGrid(world) : world;

                Undo.RecordObject(so, "Add Vertex");
                so.Vertices.Add(snapped);
                if (so.UVs == null) so.UVs = new List<Vector2>();
                so.UVs.Add(Vector2.zero);
                _selectedVert = so.Vertices.Count - 1;
                EditorUtility.SetDirty(so);
                Repaint();
                e.Use();
                return;
            }

            // Try to select / start dragging nearest vertex
            int nearest = FindNearestVertex(so, mouse);
            if (nearest >= 0)
            {
                _selectedVert = nearest;
                _draggingVert = nearest;
                _isDragging   = false; // not yet moved
                _dragStartWorld = CanvasToWorld(mouse);
                e.Use();
                Repaint();
            }
            else
            {
                // Click empty space = deselect
                _selectedVert = -1;
                Repaint();
            }
        }

        private void HandleRightMouseDown(ProjectileShapeSO so, Event e)
        {
            int nearest = FindNearestVertex(so, e.mousePosition);
            if (nearest < 0) return;

            Undo.RecordObject(so, "Delete Vertex");
            so.Vertices.RemoveAt(nearest);
            if (so.UVs != null && nearest < so.UVs.Count)
                so.UVs.RemoveAt(nearest);

            if (_selectedVert == nearest)   _selectedVert = -1;
            else if (_selectedVert > nearest) _selectedVert--;

            so.BuildMesh();
            EditorUtility.SetDirty(so);
            e.Use();
            Repaint();
        }

        private void HandleMouseDrag(ProjectileShapeSO so, Event e)
        {
            if (_draggingVert < 0 || _draggingVert >= so.Vertices.Count) return;

            _isDragging = true;

            Vector2 world   = CanvasToWorld(e.mousePosition);
            Vector2 snapped = _snapEnabled ? SnapToGrid(world) : world;

            // Clamp to canvas world bounds
            float halfW = _canvasRect.width  * 0.5f / PIXELS_PER_UNIT;
            float halfH = _canvasRect.height * 0.5f / PIXELS_PER_UNIT;
            snapped.x   = Mathf.Clamp(snapped.x, -halfW + 0.1f, halfW - 0.1f);
            snapped.y   = Mathf.Clamp(snapped.y, -halfH + 0.1f, halfH - 0.1f);

            Undo.RecordObject(so, "Move Vertex");
            so.Vertices[_draggingVert] = snapped;

            // Sync UV if present
            if (so.UVs != null && _draggingVert < so.UVs.Count)
                so.UVs[_draggingVert] = Vector2.zero; // will be rebuilt

            so.BuildMesh();
            EditorUtility.SetDirty(so);
            e.Use();
            Repaint();
        }

        private void HandleMouseUp(ProjectileShapeSO so, Event e)
        {
            if (_draggingVert >= 0 && _isDragging)
            {
                // Rebuild UVs after drag completes
                if (so.Vertices != null && so.Vertices.Count >= 3)
                    NormalizeUVs(so);
                so.BuildMesh();
                EditorUtility.SetDirty(so);
            }
            _draggingVert = -1;
            _isDragging   = false;
            e.Use();
        }

        // ── Vertex list (numeric) ─────────────────────────────────────────────

        private void DrawVertexList(ProjectileShapeSO so)
        {
            if (so.Vertices == null) so.Vertices = new List<Vector2>();
            if (so.UVs      == null) so.UVs      = new List<Vector2>();

            for (int i = 0; i < so.Vertices.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    // Highlight selected
                    var prevBg  = GUI.backgroundColor;
                    var prevCol = GUI.contentColor;
                    if (i == _selectedVert)
                    {
                        GUI.backgroundColor = new Color(0.2f, 0.9f, 0.85f, 0.4f);
                        GUI.contentColor    = ColVertSel;
                    }

                    if (GUILayout.Button($"V{i}", GUILayout.Width(28), GUILayout.Height(18)))
                    {
                        _selectedVert = (i == _selectedVert) ? -1 : i;
                        Repaint();
                    }

                    GUI.contentColor    = prevCol;
                    GUI.backgroundColor = prevBg;

                    EditorGUI.BeginChangeCheck();
                    var newVal = EditorGUILayout.Vector2Field("", so.Vertices[i]);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(so, "Edit Vertex");
                        so.Vertices[i] = newVal;
                        so.BuildMesh();
                        EditorUtility.SetDirty(so);
                    }

                    // Delete button
                    GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                    if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18)))
                    {
                        Undo.RecordObject(so, "Delete Vertex");
                        so.Vertices.RemoveAt(i);
                        if (i < so.UVs.Count) so.UVs.RemoveAt(i);
                        if (_selectedVert == i)       _selectedVert = -1;
                        else if (_selectedVert > i)   _selectedVert--;
                        so.BuildMesh();
                        EditorUtility.SetDirty(so);
                        GUI.backgroundColor = Color.white;
                        break;
                    }
                    GUI.backgroundColor = Color.white;
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ Add Vertex", GUILayout.Height(20)))
                {
                    Undo.RecordObject(so, "Add Vertex");
                    so.Vertices.Add(Vector2.zero);
                    so.UVs.Add(Vector2.zero);
                    _selectedVert = so.Vertices.Count - 1;
                    EditorUtility.SetDirty(so);
                }
                if (GUILayout.Button("Clear All", GUILayout.Height(20)))
                {
                    if (EditorUtility.DisplayDialog(
                        "Clear Vertices",
                        "Delete all vertices?",
                        "Clear", "Cancel"))
                    {
                        Undo.RecordObject(so, "Clear Vertices");
                        so.Vertices.Clear();
                        so.Triangles?.Clear();
                        so.UVs?.Clear();
                        _selectedVert = -1;
                        so.BuildMesh();
                        EditorUtility.SetDirty(so);
                    }
                }
            }
        }

        // ── Coordinate utilities ──────────────────────────────────────────────

        /// World space → canvas screen space.
        private Vector2 WorldToCanvas(Vector2 world)
        {
            return new Vector2(
                _canvasRect.center.x + world.x * PIXELS_PER_UNIT,
                _canvasRect.center.y - world.y * PIXELS_PER_UNIT); // Y flipped
        }

        private Vector3 WorldToCanvas(Vector3 world)
            => WorldToCanvas(new Vector2(world.x, world.y));

        /// Canvas screen space → world space.
        private Vector2 CanvasToWorld(Vector2 screen)
        {
            return new Vector2(
                (screen.x - _canvasRect.center.x) / PIXELS_PER_UNIT,
               -(screen.y - _canvasRect.center.y) / PIXELS_PER_UNIT); // Y flipped
        }

        /// Snap world position to grid.
        private Vector2 SnapToGrid(Vector2 world)
        {
            return new Vector2(
                Mathf.Round(world.x / _snapIncrement) * _snapIncrement,
                Mathf.Round(world.y / _snapIncrement) * _snapIncrement);
        }

        /// Find nearest vertex to screen position. Returns -1 if none within hit radius.
        private int FindNearestVertex(ProjectileShapeSO so, Vector2 screenPos)
        {
            if (so.Vertices == null) return -1;
            float  bestDist = VERT_RADIUS + 4f;
            int    bestIdx  = -1;

            for (int i = 0; i < so.Vertices.Count; i++)
            {
                float d = Vector2.Distance(screenPos, WorldToCanvas(so.Vertices[i]));
                if (d < bestDist) { bestDist = d; bestIdx = i; }
            }
            return bestIdx;
        }

        // ── Mesh utilities ────────────────────────────────────────────────────

        private void AutoTriangulate(ProjectileShapeSO so)
        {
            Undo.RecordObject(so, "Auto Triangulate");
            if (so.Triangles == null) so.Triangles = new List<int>();
            so.Triangles.Clear();
            for (int i = 1; i < so.Vertices.Count - 1; i++)
            {
                so.Triangles.Add(0);
                so.Triangles.Add(i);
                so.Triangles.Add(i + 1);
            }
            NormalizeUVs(so);
            so.BuildMesh();
            EditorUtility.SetDirty(so);
        }

        private void NormalizeUVs(ProjectileShapeSO so)
        {
            if (so.Vertices == null || so.Vertices.Count == 0) return;
            if (so.UVs      == null) so.UVs = new List<Vector2>();

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var v in so.Vertices)
            {
                minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
            }
            float rw = (maxX - minX) < 0.0001f ? 1f : maxX - minX;
            float rh = (maxY - minY) < 0.0001f ? 1f : maxY - minY;

            so.UVs.Clear();
            foreach (var v in so.Vertices)
                so.UVs.Add(new Vector2((v.x - minX) / rw, (v.y - minY) / rh));

            EditorUtility.SetDirty(so);
        }

        public override bool RequiresConstantRepaint() => true;
    }
}
#endif
