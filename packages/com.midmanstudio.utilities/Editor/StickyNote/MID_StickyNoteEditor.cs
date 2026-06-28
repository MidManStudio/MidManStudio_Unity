
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Notes;

namespace MidManStudio.Core.EditorUtils.Notes
{
    /// <summary>
    /// Custom inspector for MID_StickyNote.
    /// Shows a live theme preview, note list with add/remove/reorder,
    /// and quick-action runtime controls.
    /// </summary>
    [CustomEditor(typeof(MID_StickyNote))]
    public class MID_StickyNoteEditor : UnityEditor.Editor
    {
        // ── Serialized properties ─────────────────────────────────────────────
       
        private SerializedProperty _pTitle;
        private SerializedProperty _pNotes;
        private SerializedProperty _pTextFile;
        private SerializedProperty _pTheme;
        private SerializedProperty _pFontSize;
        private SerializedProperty _pUseCustomTextColor;
        private SerializedProperty _pCustomTextColor;
        private SerializedProperty _pWidth;
        private SerializedProperty _pMaxBodyHeight;
        private SerializedProperty _pCornerPadding;
        private SerializedProperty _pAnchor;
        private SerializedProperty _pMargin;
        private SerializedProperty _pDraggable;
        private SerializedProperty _pSortingOrder;
        private SerializedProperty _pStartVisible;
        private SerializedProperty _pBuildInEditMode;

        // ── State ─────────────────────────────────────────────────────────────

        private bool _foldContent   = true;
        private bool _foldAppear    = true;
        private bool _foldLayout    = false;
        private bool _foldBehaviour = false;

        private static readonly Color ColGreen  = new Color(0.28f, 0.92f, 0.46f, 1f);
        private static readonly Color ColYellow = new Color(1.00f, 0.88f, 0.22f, 1f);
        private static readonly Color ColDim    = new Color(0.55f, 0.55f, 0.55f, 1f);

        // ── OnEnable ──────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _pTitle             = serializedObject.FindProperty("_title");
            _pNotes             = serializedObject.FindProperty("_notes");
            _pTextFile          = serializedObject.FindProperty("_textFile");
            _pTheme             = serializedObject.FindProperty("_theme");
            _pFontSize          = serializedObject.FindProperty("_fontSize");
            _pUseCustomTextColor = serializedObject.FindProperty("_useCustomTextColor");
            _pCustomTextColor   = serializedObject.FindProperty("_customTextColor");
            _pWidth             = serializedObject.FindProperty("_width");
            _pMaxBodyHeight     = serializedObject.FindProperty("_maxBodyHeight");
            _pCornerPadding     = serializedObject.FindProperty("_cornerPadding");
            _pAnchor            = serializedObject.FindProperty("_anchor");
            _pMargin            = serializedObject.FindProperty("_margin");
            _pDraggable         = serializedObject.FindProperty("_draggable");
            _pSortingOrder      = serializedObject.FindProperty("_sortingOrder");
            _pStartVisible      = serializedObject.FindProperty("_startVisible");
            _pBuildInEditMode   = serializedObject.FindProperty("_buildInEditMode");
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

            EditorGUILayout.HelpBox(
                "Position and appearance preview in the Game View even outside Play Mode.\n" +
                "Dragging, minimizing, and closing require Play Mode — same as any other UI button.",
                MessageType.Info);
            EditorGUILayout.Space(4);

            _foldContent   = DrawSection("Content",    _foldContent,   DrawContent);
            _foldAppear    = DrawSection("Appearance", _foldAppear,    DrawAppearance);
            _foldLayout    = DrawSection("Layout",     _foldLayout,    DrawLayout);
            _foldBehaviour = DrawSection("Behaviour",  _foldBehaviour, DrawBehaviour);

            EditorGUILayout.Space(4);
            DrawUtilityButtons(note);
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
            EditorGUILayout.LabelField($"v2.0 (UGUI)  |  {note.name}", EditorStyles.miniLabel,
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

            Rect previewRect = EditorGUILayout.GetControlRect(false, 64f);
            previewRect.x     += 4f;
            previewRect.width -= 8f;

            Color hCol = MID_StickyNote.GetHeaderColor(theme);
            Color bCol = MID_StickyNote.GetBodyColor(theme);
            Color tCol = _pUseCustomTextColor.boolValue
                ? _pCustomTextColor.colorValue
                : MID_StickyNote.GetTextColor(theme);

            EditorGUI.DrawRect(
                new Rect(previewRect.x + 3f, previewRect.y + 3f, previewRect.width, previewRect.height),
                new Color(0, 0, 0, 0.15f));

            Rect headerStrip = new Rect(previewRect.x, previewRect.y, previewRect.width, 18f);
            EditorGUI.DrawRect(headerStrip, hCol);

            var old = GUI.contentColor;
            GUI.contentColor = Color.white;
            GUI.Label(new Rect(headerStrip.x + 5f, headerStrip.y + 1f, headerStrip.width - 40f, 16f),
                $"📌  {_pTitle.stringValue}", EditorStyles.miniBoldLabel);
            GUI.contentColor = old;

            EditorGUI.DrawRect(new Rect(headerStrip.xMax - 26f, headerStrip.y + 4f, 9f, 9f),
                new Color(1, 1, 1, 0.4f));
            EditorGUI.DrawRect(new Rect(headerStrip.xMax - 14f, headerStrip.y + 4f, 9f, 9f),
                new Color(1, 1, 1, 0.4f));

            Rect bodyStrip = new Rect(previewRect.x, previewRect.y + 18f,
                previewRect.width, previewRect.height - 18f);
            EditorGUI.DrawRect(bodyStrip, bCol);

            var bodyStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, richText = true };
            bodyStyle.normal.textColor = tCol;

