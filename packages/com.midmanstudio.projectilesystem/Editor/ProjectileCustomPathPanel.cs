// ProjectileCustomPathPanel — rich editing UI for ProjectileConfigSO's
// CustomCurve movement path (_customPathShape/_customPathSplineType/
// _customPathPoints/_customPathFormulaX/_customPathFormulaY), embedded into
// ProjectileConfigScriptableObjectEditor the same way ProjectileConfigJsonPanel
// already is — a plain reusable class, not itself a [CustomEditor], so a
// game-specific ProjectileConfigSO subclass's own custom editor can embed it
// too if it wants this exact UI.
//
// Deliberately mirrors ProjectilePatternEditor's formula-validation/example-
// dropdown/draggable-point-list UX as closely as the two domains allow — same
// visual language, same interaction model, so anyone already comfortable
// authoring a spawn pattern feels at home authoring a movement path.
//
// KEY DIFFERENCE FROM THE PATTERN PREVIEW: the preview here calls
// ProjectileConfigSO.EvaluateCustomPath(t) directly — the exact same method
// PhysicsProjectileBase.ApplyCustomCurve() calls at runtime — rather than
// re-implementing the spline/formula math a second time for display purposes.
// What you see in this preview is what the projectile actually does, not a
// best-effort approximation of it.

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;

namespace MidManStudio.Projectiles.EditorUtils
{
    public sealed class ProjectileCustomPathPanel
    {
        // ── Preview state ────────────────────────────────────────────────────
        private bool    _showPanel;
        private bool    _userToggledPanel; // once the user manually folds/unfolds, stop auto-expanding on movement-type change
        private Rect    _previewRect;
        private int     _draggingPoint = -1;
        private Vector2 _dragOffset;

        // ── Formula validation cache ────────────────────────────────────────
        private string _lastValidatedX;
        private string _lastValidatedY;
        private string _formulaXError;
        private string _formulaYError;

        // ── Preview constants ────────────────────────────────────────────────
        private const float PreviewHeight  = 220f;
        private const float PreviewPadding = 28f;
        private const int   SampleCount    = 64;

        private static readonly Color PathColor    = new Color(1.0f, 0.55f, 0.1f);
        private static readonly Color PointColor   = new Color(1.0f, 0.8f, 0.2f);
        private static readonly Color PointHover   = new Color(1.0f, 1.0f, 0.5f);
        private static readonly Color SpawnColor   = new Color(0.4f, 1.0f, 0.5f);
        private static readonly Color GridColor    = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        private static readonly Color AxisColor    = new Color(0.6f, 0.6f, 0.6f, 0.6f);
        private static readonly Color PaceDotColor = new Color(0.3f, 0.8f, 1.0f, 0.9f);
        private static readonly Color ColValidOk   = new Color(0.30f, 0.90f, 0.30f);
        private static readonly Color ColValidErr  = new Color(0.95f, 0.30f, 0.30f);

