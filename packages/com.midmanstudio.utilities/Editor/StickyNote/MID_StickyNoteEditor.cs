// MID_StickyNoteEditor.cs
// Custom inspector for MID_StickyNote.
// Shows a live preview swatch, note list with add/remove/reorder,
// and quick-action buttons.

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Notes;

namespace MidManStudio.Core.EditorUtils.Notes
{
    [CustomEditor(typeof(MID_StickyNote))]
    public class MID_StickyNoteEditor : UnityEditor.Editor
    {
        // ── Serialized properties ─────────────────────────────────────────────

        private SerializedProperty _pTitle;
        private SerializedProperty _pNotes;
        private SerializedProperty _pTextFile;
        private SerializedProperty _pTheme;
        private SerializedProperty _pFontSize;
        private SerializedProperty _pWidth;
        private SerializedProperty _pMaxBodyHeight;
        private SerializedProperty _pAnchor;
        private SerializedProperty _pMargin;
        private SerializedProperty _pDraggable;
        private SerializedProperty _pStartVisible;
        private SerializedProperty _pShowInEditMode;

        // ── State ─────────────────────────────────────────────────────────────

        private bool _foldContent   = true;
        private bool _foldAppear    = true;
        private bool _foldLayout    = false;
        private bool _foldBehaviour = false;

        // Colours
        private static readonly Color ColGreen  = new Color(0.28f, 0.92f, 0.46f, 1f);
        private static readonly Color ColRed    = new Color(1.00f, 0.36f, 0.36f, 1f);
        private static readonly Color ColYellow = new Color(1.00f, 0.88f, 0.22f, 1f);
        private static readonly Color ColDim    = new Color(0.55f, 0.55f, 0.55f, 1f);

        // ── OnEnable ──────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _pTitle          = serializedObject.FindProperty("_title");
            _pNotes          = serializedObject.FindProperty("_notes");
            _pTextFile       = serializedObject.FindProperty("_textFile");
            _pTheme          = serializedObject.FindProperty("_theme");
            _pFontSize       = serializedObject.FindProperty("_fontSize");
            _pWidth          = serializedObject.FindProperty("_width");
            _pMaxBodyHeight  = serializedObject.FindProperty("_maxBodyHeight");
            _pAnchor         = serializedObject.FindProperty("_anchor");
            _pMargin         = serializedObject.FindProperty("_margin");
            _pDraggable      = serializedObject.FindProperty("_draggable");
            _pStartVisible   = serializedObject.FindProperty("_startVisible");
            _pShowInEditMode = serializedObject.FindProperty("_showInEditMode");
        }

        // ── OnInspectorGUI ────────────────────────────────────────────────────

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var note = (MID_StickyNote)target;

            EditorGUILayout.Space(6);
            DrawHeader(note);
            EditorGUILayout.Space(6);

            DrawThemePreview(note);
            EditorGUILayout.Space(4);

            _foldContent   = DrawSection("Content",    _foldContent,   DrawContent);
            _foldAppear    = DrawSection("Appearance", _foldAppear,    DrawAppearance);
            _foldLayout    = DrawSection("Layout",     _foldLayout,    DrawLayout);
            _foldBehaviour = DrawSection("Behaviour",  _foldBehaviour, DrawBehaviour);

            EditorGUILayout.Space(4);
            DrawRuntimeControls(note);