            GUI.Label(new Rect(bodyStrip.x + 5f, bodyStrip.y + 3f, bodyStrip.width - 10f, bodyStrip.height - 6f),
                BuildPreviewText(), bodyStyle);

            var badgeStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontStyle = FontStyle.Bold };
            badgeStyle.normal.textColor = hCol;
            GUI.Label(new Rect(headerStrip.xMax - 120f, previewRect.yMax + 2f, 120f, 14f),
                theme.ToString(), badgeStyle);

            EditorGUILayout.Space(16f);
            EditorGUILayout.EndVertical();
        }

        private string BuildPreviewText()
        {
            if (_pNotes.arraySize == 0 && _pTextFile.objectReferenceValue == null) return "(empty)";
            var sb = new System.Text.StringBuilder();
            int count = Mathf.Min(_pNotes.arraySize, 3);
            for (int i = 0; i < count; i++)
            {
                var elem = _pNotes.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(elem.stringValue)) sb.AppendLine(elem.stringValue);
            }
            if (_pNotes.arraySize > 3) sb.Append("…");
            return sb.ToString().TrimEnd();
        }

        // ── Content section ───────────────────────────────────────────────────

        private void DrawContent()
        {
            EditorGUILayout.PropertyField(_pTitle, new GUIContent("Title"));
            EditorGUILayout.Space(4);

            EditorGUILayout.LabelField("Notes", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            int removeIdx = -1;
            for (int i = 0; i < _pNotes.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();

                var dimStyle = new GUIStyle(EditorStyles.miniLabel);
                dimStyle.normal.textColor = ColDim;
                EditorGUILayout.LabelField($"[{i}]", dimStyle, GUILayout.Width(24));

                var elem = _pNotes.GetArrayElementAtIndex(i);
                elem.stringValue = EditorGUILayout.TextField(elem.stringValue, GUILayout.ExpandWidth(true));

                GUI.enabled = i > 0;
                if (GUILayout.Button("↑", EditorStyles.miniButton, GUILayout.Width(22)))
                    _pNotes.MoveArrayElement(i, i - 1);
                GUI.enabled = true;

                GUI.enabled = i < _pNotes.arraySize - 1;
                if (GUILayout.Button("↓", EditorStyles.miniButton, GUILayout.Width(22)))
                    _pNotes.MoveArrayElement(i, i + 1);
                GUI.enabled = true;

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
                if (EditorUtility.DisplayDialog("Clear Notes", "Remove all note entries?", "Clear", "Cancel"))
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
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Theme", GUILayout.Width(EditorGUIUtility.labelWidth));
            DrawThemeSwatches();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(_pFontSize,      new GUIContent("Font Size"));

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(_pUseCustomTextColor,
                new GUIContent("Use Custom Text Color"));
            if (_pUseCustomTextColor.boolValue)
                EditorGUILayout.PropertyField(_pCustomTextColor, new GUIContent("Text Color"));

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(_pWidth,         new GUIContent("Width (px)"));
            EditorGUILayout.PropertyField(_pMaxBodyHeight, new GUIContent("Max Body Height (px)"));
            EditorGUILayout.PropertyField(_pCornerPadding, new GUIContent("Inner Text Padding"));
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

                if (GUILayout.Button(new GUIContent("  ", names[i]), GUILayout.Width(26), GUILayout.Height(18)))
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
            EditorGUILayout.PropertyField(_pAnchor, new GUIContent("Initial Anchor",
                "Corner to place the note when the scene loads. Freely draggable after (Play Mode)."));
            EditorGUILayout.PropertyField(_pMargin,    new GUIContent("Margin"));
            EditorGUILayout.PropertyField(_pDraggable, new GUIContent("Draggable",
                "Only functions in Play Mode — same as any other draggable UI element."));
            EditorGUILayout.PropertyField(_pSortingOrder, new GUIContent("Canvas Sorting Order",
                "Higher values draw on top of your game's own UI canvas."));
        }

        // ── Behaviour section ─────────────────────────────────────────────────

        private void DrawBehaviour()
        {
            EditorGUILayout.PropertyField(_pStartVisible, new GUIContent("Visible on Start"));
            EditorGUILayout.PropertyField(_pBuildInEditMode, new GUIContent("Build in Edit Mode",
                "Preview the note in the Game View while not in Play Mode."));
        }

        // ── Utility / runtime controls ────────────────────────────────────────

        private void DrawUtilityButtons(MID_StickyNote note)
        {
            EditorGUILayout.Space(2);
            if (GUILayout.Button("⟳  Rebuild Note", GUILayout.Height(24)))
                note.RebuildNow();
        }

        private void DrawRuntimeControls(MID_StickyNote note)
        {
            if (!Application.isPlaying) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Runtime Controls", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            var old = GUI.backgroundColor;

            bool vis = note.IsVisible;
            GUI.backgroundColor = vis ? ColGreen : new Color(0.5f, 0.5f, 0.5f);
            if (GUILayout.Button(vis ? "Visible" : "Hidden", GUILayout.Height(24)))
                note.Toggle();
            GUI.backgroundColor = old;

            bool mini = note.IsMinimized;
            GUI.backgroundColor = mini ? ColYellow : new Color(0.5f, 0.5f, 0.5f);
            if (GUILayout.Button(mini ? "Minimized" : "Expanded", GUILayout.Height(24)))
            {
                if (mini) note.Restore(); else note.Minimize();
            }
            GUI.backgroundColor = old;

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            Repaint();
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
