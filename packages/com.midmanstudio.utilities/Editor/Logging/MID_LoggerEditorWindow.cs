// MID_LoggerEditorWindow.cs
// Editor window for bulk-managing MID_LogLevel fields across all scene MonoBehaviours.
// Supports: search/filter, file selection, group by GameObject, validated live editing.
// Open via: MidManStudio > Utilities > Logger Manager

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.EditorTools
{
    public class MID_LoggerEditorWindow : EditorWindow
    {
        // ── State ──────────────────────────────────────────────────────────────
        private MID_LoggerSettings     _settings;
        private Vector2                _scrollPos;
        private string                 _searchFilter     = "";
        private bool                   _groupByGO        = false;
        private bool                   _showSelected     = false; // filter to selection only

        private List<MonoBehaviourLogInfo> _allInfos     = new();
        private HashSet<MonoBehaviourLogInfo> _selected  = new();
        private bool                   _needsRefresh     = true;

        // Foldouts
        private bool _showSettings    = true;
        private bool _showObjects     = true;
        private bool _showActions     = true;

        // ── Menu ───────────────────────────────────────────────────────────────
        [MenuItem("MidManStudio/Utilities/Logger Manager")]
        public static void ShowWindow()
        {
            var w = GetWindow<MID_LoggerEditorWindow>("Logger Manager");
            w.minSize = new Vector2(580, 480);
            w.Show();
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────
        private void OnEnable()
        {
            LoadSettings();
            EditorApplication.hierarchyChanged      += MarkDirty;
            EditorApplication.playModeStateChanged  += _ => MarkDirty();
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged      -= MarkDirty;
            EditorApplication.playModeStateChanged  -= _ => MarkDirty();
        }

        private void MarkDirty() { _needsRefresh = true; Repaint(); }

        // ── Main GUI ───────────────────────────────────────────────────────────
        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            DrawHeader();
            DrawSeparator();
            EditorGUILayout.Space(6);

            _showSettings = DrawFoldout(_showSettings, "Global Settings", DrawSettings);
            DrawSeparator();
            _showObjects  = DrawFoldout(_showObjects,  "Scene MonoBehaviours", DrawObjects);
            DrawSeparator();
            _showActions  = DrawFoldout(_showActions,  "Quick Actions", DrawActions);
        }

        // ── Header ─────────────────────────────────────────────────────────────
        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("MID Logger Manager",
                new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Refresh", GUILayout.Width(72), GUILayout.Height(22)))
                MarkDirty();

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        // ── Settings section ───────────────────────────────────────────────────
        private void DrawSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            var prev = _settings;
            _settings = (MID_LoggerSettings)EditorGUILayout.ObjectField(
                "Settings Asset", _settings, typeof(MID_LoggerSettings), false);
            if (_settings != prev && _settings != null) EditorUtility.SetDirty(_settings);

            if (GUILayout.Button("Create New", GUILayout.Width(90)))
                CreateSettings();
            EditorGUILayout.EndHorizontal();

            if (_settings != null)
            {
                EditorGUILayout.Space(4);
                EditorGUI.BeginChangeCheck();
                var newLevel = (MID_LogLevel)EditorGUILayout.EnumPopup(
                    "Default Level", _settings.DefaultLogLevel);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_settings, "Change Default Log Level");
                    _settings.DefaultLogLevel = newLevel;
                    EditorUtility.SetDirty(_settings);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No MID_LoggerSettings asset found.", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        // ── Objects section ────────────────────────────────────────────────────
        private void DrawObjects()
        {
            if (_needsRefresh) { RefreshList(); _needsRefresh = false; }

            // ── Toolbar ────────────────────────────────────────────────────────
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUI.BeginChangeCheck();
            _searchFilter = EditorGUILayout.TextField(_searchFilter,
                EditorStyles.toolbarSearchField, GUILayout.ExpandWidth(true));
            if (EditorGUI.EndChangeCheck()) _selected.Clear();

            if (GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(22)))
            { _searchFilter = ""; _selected.Clear(); GUI.FocusControl(null); }

            _groupByGO   = GUILayout.Toggle(_groupByGO, "Group", EditorStyles.toolbarButton, GUILayout.Width(50));
            _showSelected = GUILayout.Toggle(_showSelected, "Selection only",
                EditorStyles.toolbarButton, GUILayout.Width(94));

            EditorGUILayout.EndHorizontal();

            // ── Selection toolbar ──────────────────────────────────────────────
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            var filtered = GetFiltered();
            EditorGUILayout.LabelField(
                $"Showing {filtered.Count}   |   Selected {_selected.Count}",
                EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

            if (GUILayout.Button("Select All", EditorStyles.miniButton, GUILayout.Width(70)))
                foreach (var i in filtered) _selected.Add(i);

            if (GUILayout.Button("Deselect All", EditorStyles.miniButton, GUILayout.Width(80)))
                _selected.Clear();

            // Apply level to selected only
            if (_selected.Count > 0)
            {
                EditorGUILayout.LabelField("→", GUILayout.Width(14));
                var pick = (MID_LogLevel)EditorGUILayout.EnumPopup(
                    GUIContent.none,
                    _selected.First().CurrentLogLevel,
                    GUILayout.Width(78));

                if (GUILayout.Button("Apply", EditorStyles.miniButton, GUILayout.Width(46)))
                    ApplyLevelToSelected(pick);
            }

            EditorGUILayout.EndHorizontal();

            if (filtered.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    string.IsNullOrEmpty(_searchFilter)
                        ? "No MonoBehaviours with MID_LogLevel fields found.\n" +
                          "Add [SerializeField] private MID_LogLevel _logLevel to your scripts."
                        : "No results match the filter.",
                    MessageType.Info);
                return;
            }

            // ── List ───────────────────────────────────────────────────────────
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.MaxHeight(340));

            if (_groupByGO) DrawGrouped(filtered);
            else            DrawFlat(filtered);

            EditorGUILayout.EndScrollView();
        }

        // ── Actions section ────────────────────────────────────────────────────
        private void DrawActions()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Set ALL scene MonoBehaviours to:", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            BulkButton("None",    MID_LogLevel.None,    new Color(0.5f, 0.5f, 0.5f));
            BulkButton("Error",   MID_LogLevel.Error,   new Color(1f,   0.4f, 0.4f));
            BulkButton("Info",    MID_LogLevel.Info,    new Color(0.4f, 0.8f, 1f));
            BulkButton("Debug",   MID_LogLevel.Debug,   new Color(0.4f, 1f,   0.4f));
            BulkButton("Verbose", MID_LogLevel.Verbose, new Color(1f,   0.8f, 0.4f));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Export Log Levels to Console", GUILayout.Height(24)))
                ExportToConsole();

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        // ── Row drawing ────────────────────────────────────────────────────────
        private void DrawFlat(List<MonoBehaviourLogInfo> list)
        {
            foreach (var info in list.OrderBy(i => i.GameObjectName).ThenBy(i => i.ComponentName))
                DrawRow(info, false);
        }

        private void DrawGrouped(List<MonoBehaviourLogInfo> list)
        {
            foreach (var group in list.GroupBy(i => i.GameObjectName).OrderBy(g => g.Key))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"⬡  {group.Key}",
                    new GUIStyle(EditorStyles.boldLabel)
                    { normal = { textColor = new Color(0.7f, 0.7f, 1f) } });

                if (GUILayout.Button("Select GO", EditorStyles.miniButton, GUILayout.Width(70)))
                {
                    var mb = group.First().Component;
                    if (mb) { Selection.activeGameObject = mb.gameObject; EditorGUIUtility.PingObject(mb.gameObject); }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(2);
                foreach (var info in group.OrderBy(i => i.ComponentName))
                    DrawRow(info, true);

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(3);
            }
        }

        private void DrawRow(MonoBehaviourLogInfo info, bool indented)
        {
            if (info.Component == null) return;

            bool isSelected = _selected.Contains(info);
            var  rowStyle   = isSelected
                ? new GUIStyle(EditorStyles.helpBox)
                  { normal = { background = MakeTex(1, 1, new Color(0.3f, 0.5f, 0.8f, 0.25f)) } }
                : EditorStyles.helpBox;

            EditorGUILayout.BeginHorizontal(rowStyle);

            // Checkbox
            bool nowSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(16));
            if (nowSelected != isSelected)
            {
                if (nowSelected) _selected.Add(info);
                else             _selected.Remove(info);
            }

            // Name
            string label = indented
                ? $"  └  {info.ComponentName}"
                : $"{info.GameObjectName}  /  {info.ComponentName}";
            EditorGUILayout.LabelField(label, GUILayout.MinWidth(200), GUILayout.ExpandWidth(true));

            // Level popup — validated and applied
            EditorGUI.BeginChangeCheck();
            var newLevel = (MID_LogLevel)EditorGUILayout.EnumPopup(
                info.CurrentLogLevel, GUILayout.Width(80));
            if (EditorGUI.EndChangeCheck())
                ApplyLevelToInfo(info, newLevel);

            // Ping / Select
            if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(36)))
            {
                Selection.activeGameObject = info.Component.gameObject;
                EditorGUIUtility.PingObject(info.Component);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ── Apply helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Core validated apply. Uses SerializedObject so Unity marks the
        /// asset/object dirty and the value survives domain reloads and prefab saves.
        /// Falls back to direct reflection if SerializedObject cannot find the property.
        /// </summary>
        private void ApplyLevelToInfo(MonoBehaviourLogInfo info, MID_LogLevel level)
        {
            if (info.Component == null) return;

            Undo.RecordObject(info.Component, "Change Log Level");

            // Primary: SerializedObject path (robust, survives domain reload)
            var so   = new SerializedObject(info.Component);
            var prop = so.FindProperty(info.FieldName);

            if (prop != null)
            {
                prop.enumValueIndex = (int)level;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                // Fallback: direct reflection set
                info.LogLevelField.SetValue(info.Component, level);
            }

            // Verify the value was actually written
            var readBack = (MID_LogLevel)info.LogLevelField.GetValue(info.Component);
            if (readBack != level)
                Debug.LogWarning(
                    $"[MID Logger] Could not write {level} to " +
                    $"{info.ComponentName}.{info.FieldName} — field may be a property.");
            else
                info.CurrentLogLevel = level;

            EditorUtility.SetDirty(info.Component);

            // If we're in prefab mode also mark the prefab stage dirty
#if UNITY_2021_2_OR_NEWER
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(stage.scene);
#endif
        }

        private void ApplyLevelToSelected(MID_LogLevel level)
        {
            foreach (var info in _selected.ToList())
                ApplyLevelToInfo(info, level);
        }

        private void ApplyLevelToAll(MID_LogLevel level)
        {
            if (!EditorUtility.DisplayDialog("Set All Log Levels",
                $"Set all {_allInfos.Count} MonoBehaviour(s) to {level}?", "Set All", "Cancel"))
                return;

            foreach (var info in _allInfos)
                ApplyLevelToInfo(info, level);
        }

        // ── Filtering ──────────────────────────────────────────────────────────
        private List<MonoBehaviourLogInfo> GetFiltered()
        {
            var src = _showSelected && _selected.Count > 0
                ? _allInfos.Where(i => _selected.Contains(i)).ToList()
                : _allInfos;

            if (string.IsNullOrEmpty(_searchFilter)) return src;
            string q = _searchFilter.ToLowerInvariant();
            return src.Where(i =>
                i.GameObjectName.ToLowerInvariant().Contains(q) ||
                i.ComponentName.ToLowerInvariant().Contains(q) ||
                i.FieldName.ToLowerInvariant().Contains(q)).ToList();
        }

        // ── Refresh ────────────────────────────────────────────────────────────
        private void RefreshList()
        {
            _allInfos.Clear();
            _selected.Clear();

            foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null || mb.gameObject.scene.name == null) continue;

                var type = mb.GetType();
                foreach (var field in type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.FieldType != typeof(MID_LogLevel)) continue;

                    var current = (MID_LogLevel)field.GetValue(mb);
                    _allInfos.Add(new MonoBehaviourLogInfo
                    {
                        Component       = mb,
                        GameObjectName  = mb.gameObject.name,
                        ComponentName   = type.Name,
                        FieldName       = field.Name,
                        LogLevelField   = field,
                        CurrentLogLevel = current
                    });
                    break; // one entry per MonoBehaviour (first MID_LogLevel field)
                }
            }
        }

        // ── Utilities ──────────────────────────────────────────────────────────
        private void BulkButton(string label, MID_LogLevel level, Color col)
        {
            var old = GUI.backgroundColor;
            GUI.backgroundColor = col;
            if (GUILayout.Button(label, GUILayout.Height(28)))
                ApplyLevelToAll(level);
            GUI.backgroundColor = old;
        }

        private void ExportToConsole()
        {
            if (_allInfos.Count == 0) { Debug.Log("[MID Logger] No MonoBehaviours found."); return; }
            var sb = new System.Text.StringBuilder("===== SCENE LOG LEVELS =====\n");
            sb.AppendLine($"Total: {_allInfos.Count}");
            foreach (var g in _allInfos
                .OrderBy(i => i.CurrentLogLevel)
                .ThenBy(i => i.GameObjectName)
                .GroupBy(i => i.CurrentLogLevel))
            {
                sb.AppendLine($"\n--- {g.Key} ({g.Count()}) ---");
                foreach (var i in g)
                    sb.AppendLine($"  • {i.GameObjectName} / {i.ComponentName}  [{i.FieldName}]");
            }
            Debug.Log(sb.ToString());
        }

        private bool DrawFoldout(bool state, string title, Action drawContent)
        {
            state = EditorGUILayout.BeginFoldoutHeaderGroup(state, title);
            if (state) drawContent?.Invoke();
            EditorGUILayout.EndFoldoutHeaderGroup();
            return state;
        }

        private void DrawSeparator()
        {
            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.4f));
            EditorGUILayout.Space(4);
        }

        private void LoadSettings()
        {
            _settings = Resources.Load<MID_LoggerSettings>("MID_LoggerSettings");
            if (_settings != null) return;
            var guids = AssetDatabase.FindAssets("t:MID_LoggerSettings");
            if (guids.Length > 0)
                _settings = AssetDatabase.LoadAssetAtPath<MID_LoggerSettings>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        private void CreateSettings()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create MID Logger Settings", "MID_LoggerSettings", "asset",
                "Place in a Resources folder for runtime access.");
            if (string.IsNullOrEmpty(path)) return;

            var asset = CreateInstance<MID_LoggerSettings>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            _settings = asset;

            if (!path.Contains("/Resources/"))
                EditorUtility.DisplayDialog("Warning",
                    "Settings asset should be inside a Resources/ folder.\nCurrent path: " + path, "OK");
        }

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var t = new Texture2D(w, h);
            t.SetPixels(pix); t.Apply();
            return t;
        }

        // ── Data class ─────────────────────────────────────────────────────────
        private class MonoBehaviourLogInfo
        {
            public MonoBehaviour Component;
            public string        GameObjectName;
            public string        ComponentName;
            public string        FieldName;
            public FieldInfo     LogLevelField;
            public MID_LogLevel  CurrentLogLevel;
        }
    }
}
#endif
