
//    DrawShapeSpecificFields now handles PatternShape.Formula:
//     shows _patternFormulaH / _patternFormulaV text fields with live
//     per-field validation (green ✓ or red error) and example dropdowns
//     via MathFormulaEvaluator.GetExamples().
//    Formula validation cache (_lastValidatedH/V, _formulaHError/VError).

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.EditorTools
{
    [CustomEditor(typeof(ProjectilePatternSO))]
    public class ProjectilePatternEditor : UnityEditor.Editor
    {
        // ── Preview state ─────────────────────────────────────────────────────
        private bool    _showPreview     = true;
        private bool    _showSimulation  = false;
        private int     _simulationCount = -1;
        private Rect    _previewRect;
        private int     _draggingPoint   = -1;
        private Vector2 _dragOffset;

        // ── Formula validation cache ──────────────────────────────────────────
        private string _lastValidatedH;
        private string _lastValidatedV;
        private string _formulaHError;
        private string _formulaVError;

        // ── Preview constants ─────────────────────────────────────────────────
        private const float PreviewHeight    = 300f;
        private const float PreviewPadding   = 30f;
        private const int   SplineResolution = 64;

        private static readonly Color SplineColor = new Color(0.3f, 0.8f, 1.0f);
        private static readonly Color PointColor  = new Color(1.0f, 0.8f, 0.2f);
        private static readonly Color PointHover  = new Color(1.0f, 1.0f, 0.5f);
        private static readonly Color SimRayColor = new Color(0.2f, 1.0f, 0.4f, 0.7f);
        private static readonly Color GridColor   = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        private static readonly Color OriginColor = new Color(0.7f, 0.7f, 0.7f, 0.8f);
        private static readonly Color LinearColor = new Color(1.0f, 0.6f, 0.2f);
        private static readonly Color ColValidOk  = new Color(0.30f, 0.90f, 0.30f);
        private static readonly Color ColValidErr = new Color(0.95f, 0.30f, 0.30f);

        // ─────────────────────────────────────────────────────────────────────

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var pattern = (ProjectilePatternSO)target;

            // ── Shape selector ────────────────────────────────────────────────
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_shape"));
            EditorGUILayout.Space(2);

            bool isSpline  = pattern.Shape == PatternShape.Spline;
            bool isFormula = pattern.Shape == PatternShape.Formula;

            // ── Shape-specific fields ─────────────────────────────────────────
            if (isSpline)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_splineType"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_projectileCount"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_speedVariance"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_rngSeed"));
                EditorGUILayout.Space(6);
                DrawControlPointList();
            }
            else if (isFormula)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_projectileCount"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_speedVariance"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_rngSeed"));
                EditorGUILayout.Space(6);
                DrawFormulaFields(pattern);
            }
            else
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_projectileCount"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_speedVariance"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_rngSeed"));
                EditorGUILayout.Space(4);
                DrawShapeSpecificFields(pattern.Shape);
            }

            serializedObject.ApplyModifiedProperties();
            EditorGUILayout.Space(10);

            // ── Preview ───────────────────────────────────────────────────────
            _showPreview = EditorGUILayout.Foldout(
                _showPreview, "Pattern Preview", true, EditorStyles.foldoutHeader);

            if (_showPreview)
            {
                DrawPreviewHeader(pattern);
                DrawPreviewViewport(pattern);
            }

            // ── Scene simulation ──────────────────────────────────────────────
            EditorGUILayout.Space(6);
            _showSimulation = EditorGUILayout.Foldout(
                _showSimulation, "Simulate (Gizmo in Scene)", true,
                EditorStyles.foldoutHeader);

            if (_showSimulation)
            {
                EditorGUILayout.HelpBox(
                    "Select a GameObject in the scene. Green rays show projectile directions.\n" +
                    "Horizontal angle = yaw, Vertical angle = pitch (3D correct).",
                    MessageType.Info);
                _simulationCount = EditorGUILayout.IntField(
                    "Override Count (-1 = use config)", _simulationCount);
                if (GUILayout.Button("Refresh Scene Gizmo")) SceneView.RepaintAll();
            }
        }

        // ── Formula fields ────────────────────────────────────────────────────

        private void DrawFormulaFields(ProjectilePatternSO pattern)
        {
            var propH = serializedObject.FindProperty("_patternFormulaH");
            var propV = serializedObject.FindProperty("_patternFormulaV");

            EditorGUILayout.LabelField(
                "Shot Angle Formulas  (t = i/n  |  i = index  |  n = count)",
                EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // H formula
            EditorGUILayout.LabelField("H(i,n)  — horizontal degrees",
                EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(propH, GUIContent.none);
            if (EditorGUI.EndChangeCheck())
                serializedObject.ApplyModifiedProperties();

            if (propH.stringValue != _lastValidatedH)
            {
                _lastValidatedH = propH.stringValue;
                MathFormulaEvaluator.Validate(propH.stringValue, out _formulaHError);
            }
            DrawFormulaStatus(_formulaHError);
            DrawExampleDropdown(propH, FormulaUsage.PatternH);

            EditorGUILayout.Space(4);

            // V formula
            EditorGUILayout.LabelField("V(i,n)  — vertical degrees",
                EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(propV, GUIContent.none);
            if (EditorGUI.EndChangeCheck())
                serializedObject.ApplyModifiedProperties();

            if (propV.stringValue != _lastValidatedV)
            {
                _lastValidatedV = propV.stringValue;
                MathFormulaEvaluator.Validate(propV.stringValue, out _formulaVError);
            }
            DrawFormulaStatus(_formulaVError);
            DrawExampleDropdown(propV, FormulaUsage.PatternV);

            EditorGUILayout.EndVertical();

            EditorGUILayout.HelpBox(
                "Variables: t=i/n (0..1), i (index), n (count), pi, tau, e\n" +
                "Functions: sin cos tan sqrt abs min max clamp lerp floor ceil\n" +
                "Ring:    H = i/n*360              V = 0\n" +
                "Fan:     H = i/(n-1)*180 - 90     V = 0\n" +
                "Spiral:  H = i/n*360*3            V = i/(n-1)*60 - 30",
                MessageType.None);
        }

        private static void DrawFormulaStatus(string error)
        {
            if (error == null)
            {
                var s = new GUIStyle(EditorStyles.miniLabel);
                s.normal.textColor = ColValidOk;
                EditorGUILayout.LabelField("✓ Valid", s);
            }
            else
            {
                var s = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
                s.normal.textColor = ColValidErr;
                EditorGUILayout.LabelField($"✕ {error}", s);
            }
        }

        private static void DrawExampleDropdown(
            SerializedProperty prop, FormulaUsage usage)
        {
            var examples = MathFormulaEvaluator.GetExamples(usage);
            if (examples.Length == 0) return;

            var options = new string[examples.Length + 1];
            options[0]  = "Insert Example…";
            for (int i = 0; i < examples.Length; i++) options[i + 1] = examples[i];

            int sel = EditorGUILayout.Popup(0, options);
            if (sel > 0)
            {
                prop.stringValue = examples[sel - 1];
                prop.serializedObject.ApplyModifiedProperties();
                GUI.changed = true;
            }
        }

        // ── Shape-specific inspector sections ────────────────────────────────

        private void DrawShapeSpecificFields(PatternShape shape)
        {
            switch (shape)
            {
                case PatternShape.Fan:
                    EditorGUILayout.PropertyField(
                        serializedObject.FindProperty("_fanHalfArcDeg"),
                        new GUIContent("Half Arc (°)"));
                    EditorGUILayout.PropertyField(
                        serializedObject.FindProperty("_fanVerticalDeg"),
                        new GUIContent("Vertical Tilt (°)"));
                    break;

                case PatternShape.VShape:
                    EditorGUILayout.PropertyField(
                        serializedObject.FindProperty("_vShapeAngleDeg"),
                        new GUIContent("Arm Angle (°)"));
                    EditorGUILayout.PropertyField(
                        serializedObject.FindProperty("_vShapeIncludeCenter"),
                        new GUIContent("Include Center Bullet"));
                    EditorGUILayout.PropertyField(
                        serializedObject.FindProperty("_vShapeVerticalDeg"),
                        new GUIContent("Vertical Tilt (°)"));
                    break;

                case PatternShape.Shotgun:
                    EditorGUILayout.PropertyField(
                        serializedObject.FindProperty("_shotgunConeDeg"),
                        new GUIContent("Cone Half-Angle (°)"));
                    break;

                case PatternShape.Star:
                    EditorGUILayout.PropertyField(
                        serializedObject.FindProperty("_starPoints"),
                        new GUIContent("Polygon Points"));
                    EditorGUILayout.PropertyField(
                        serializedObject.FindProperty("_starInnerScale"),
                        new GUIContent("Inner Ring Scale"));
                    break;

                case PatternShape.Spiral:
                    EditorGUILayout.PropertyField(
                        serializedObject.FindProperty("_spiralAngleStep"),
                        new GUIContent("Angle Step Per Bullet (°)"));
                    break;
            }
        }

        private void DrawControlPointList()
        {
            EditorGUILayout.LabelField("Control Points  (X = H°, Y = V°)",
                EditorStyles.boldLabel);

            var pointsProp = serializedObject.FindProperty("_controlPoints");
            EditorGUI.indentLevel++;
            for (int i = 0; i < pointsProp.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(
                    pointsProp.GetArrayElementAtIndex(i),
                    new GUIContent($"Point {i}"), true);

                if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18))
                    && pointsProp.arraySize > 1)
                {
                    pointsProp.DeleteArrayElementAtIndex(i);
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Point"))
            {
                pointsProp.InsertArrayElementAtIndex(pointsProp.arraySize);
                pointsProp.GetArrayElementAtIndex(pointsProp.arraySize - 1)
                          .vector2Value = Vector2.zero;
            }
            if (GUILayout.Button("Clear")) pointsProp.ClearArray();
            EditorGUILayout.EndHorizontal();
        }

        // ── Preview header ────────────────────────────────────────────────────

        private void DrawPreviewHeader(ProjectilePatternSO pattern)
        {
            string shapeStr = pattern.Shape == PatternShape.Spline
                ? $"Spline ({pattern.SplineType})"
                : pattern.Shape.ToString();

            EditorGUILayout.LabelField(
                $"Shape: {shapeStr}   Projectiles: {pattern.ProjectileCount}",
                EditorStyles.miniLabel);

            // Validation summary for Formula mode
            if (pattern.Shape == PatternShape.Formula)
            {
                bool hOk = _formulaHError == null;
                bool vOk = _formulaVError == null;
                if (!hOk || !vOk)
                {
                    EditorGUILayout.HelpBox(
                        "Formula errors — preview may fall back to (0,0). " +
                        "Fix formulas above.", MessageType.Warning);
                }
            }
        }

        // ── Main preview viewport ─────────────────────────────────────────────

        private void DrawPreviewViewport(ProjectilePatternSO pattern)
        {
            _previewRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(PreviewHeight));

            float angleRange = ComputeAngleRange(pattern);

            if (Event.current.type == EventType.Repaint)
            {
                DrawPreviewBackground();
                DrawPreviewGrid(angleRange);
                DrawOriginMarker(angleRange);
                DrawPatternVisual(pattern, angleRange);
                DrawSampleRays(pattern, angleRange);

                if (pattern.Shape == PatternShape.Spline)
                    DrawControlPoints(pattern, angleRange);
            }

            if (pattern.Shape == PatternShape.Spline)
                HandleMouseInput(pattern, angleRange);
        }

        // ── Dynamic angle range ───────────────────────────────────────────────

        private float ComputeAngleRange(ProjectilePatternSO pattern)
        {
            switch (pattern.Shape)
            {
                case PatternShape.Ring360:
                case PatternShape.Spiral:
                case PatternShape.Star:
                case PatternShape.Formula:
                    return 180f;

                case PatternShape.Fan:
                    return Mathf.Max(pattern.FanHalfArcDeg + 15f, 45f);

                case PatternShape.Spline:
                {
                    float maxAbs = 30f;
                    if (pattern.ControlPoints != null)
                        foreach (var cp in pattern.ControlPoints)
                        {
                            maxAbs = Mathf.Max(maxAbs, Mathf.Abs(cp.x));
                            maxAbs = Mathf.Max(maxAbs, Mathf.Abs(cp.y));
                        }
                    return maxAbs + 15f;
                }

                default:
                    return 90f;
            }
        }

        // ── Drawing helpers ───────────────────────────────────────────────────

        private void DrawPreviewBackground()
            => EditorGUI.DrawRect(_previewRect, new Color(0.12f, 0.12f, 0.12f));

        private void DrawPreviewGrid(float angleRange)
        {
            Handles.color = GridColor;
            Handles.DrawLine(
                AngleToPreview(new Vector2(0f, -angleRange), angleRange),
                AngleToPreview(new Vector2(0f,  angleRange), angleRange));
            Handles.DrawLine(
                AngleToPreview(new Vector2(-angleRange, 0f), angleRange),
                AngleToPreview(new Vector2( angleRange, 0f), angleRange));

            Handles.color = new Color(0.2f, 0.2f, 0.2f, 0.4f);
            float step = angleRange * 0.5f;
            foreach (float a in new[] { -step, step })
            {
                Handles.DrawLine(
                    AngleToPreview(new Vector2(a, -angleRange), angleRange),
                    AngleToPreview(new Vector2(a,  angleRange), angleRange));
                Handles.DrawLine(
                    AngleToPreview(new Vector2(-angleRange, a), angleRange),
                    AngleToPreview(new Vector2( angleRange, a), angleRange));
            }
        }

        private void DrawOriginMarker(float angleRange)
        {
            Handles.color = OriginColor;
            Vector2 origin = AngleToPreview(Vector2.zero, angleRange);
            Handles.DrawSolidDisc(origin, Vector3.forward, 3f);
            Vector2 fwd = AngleToPreview(new Vector2(0f, angleRange * 0.06f), angleRange);
            Handles.DrawLine(origin, fwd);
        }

        private void DrawPatternVisual(ProjectilePatternSO pattern, float angleRange)
        {
            if (pattern.Shape == PatternShape.Spline)
            {
                DrawSplineVisual(pattern, angleRange);
            }
            else
            {
                var dirs = pattern.SampleDirections();
                Handles.color = SplineColor;
                Vector2 origin = AngleToPreview(Vector2.zero, angleRange);
                foreach (var d in dirs)
                    Handles.DrawLine(origin, AngleToPreview(d * 0.7f, angleRange));
            }
        }

        private void DrawSplineVisual(ProjectilePatternSO pattern, float angleRange)
        {
            if (pattern.ControlPoints == null || pattern.ControlPoints.Length < 2) return;

            Color lineCol = pattern.SplineType == PatternSplineType.Linear
                ? LinearColor : SplineColor;
            Handles.color = lineCol;

            int res = pattern.SplineType == PatternSplineType.Linear
                ? pattern.ControlPoints.Length - 1
                : SplineResolution;

            Vector2 prev = AngleToPreview(pattern.EvaluateSpline(0f), angleRange);
            for (int i = 1; i <= res; i++)
            {
                float   t    = (float)i / res;
                Vector2 curr = AngleToPreview(pattern.EvaluateSpline(t), angleRange);
                Handles.DrawLine(prev, curr);
                prev = curr;
            }
        }

        private void DrawSampleRays(ProjectilePatternSO pattern, float angleRange)
        {
            var dirs = pattern.SampleDirections();
            Handles.color = SimRayColor;
            Vector2 origin = AngleToPreview(Vector2.zero, angleRange);

            foreach (var dir in dirs)
            {
                Vector2 rawEnd = AngleToPreview(dir * 0.8f, angleRange);
                Handles.DrawLine(origin, rawEnd);
                Handles.DrawSolidDisc(rawEnd, Vector3.forward, 3f);
            }
        }

        private void DrawControlPoints(ProjectilePatternSO pattern, float angleRange)
        {
            if (pattern.ControlPoints == null) return;
            Vector2 mousePos = Event.current.mousePosition;

            for (int i = 0; i < pattern.ControlPoints.Length; i++)
            {
                Vector2 screenPos = AngleToPreview(pattern.ControlPoints[i], angleRange);
                bool    hovered   = Vector2.Distance(mousePos, screenPos) < 12f;
                Handles.color     = hovered ? PointHover : PointColor;
                Handles.DrawSolidDisc(screenPos, Vector3.forward, 8f);
                GUI.Label(new Rect(screenPos.x + 10f, screenPos.y - 8f, 36f, 16f),
                    i.ToString(), EditorStyles.miniLabel);
            }
        }

        // ── Mouse input ───────────────────────────────────────────────────────

        private void HandleMouseInput(ProjectilePatternSO pattern, float angleRange)
        {
            Event e = Event.current;
            if (!_previewRect.Contains(e.mousePosition)) return;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                for (int i = 0; i < pattern.ControlPoints.Length; i++)
                {
                    Vector2 screenPos = AngleToPreview(pattern.ControlPoints[i], angleRange);
                    if (Vector2.Distance(e.mousePosition, screenPos) <= 12f)
                    {
                        _draggingPoint = i;
                        _dragOffset    = screenPos - e.mousePosition;
                        e.Use(); break;
                    }
                }
            }

            if (e.type == EventType.MouseDrag && _draggingPoint >= 0)
            {
                Vector2 newAngle = PreviewToAngle(e.mousePosition + _dragOffset, angleRange);
                newAngle.x = Mathf.Clamp(newAngle.x, -180f, 180f);
                newAngle.y = Mathf.Clamp(newAngle.y, -90f,   90f);

                Undo.RecordObject(target, "Move Pattern Point");
                serializedObject.Update();
                serializedObject.FindProperty("_controlPoints")
                                .GetArrayElementAtIndex(_draggingPoint)
                                .vector2Value = newAngle;
                serializedObject.ApplyModifiedProperties();
                e.Use();
                Repaint();
            }

            if (e.type == EventType.MouseUp) { _draggingPoint = -1; e.Use(); }
        }

        // ── Coordinate conversion ─────────────────────────────────────────────

        private Vector2 AngleToPreview(Vector2 angleDeg, float angleRange)
        {
            float u = (angleDeg.x + angleRange) / (2f * angleRange);
            float v = 1f - (angleDeg.y + angleRange) / (2f * angleRange);
            return new Vector2(
                _previewRect.x + PreviewPadding + u * (_previewRect.width  - 2f * PreviewPadding),
                _previewRect.y + PreviewPadding + v * (_previewRect.height - 2f * PreviewPadding));
        }

        private Vector2 PreviewToAngle(Vector2 screenPos, float angleRange)
        {
            float u = (screenPos.x - _previewRect.x - PreviewPadding)
                      / (_previewRect.width - 2f * PreviewPadding);
            float v = (screenPos.y - _previewRect.y - PreviewPadding)
                      / (_previewRect.height - 2f * PreviewPadding);
            return new Vector2(
                 u * 2f * angleRange - angleRange,
                (1f - v) * 2f * angleRange - angleRange);
        }

        // ── Scene gizmo ───────────────────────────────────────────────────────

        private void OnSceneGUI()
        {
            if (!_showSimulation) return;
            var pattern = (ProjectilePatternSO)target;

            Transform t = Selection.activeTransform;
            if (t == null) return;

            int count   = _simulationCount > 0 ? _simulationCount : pattern.ProjectileCount;
            var samples = pattern.SampleDirections(count);
            float rayLen = 5f;

            Handles.color = SimRayColor;
            foreach (var angleDeg in samples)
            {
                Vector3 dir = Quaternion.Euler(-angleDeg.y, angleDeg.x, 0f) * t.forward;
                Handles.DrawLine(t.position, t.position + dir * rayLen);
                Handles.DrawSolidDisc(t.position + dir * rayLen, dir, 0.05f);
            }
        }
    }
}
#endif