            serializedObject.ApplyModifiedProperties();
        }

        // ── Header ────────────────────────────────────────────────────────────

        private void DrawHeader(MID_StickyNote note)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            var old = GUI.color;
            GUI.color = ColYellow;
            EditorGUILayout.LabelField("📌  Sticky Note", EditorStyles.boldLabel);
            GUI.color = old;
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"v1.0  |  {note.name}", EditorStyles.miniLabel,
                GUILayout.Width(160));
            EditorGUILayout.EndHorizontal();
        }

        // ── Theme preview ─────────────────────────────────────────────────────

        private void DrawThemePreview(MID_StickyNote note)
        {
            var theme = (MID_StickyNote.NoteTheme)_pTheme.enumValueIndex;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Theme Preview", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            // Mini note preview (fixed-size box)
            Rect previewRect = EditorGUILayout.GetControlRect(false, 64f);
            previewRect.x    += 4f;
            previewRect.width -= 8f;

            Color hCol = MID_StickyNote.GetHeaderColor(theme);
            Color bCol = MID_StickyNote.GetBodyColor(theme);
            Color tCol = MID_StickyNote.GetTextColor(theme);

            // Shadow
            EditorGUI.DrawRect(
                new Rect(previewRect.x + 3f, previewRect.y + 3f, previewRect.width, previewRect.height),
                new Color(0, 0, 0, 0.15f));

            // Header strip
            Rect headerStrip = new Rect(previewRect.x, previewRect.y, previewRect.width, 18f);
            EditorGUI.DrawRect(headerStrip, hCol);

            var old = GUI.contentColor;
            GUI.contentColor = Color.white;
            GUI.Label(new Rect(headerStrip.x + 5f, headerStrip.y + 1f,
                headerStrip.width - 40f, 16f),
                $"📌  {_pTitle.stringValue}", EditorStyles.miniBoldLabel);
            GUI.contentColor = old;

            // Mini close/minimize dots
            EditorGUI.DrawRect(new Rect(headerStrip.xMax - 26f, headerStrip.y + 4f, 9f, 9f),
                new Color(1, 1, 1, 0.4f));
            EditorGUI.DrawRect(new Rect(headerStrip.xMax - 14f, headerStrip.y + 4f, 9f, 9f),
                new Color(1, 1, 1, 0.4f));

            // Body
            Rect bodyStrip = new Rect(previewRect.x, previewRect.y + 18f,
                previewRect.width, previewRect.height - 18f);
            EditorGUI.DrawRect(bodyStrip, bCol);

            var bodyStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                richText = true,
            };
            bodyStyle.normal.textColor = tCol;

            string preview = BuildPreviewText();
            GUI.Label(new Rect(bodyStrip.x + 5f, bodyStrip.y + 3f,
                bodyStrip.width - 10f, bodyStrip.height - 6f),
                preview, bodyStyle);

            // Theme name badge
            var badgeStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                fontStyle = FontStyle.Bold,
            };
            badgeStyle.normal.textColor = hCol;
            GUI.Label(new Rect(headerStrip.xMax - 120f, previewRect.yMax + 2f, 120f, 14f),
                theme.ToString(), badgeStyle);

            EditorGUILayout.Space(16f);
            EditorGUILayout.EndVertical();
        }

        private string BuildPreviewText()
        {
            if (_pNotes.arraySize == 0 && _pTextFile.objectReferenceValue == null)
                return "(empty)";
            var sb = new System.Text.StringBuilder();
            int count = Mathf.Min(_pNotes.arraySize, 3);
            for (int i = 0; i < count; i++)
            {
                var elem = _pNotes.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(elem.stringValue))
                    sb.AppendLine(elem.stringValue);
            }
            if (_pNotes.arraySize > 3) sb.Append("…");
            return sb.ToString().TrimEnd();
        }

        // ── Content section ───────────────────────────────────────────────────

        private void DrawContent()
        {
            EditorGUILayout.PropertyField(_pTitle, new GUIContent("Title"));
            EditorGUILayout.Space(4);

            // Notes list — manual with add/remove for clean UX
            EditorGUILayout.LabelField("Notes", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            int removeIdx = -1;
            for (int i = 0; i < _pNotes.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();

                // Index label
                var dimStyle = new GUIStyle(EditorStyles.miniLabel);
                dimStyle.normal.textColor = ColDim;
                EditorGUILayout.LabelField($"[{i}]", dimStyle, GUILayout.Width(24));

                // Text field
                var elem = _pNotes.GetArrayElementAtIndex(i);
                elem.stringValue = EditorGUILayout.TextField(elem.stringValue,
                    GUILayout.ExpandWidth(true));

                // Move up
                GUI.enabled = i > 0;
                if (GUILayout.Button("↑", EditorStyles.miniButton, GUILayout.Width(22)))
                    _pNotes.MoveArrayElement(i, i - 1);
                GUI.enabled = true;

                // Move down
                GUI.enabled = i < _pNotes.arraySize - 1;
                if (GUILayout.Button("↓", EditorStyles.miniButton, GUILayout.Width(22)))
                    _pNotes.MoveArrayElement(i, i + 1);
                GUI.enabled = true;

                // Remove
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22)))
                    removeIdx = i;
                GUI.backgroundColor = oldBg;

                EditorGUILayout.EndHorizontal();
            }

            if (removeIdx >= 0) _pNotes.DeleteArrayElementAtIndex(removeIdx);

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();

            var addBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("+ Add Note", GUILayout.Height(22)))
            {
                _pNotes.InsertArrayElementAtIndex(_pNotes.arraySize);
                _pNotes.GetArrayElementAtIndex(_pNotes.arraySize - 1).stringValue = "";
            }
            GUI.backgroundColor = addBg;

            if (_pNotes.arraySize > 0 &&
                GUILayout.Button("Clear All", GUILayout.Height(22), GUILayout.Width(72)))
            {
                if (EditorUtility.DisplayDialog("Clear Notes",
                    "Remove all note entries?", "Clear", "Cancel"))
                    _pNotes.ClearArray();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(_pTextFile, new GUIContent("Text File (.txt)",
                "Optional TextAsset. Content is appended below the notes list."));
        }

        // ── Appearance section ────────────────────────────────────────────────

        private void DrawAppearance()
        {
            // Theme — shows swatches next to names
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Theme", GUILayout.Width(EditorGUIUtility.labelWidth));
            DrawThemeSwatches();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(_pFontSize,    new GUIContent("Font Size"));
            EditorGUILayout.PropertyField(_pWidth,       new GUIContent("Width (px)"));
            EditorGUILayout.PropertyField(_pMaxBodyHeight, new GUIContent("Max Body Height (px)"));
        }

        private void DrawThemeSwatches()
        {
            var names = System.Enum.GetNames(typeof(MID_StickyNote.NoteTheme));
            int cur   = _pTheme.enumValueIndex;

            for (int i = 0; i < names.Length; i++)
            {
                bool selected = i == cur;
                var old = GUI.backgroundColor;
                GUI.backgroundColor = MID_StickyNote.GetHeaderColor((MID_StickyNote.NoteTheme)i);

                var style = selected
                    ? EditorStyles.miniButtonMid
                    : EditorStyles.miniButtonMid;

                if (GUILayout.Button(new GUIContent("  ", names[i]),
                    GUILayout.Width(26), GUILayout.Height(18)))
                    _pTheme.enumValueIndex = i;

                if (selected)
                {
                    Rect r = GUILayoutUtility.GetLastRect();
                    EditorGUI.DrawRect(new Rect(r.x, r.yMax - 2f, r.width, 2f), ColYellow);
                }

                GUI.backgroundColor = old;
            }
        }

        // ── Layout section ────────────────────────────────────────────────────

        private void DrawLayout()
        {
            EditorGUILayout.PropertyField(_pAnchor,   new GUIContent("Initial Anchor",
                "Corner to place the note when the scene loads. Freely draggable after."));
            EditorGUILayout.PropertyField(_pMargin,   new GUIContent("Margin"));
            EditorGUILayout.PropertyField(_pDraggable, new GUIContent("Draggable"));
        }

        // ── Behaviour section ─────────────────────────────────────────────────

        private void DrawBehaviour()
        {
            EditorGUILayout.PropertyField(_pStartVisible,   new GUIContent("Visible on Start",
                "Whether the note is shown when the scene loads / component enables."));
            EditorGUILayout.PropertyField(_pShowInEditMode, new GUIContent("Show in Edit Mode",
                "Show note in the Game View tab outside of Play Mode."));
        }

        // ── Runtime controls ──────────────────────────────────────────────────

        private void DrawRuntimeControls(MID_StickyNote note)
        {
            if (!Application.isPlaying) return;

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Runtime Controls", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            var old = GUI.backgroundColor;

            // Show/Hide toggle
            bool vis = note.IsVisible;
            GUI.backgroundColor = vis ? ColGreen : new Color(0.5f, 0.5f, 0.5f);
            if (GUILayout.Button(vis ? "Visible" : "Hidden", GUILayout.Height(24)))
                note.Toggle();
            GUI.backgroundColor = old;

            // Minimize toggle
            bool mini = note.IsMinimized;
            GUI.backgroundColor = mini ? ColYellow : new Color(0.5f, 0.5f, 0.5f);
            if (GUILayout.Button(mini ? "Minimized" : "Expanded", GUILayout.Height(24)))
            {
                if (mini) note.Restore(); else note.Minimize();
            }
            GUI.backgroundColor = old;

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            if (Application.isPlaying) Repaint();
        }

        // ── Section helper ────────────────────────────────────────────────────

        private bool DrawSection(string label, bool expanded, System.Action drawContent)
        {
            expanded = EditorGUILayout.BeginFoldoutHeaderGroup(expanded, label);
            if (expanded)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.Space(2);
                drawContent();
                EditorGUILayout.Space(2);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(2);
            return expanded;
        }
    }
}
#endif
