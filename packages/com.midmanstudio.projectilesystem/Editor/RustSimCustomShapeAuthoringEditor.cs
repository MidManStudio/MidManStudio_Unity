// Scene-view point-placement editor for RustSimCustomShapeAuthoring — the
// "custom editor to place points to shape our own collider... in the editor
// scene view, not the inspector" the shape colliders were asked for.
//
// CONTROLS (all in the Scene view, with the object selected):
//   Left-click a point handle + drag  — move that control point
//   Ctrl + Left-click empty space     — add a new control point there
//   Alt  + Left-click a point handle  — remove that control point
//   Inspector "Rebake Now" button     — force a re-bake (auto-happens on
//                                        every scene-view edit already; this
//                                        is for after an Inspector-only edit
//                                        like changing Spline Type)
//
// Draws the raw control polygon (thin gray) and the actual baked curve that
// RustSim will receive (orange, thicker) side by side, so it's obvious how
// far a CatmullRom/Bezier bake diverges from the straight-line control
// points — the same distinction ProjectilePatternEditor draws for spawn
// patterns, applied here to collider shapes instead.

using UnityEditor;
using UnityEngine;
using MidManStudio.Projectiles.Managers;

namespace MidManStudio.Projectiles.EditorUtils
{
    [CustomEditor(typeof(RustSimCustomShapeAuthoring))]
    public sealed class RustSimCustomShapeAuthoringEditor : Editor
    {
        private const float HandleSize = 0.08f;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Edit control points in the Scene view:\n" +
                "• Drag a point to move it\n" +
                "• Ctrl+Click empty space to add a point\n" +
                "• Alt+Click a point to remove it",
                MessageType.Info);

            if (GUILayout.Button("Rebake Now"))
            {
                var authoring = (RustSimCustomShapeAuthoring)target;
                Undo.RecordObject(authoring, "Rebake Shape Curve");
                authoring.Rebake();
                SceneView.RepaintAll();
            }
        }

        private void OnSceneGUI()
        {
            var authoring = (RustSimCustomShapeAuthoring)target;
            var t = authoring.transform;
            var pts = authoring.ControlPoints;

            Event e = Event.current;

            // ── Draw control polygon (raw, unbaked) ────────────────────────────
            Handles.color = new Color(0.6f, 0.6f, 0.6f, 0.7f);
            for (int i = 0; i < pts.Count - 1; i++)
                Handles.DrawLine(t.TransformPoint(pts[i]), t.TransformPoint(pts[i + 1]));
            if (authoring.ClosedLoop && pts.Count > 2)
                Handles.DrawLine(t.TransformPoint(pts[pts.Count - 1]), t.TransformPoint(pts[0]));

            // ── Draw the actual baked curve RustSim will receive ────────────────
            authoring.Rebake(); // idempotent — cheap (≤8 samples), safe every pass
            int bakedCount = authoring.BakedCount;
            if (bakedCount >= 2)
            {
                Handles.color = new Color(1f, 0.55f, 0.1f, 0.9f);
                int segCount = authoring.ClosedLoop ? bakedCount : bakedCount - 1;
                for (int i = 0; i < segCount; i++)
                {
                    Vector3 a = t.TransformPoint(authoring.GetBakedPoint(i));
                    Vector3 b = t.TransformPoint(authoring.GetBakedPoint((i + 1) % bakedCount));
                    Handles.DrawAAPolyLine(4f, a, b);
                }
            }

            // ── Point handles: drag to move, Alt+click to remove ───────────────
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 worldPos = t.TransformPoint(pts[i]);
                float size = HandleUtility.GetHandleSize(worldPos) * HandleSize;

                if (e.alt && e.type == EventType.MouseDown && e.button == 0
                    && HandleUtility.DistanceToCircle(worldPos, size) < size)
                {
                    Undo.RecordObject(authoring, "Remove Shape Control Point");
                    authoring.EditorRemoveControlPoint(i);
                    e.Use();
                    break; // pts is now stale — bail out of this pass, redraw next frame
                }

                Handles.color = i == 0 ? Color.green : Color.white;
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(
                    worldPos, size, Vector3.zero, Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(authoring, "Move Shape Control Point");
                    authoring.EditorSetControlPoint(i, t.InverseTransformPoint(moved));
                }

                Handles.Label(worldPos + Vector3.up * size * 1.5f, i.ToString());
            }

            // ── Ctrl+Click empty space: add a point ─────────────────────────────
            if (e.control && e.type == EventType.MouseDown && e.button == 0)
            {
                Vector3? newLocalPoint = ResolveClickPoint(authoring, e.mousePosition);
                if (newLocalPoint.HasValue)
                {
                    Undo.RecordObject(authoring, "Add Shape Control Point");
                    authoring.EditorAddControlPoint(newLocalPoint.Value);
                    e.Use();
                }
            }

            // Keeps left-click-drag from also rotating/panning the scene camera
            // or deselecting while interacting with handles above.
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
        }

        /// <summary>
        /// 3D: raycasts against scene colliders first (so you can click directly
        /// on level geometry to place a point on its surface), falling back to
        /// a plane facing the scene camera through the last control point (or
        /// this transform's position with no points yet).
        /// 2D: always the object's local XY plane (Z = this transform's Z) —
        /// exact and unambiguous, no raycast needed.
        /// </summary>
        private static Vector3? ResolveClickPoint(RustSimCustomShapeAuthoring authoring, Vector2 mousePos)
        {
            var t = authoring.transform;
            Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);

            if (!authoring.Is3D)
            {
                Plane plane2D = new Plane(Vector3.forward, t.position);
                if (plane2D.Raycast(ray, out float enter2D))
                    return t.InverseTransformPoint(ray.GetPoint(enter2D));
                return null;
            }

            if (Physics.Raycast(ray, out RaycastHit hit, 5000f))
                return t.InverseTransformPoint(hit.point);

            Vector3 planeAnchor = authoring.ControlPoints.Count > 0
                ? t.TransformPoint(authoring.ControlPoints[authoring.ControlPoints.Count - 1])
                : t.position;
            Camera sceneCam = SceneView.currentDrawingSceneView != null
                ? SceneView.currentDrawingSceneView.camera : null;
            if (sceneCam == null) return null;

            Plane plane3D = new Plane(-sceneCam.transform.forward, planeAnchor);
            if (plane3D.Raycast(ray, out float enter3D))
                return t.InverseTransformPoint(ray.GetPoint(enter3D));

            return null;
        }
    }
}