        /// <summary>
        /// Draws the full path-authoring section. Call after the rest of the
        /// inspector (matching where ProjectileConfigJsonPanel is embedded) —
        /// this handles its own foldout, so it's a no-op visually (beyond the
        /// foldout header itself) when collapsed.
        /// </summary>
        public void Draw(SerializedObject serializedObject, ProjectileConfigSO cfg)
        {
            var movementTypeProp = serializedObject.FindProperty("_movementType");
            bool isCustomCurve = movementTypeProp != null
                && movementTypeProp.enumValueIndex == (int)ProjectileMovementType.CustomCurve;

            // Auto-expand the first time a config is set to CustomCurve —
            // otherwise this whole rich section is easy to miss under a
            // collapsed foldout on a config that actually needs it. Once the
            // user has manually toggled it themselves, their choice sticks
            // instead of fighting them every time they switch movement types.
            if (isCustomCurve && !_userToggledPanel) _showPanel = true;

            EditorGUILayout.Space(6);
            EditorGUI.BeginChangeCheck();
            _showPanel = EditorGUILayout.Foldout(
                _showPanel, "Custom Movement Path", true, EditorStyles.foldoutHeader);
            if (EditorGUI.EndChangeCheck()) _userToggledPanel = true;

            if (!_showPanel) return;

            if (!isCustomCurve)
            {
                EditorGUILayout.HelpBox(
                    "Only used when Movement Type is Custom Curve — edit freely, " +
                    "it just won't do anything until the config's Movement Type is " +
                    "set to Custom Curve above.", MessageType.None);
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var shapeProp = serializedObject.FindProperty("_customPathShape");
            EditorGUILayout.PropertyField(shapeProp, new GUIContent("Path Shape"));
            serializedObject.ApplyModifiedProperties();

            bool isFormula = shapeProp.enumValueIndex == (int)PathShape.Formula;

            if (isFormula)
            {
                DrawFormulaFields(serializedObject);
            }
            else
            {
                var splineProp = serializedObject.FindProperty("_customPathSplineType");
                EditorGUILayout.PropertyField(splineProp, new GUIContent("Spline Type"));
                serializedObject.ApplyModifiedProperties();
                EditorGUILayout.Space(4);
                DrawPointList(serializedObject);
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6);
            DrawPreviewViewport(serializedObject, cfg, isFormula);
        }

        // ── Formula fields ───────────────────────────────────────────────────

        private void DrawFormulaFields(SerializedObject serializedObject)
        {
            var propX = serializedObject.FindProperty("_customPathFormulaX");
            var propY = serializedObject.FindProperty("_customPathFormulaY");

            EditorGUILayout.LabelField(
                "Path Formulas  (t = 0..1 over the path's duration)",
                EditorStyles.boldLabel);

            EditorGUILayout.LabelField("X(t)  — forward distance from spawn",
                EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(propX, GUIContent.none);
            if (EditorGUI.EndChangeCheck()) serializedObject.ApplyModifiedProperties();

            if (propX.stringValue != _lastValidatedX)
            {
                _lastValidatedX = propX.stringValue;
                MathFormulaEvaluator.Validate(propX.stringValue, out _formulaXError);
            }
            DrawFormulaStatus(_formulaXError);
            DrawExampleDropdown(propX, FormulaUsage.PathX);

            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("Y(t)  — perpendicular deviation",
                EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(propY, GUIContent.none);
            if (EditorGUI.EndChangeCheck()) serializedObject.ApplyModifiedProperties();

            if (propY.stringValue != _lastValidatedY)
            {
                _lastValidatedY = propY.stringValue;
                MathFormulaEvaluator.Validate(propY.stringValue, out _formulaYError);
            }
            DrawFormulaStatus(_formulaYError);
            DrawExampleDropdown(propY, FormulaUsage.PathY);

            EditorGUILayout.HelpBox(
                "Auto-anchored to spawn — the effective offset used is always " +
                "X(t)-X(0), Y(t)-Y(0), so the path starts at spawn regardless of " +
                "what X(0)/Y(0) literally evaluate to.\n" +
                "Variables: t (0..1), pi, tau, e   Functions: sin cos tan sqrt abs " +
                "min max clamp lerp floor ceil",
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

        private static void DrawExampleDropdown(SerializedProperty prop, FormulaUsage usage)
        {
            var examples = MathFormulaEvaluator.GetExamples(usage);
            if (examples.Length == 0) return;

            var options = new string[examples.Length + 1];
            options[0] = "Insert Example…";
            for (int i = 0; i < examples.Length; i++) options[i + 1] = examples[i];

            int sel = EditorGUILayout.Popup(0, options);
            if (sel > 0)
            {
                prop.stringValue = examples[sel - 1];
                prop.serializedObject.ApplyModifiedProperties();
                GUI.changed = true;
            }
        }

        // ── Point list ────────────────────────────────────────────────────────

        private void DrawPointList(SerializedObject serializedObject)
        {
            EditorGUILayout.LabelField(
                "Waypoints  (X = forward distance, Y = perpendicular deviation)",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Point 0 (spawn, always (0,0)) is implicit — not listed here.",
                EditorStyles.miniLabel);

            var pointsProp = serializedObject.FindProperty("_customPathPoints");
            EditorGUI.indentLevel++;
            for (int i = 0; i < pointsProp.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(
                    pointsProp.GetArrayElementAtIndex(i),
                    new GUIContent($"Point {i + 1}"), true);

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
                Vector2 last = pointsProp.arraySize > 0
                    ? pointsProp.GetArrayElementAtIndex(pointsProp.arraySize - 1).vector2Value
                    : Vector2.zero;
                pointsProp.InsertArrayElementAtIndex(pointsProp.arraySize);
                // New point continues 1 unit further forward than the last one,
                // not stacked at the same spot — a new point is immediately
                // visible/draggable rather than hidden exactly under its
                // predecessor.
                pointsProp.GetArrayElementAtIndex(pointsProp.arraySize - 1).vector2Value
                    = last + new Vector2(1f, 0f);
            }
            if (GUILayout.Button("Clear")) pointsProp.ClearArray();
            EditorGUILayout.EndHorizontal();

            serializedObject.ApplyModifiedProperties();
        }

        // ── Preview viewport ──────────────────────────────────────────────────

        private void DrawPreviewViewport(SerializedObject serializedObject, ProjectileConfigSO cfg, bool isFormula)
        {
            EditorGUILayout.LabelField("Path Preview", EditorStyles.boldLabel);

            if (isFormula && (_formulaXError != null || _formulaYError != null))
            {
                EditorGUILayout.HelpBox(
                    "Formula errors — preview may be flat/incorrect. Fix formulas above.",
                    MessageType.Warning);
            }

            _previewRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(PreviewHeight));

            var (rangeX, rangeY) = ComputeRange(cfg);

            if (Event.current.type == EventType.Repaint)
            {
                DrawBackground();
                DrawGrid(rangeX, rangeY);
                DrawSpawnMarker(rangeX, rangeY);
                DrawPathCurve(cfg, rangeX, rangeY);
                DrawPaceDots(cfg, rangeX, rangeY);
                if (!isFormula) DrawPointHandles(cfg, rangeX, rangeY);
            }

            if (!isFormula) HandleMouseInput(serializedObject, cfg, rangeX, rangeY);
        }

        /// <summary>
        /// Auto-scales to whatever the path actually spans, sampling the
        /// SAME EvaluateCustomPath(t) the runtime uses — so the preview never
        /// clips a formula-generated path the way a range derived only from
        /// the point list would.
        /// </summary>
        private (float x, float y) ComputeRange(ProjectileConfigSO cfg)
        {
            float maxX = 0.5f, maxY = 0.5f;
            for (int i = 0; i <= SampleCount; i++)
            {
                Vector2 p = cfg.EvaluateCustomPath((float)i / SampleCount);
                maxX = Mathf.Max(maxX, Mathf.Abs(p.x));
                maxY = Mathf.Max(maxY, Mathf.Abs(p.y));
            }
            return (maxX * 1.15f, Mathf.Max(maxY * 1.4f, maxX * 0.3f));
        }

        /// <summary>
        /// Spawn sits at the LEFT edge, forward (X) extends rightward,
        /// perpendicular (Y) extends up/down from vertical center — reads
        /// left-to-right as "time passing", matching how a trajectory sketch
        /// is naturally drawn, rather than centering on (0,0) the way the
        /// angle-based pattern preview does (that one centers because angles
        /// are symmetric around straight-ahead; a forward-distance path isn't).
        /// </summary>
        private Vector2 PathToPreview(Vector2 p, float rangeX, float rangeY)
        {
            float u = p.x / rangeX;
            float v = 0.5f - (p.y / (2f * rangeY));
            return new Vector2(
                _previewRect.x + PreviewPadding + u * (_previewRect.width - 2f * PreviewPadding),
                _previewRect.y + v * _previewRect.height);
        }

        private Vector2 PreviewToPath(Vector2 screenPos, float rangeX, float rangeY)
        {
            float u = (screenPos.x - _previewRect.x - PreviewPadding)
                      / (_previewRect.width - 2f * PreviewPadding);
            float v = (screenPos.y - _previewRect.y) / _previewRect.height;
            return new Vector2(u * rangeX, (0.5f - v) * 2f * rangeY);
        }

        private void DrawBackground() => EditorGUI.DrawRect(_previewRect, new Color(0.12f, 0.12f, 0.12f));

        private void DrawGrid(float rangeX, float rangeY)
        {
            Handles.color = AxisColor;
            Handles.DrawLine(
                PathToPreview(new Vector2(0f, -rangeY), rangeX, rangeY),
                PathToPreview(new Vector2(0f,  rangeY), rangeX, rangeY));
            Handles.DrawLine(
                PathToPreview(new Vector2(0f, 0f), rangeX, rangeY),
                PathToPreview(new Vector2(rangeX, 0f), rangeX, rangeY));

            Handles.color = GridColor;
            for (int i = 1; i <= 4; i++)
            {
                float x = rangeX * i / 4f;
                Handles.DrawLine(
                    PathToPreview(new Vector2(x, -rangeY), rangeX, rangeY),
                    PathToPreview(new Vector2(x,  rangeY), rangeX, rangeY));
            }
        }

        private void DrawSpawnMarker(float rangeX, float rangeY)
        {
            Handles.color = SpawnColor;
            Vector2 origin = PathToPreview(Vector2.zero, rangeX, rangeY);
            Handles.DrawSolidDisc(origin, Vector3.forward, 4f);
            GUI.Label(new Rect(origin.x + 6f, origin.y - 18f, 60f, 16f), "spawn", EditorStyles.miniLabel);
        }

        private void DrawPathCurve(ProjectileConfigSO cfg, float rangeX, float rangeY)
        {
            Handles.color = PathColor;
            Vector2 prev = PathToPreview(cfg.EvaluateCustomPath(0f), rangeX, rangeY);
            for (int i = 1; i <= SampleCount; i++)
            {
                Vector2 curr = PathToPreview(cfg.EvaluateCustomPath((float)i / SampleCount), rangeX, rangeY);
                Handles.DrawLine(prev, curr);
                prev = curr;
            }
        }

        /// <summary>
        /// Dots at EQUAL TIME intervals (not equal t-along-the-curve) —
        /// visualizes the speed curve's warp directly: bunched-up dots mean
        /// the projectile covers that stretch quickly, spread-out dots mean
        /// it lingers. Uses the config's own speed multiplier curve, same
        /// cumulative-normalization shape PhysicsProjectileBase.
        /// BuildPathWarpTable uses at runtime — approximated inline here
        /// rather than duplicating that exact private method, since this is
        /// a lightweight editor-only visualization, not something that needs
        /// to match runtime float-for-float.
        /// </summary>
        private void DrawPaceDots(ProjectileConfigSO cfg, float rangeX, float rangeY)
        {
            var speedCurve = cfg.CustomCurveSpeedMultiplier;
            if (speedCurve == null) return;

            const int dotCount = 10;
            float cumulative = 0f;
            var warps = new float[dotCount + 1];
            for (int i = 0; i <= dotCount; i++)
            {
                float t = (float)i / dotCount;
                cumulative += Mathf.Max(0.0001f, speedCurve.Evaluate(t));
                warps[i] = cumulative;
            }
            float total = warps[dotCount];
            if (total <= 0.0001f) return;

            Handles.color = PaceDotColor;
            for (int i = 0; i <= dotCount; i++)
            {
                float tWarped = warps[i] / total;
                Vector2 screen = PathToPreview(cfg.EvaluateCustomPath(tWarped), rangeX, rangeY);
                Handles.DrawSolidDisc(screen, Vector3.forward, 2.5f);
            }
        }

        private void DrawPointHandles(ProjectileConfigSO cfg, float rangeX, float rangeY)
        {
            var pts = cfg.CustomPathPoints;
            if (pts == null) return;
            Vector2 mousePos = Event.current.mousePosition;

            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 screenPos = PathToPreview(pts[i], rangeX, rangeY);
                bool    hovered   = Vector2.Distance(mousePos, screenPos) < 12f;
                Handles.color     = hovered ? PointHover : PointColor;
                Handles.DrawSolidDisc(screenPos, Vector3.forward, 6f);
                GUI.Label(new Rect(screenPos.x + 8f, screenPos.y - 8f, 24f, 16f),
                    (i + 1).ToString(), EditorStyles.miniLabel);
            }
        }

        private void HandleMouseInput(SerializedObject serializedObject, ProjectileConfigSO cfg, float rangeX, float rangeY)
        {
            Event e = Event.current;
            if (!_previewRect.Contains(e.mousePosition)) return;

            var pts = cfg.CustomPathPoints;
            if (pts == null) return;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector2 screenPos = PathToPreview(pts[i], rangeX, rangeY);
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
                Vector2 newPoint = PreviewToPath(e.mousePosition + _dragOffset, rangeX, rangeY);

                Undo.RecordObject(cfg, "Move Path Point");
                serializedObject.Update();
                serializedObject.FindProperty("_customPathPoints")
                                .GetArrayElementAtIndex(_draggingPoint)
                                .vector2Value = newPoint;
                serializedObject.ApplyModifiedProperties();
                e.Use();
            }

            if (e.type == EventType.MouseUp) { _draggingPoint = -1; e.Use(); }
        }
    }
}
#endif
