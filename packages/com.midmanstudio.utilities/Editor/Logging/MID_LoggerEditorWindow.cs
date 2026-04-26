#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System;

namespace MidManStudio.Core.Logging.EditorCustom
{
    public class MID_LoggerEditorWindow : EditorWindow
    {
        private MID_LoggerSettings _settings;
        private Vector2 _scrollPosition;
        private string _searchFilter = "";

        // Cached MonoBehaviours with log levels
        private List<MonoBehaviourLogInfo> _cachedLogInfos = new List<MonoBehaviourLogInfo>();
        private bool _needsRefresh = true;

        // UI State
        private bool _showGlobalSettings = true;
        private bool _showSceneObjects = true;
        private bool _showQuickActions = true;

        // Grouping
        private bool _groupByGameObject = false;
        private Dictionary<string, List<MonoBehaviourLogInfo>> _groupedInfos = new Dictionary<string, List<MonoBehaviourLogInfo>>();

        [MenuItem("MidManStudio/Logger Manager")]
        public static void ShowWindow()
        {
            var window = GetWindow<MID_LoggerEditorWindow>("Logger Manager");
            window.minSize = new Vector2(550, 450);
            window.Show();
        }

        private void OnEnable()
        {
            LoadSettings();
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnHierarchyChanged()
        {
            _needsRefresh = true;
            Repaint();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            _needsRefresh = true;
            Repaint();
        }

        private void LoadSettings()
        {
            _settings = Resources.Load<MID_LoggerSettings>("MID_LoggerSettings");

            if (_settings == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:MID_LoggerSettings");
                if (guids.Length > 0)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    _settings = AssetDatabase.LoadAssetAtPath<MID_LoggerSettings>(path);
                }
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);

            // ===== HEADER =====
            DrawHeader();

            EditorGUILayout.Space(5);
            DrawSeparator();
            EditorGUILayout.Space(10);

            // ===== GLOBAL SETTINGS =====
            _showGlobalSettings = EditorGUILayout.BeginFoldoutHeaderGroup(_showGlobalSettings, "Global Settings");
            if (_showGlobalSettings)
            {
                DrawGlobalSettings();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            EditorGUILayout.Space(5);
            DrawSeparator();
            EditorGUILayout.Space(10);

            // ===== SCENE OBJECTS =====
            _showSceneObjects = EditorGUILayout.BeginFoldoutHeaderGroup(_showSceneObjects, "Scene MonoBehaviours with Log Levels");
            if (_showSceneObjects)
            {
                DrawSceneObjects();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            EditorGUILayout.Space(5);
            DrawSeparator();
            EditorGUILayout.Space(10);

            // ===== QUICK ACTIONS =====
            _showQuickActions = EditorGUILayout.BeginFoldoutHeaderGroup(_showQuickActions, "Quick Actions");
            if (_showQuickActions)
            {
                DrawQuickActions();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();

            GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft
            };

            EditorGUILayout.LabelField("MID Logger Manager", titleStyle);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Refresh", GUILayout.Width(70), GUILayout.Height(20)))
            {
                _needsRefresh = true;
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawGlobalSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.Space(5);

            // Settings asset reference
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _settings = (MID_LoggerSettings)EditorGUILayout.ObjectField("Settings Asset", _settings, typeof(MID_LoggerSettings), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (_settings != null)
                {
                    EditorUtility.SetDirty(_settings);
                }
            }

            if (GUILayout.Button("Create New", GUILayout.Width(100)))
            {
                CreateNewSettings();
            }
            EditorGUILayout.EndHorizontal();

            if (_settings != null)
            {
                EditorGUILayout.Space(5);
                EditorGUI.BeginChangeCheck();
                var newDefaultLevel = (MID_LogLevel)EditorGUILayout.EnumPopup("Default Log Level", _settings.DefaultLogLevel);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_settings, "Change Default Log Level");
                    _settings.DefaultLogLevel = newDefaultLevel;
                    EditorUtility.SetDirty(_settings);
                }

                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox("Default level used by editor tools when creating new scripts.", MessageType.Info);
            }
            else
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox("No MID_LoggerSettings asset found. Create one to set default levels.", MessageType.Warning);
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.EndVertical();
        }

        private void DrawSceneObjects()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.Space(5);

            // Refresh cache if needed
            if (_needsRefresh)
            {
                RefreshMonoBehaviourList();
                _needsRefresh = false;
            }

            // Search and grouping controls
            EditorGUILayout.BeginHorizontal();
            _searchFilter = EditorGUILayout.TextField("Search", _searchFilter);
            if (GUILayout.Button("Clear", GUILayout.Width(60)))
            {
                _searchFilter = "";
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            _groupByGameObject = EditorGUILayout.ToggleLeft("Group by GameObject", _groupByGameObject, GUILayout.Width(170));

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField($"Total: {_cachedLogInfos.Count}", EditorStyles.miniLabel, GUILayout.Width(80));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // Filter list
            var filteredList = _cachedLogInfos;
            if (!string.IsNullOrEmpty(_searchFilter))
            {
                filteredList = _cachedLogInfos.Where(info =>
                    info.GameObjectName.ToLower().Contains(_searchFilter.ToLower()) ||
                    info.ComponentName.ToLower().Contains(_searchFilter.ToLower())).ToList();
            }

            if (filteredList.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    string.IsNullOrEmpty(_searchFilter)
                        ? "No MonoBehaviours with MID_LogLevel fields found in the scene.\n\nAdd [SerializeField] private MID_LogLevel _logLevel to your scripts."
                        : "No MonoBehaviours match the search filter.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField($"Showing {filteredList.Count} MonoBehaviour(s)", EditorStyles.miniLabel);
                EditorGUILayout.Space(5);

                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(300));

                if (_groupByGameObject)
                {
                    DrawGroupedList(filteredList);
                }
                else
                {
                    DrawFlatList(filteredList);
                }

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.EndVertical();
        }

        private void DrawFlatList(List<MonoBehaviourLogInfo> list)
        {
            foreach (var info in list.OrderBy(i => i.GameObjectName).ThenBy(i => i.ComponentName))
            {
                DrawMonoBehaviourLogInfo(info);
                EditorGUILayout.Space(2);
            }
        }

        private void DrawGroupedList(List<MonoBehaviourLogInfo> list)
        {
            // Group by GameObject
            var grouped = list.GroupBy(info => info.GameObjectName)
                .OrderBy(g => g.Key);

            foreach (var group in grouped)
            {
                // GameObject header
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUILayout.BeginHorizontal();
                GUIStyle headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    normal = { textColor = new Color(0.8f, 0.8f, 1f) }
                };
                EditorGUILayout.LabelField($"GameObject: {group.Key}", headerStyle);

                if (GUILayout.Button("Select", GUILayout.Width(60)))
                {
                    var firstComponent = group.First().Component;
                    if (firstComponent != null)
                    {
                        Selection.activeGameObject = firstComponent.gameObject;
                        EditorGUIUtility.PingObject(firstComponent.gameObject);
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                // Components under this GameObject
                foreach (var info in group.OrderBy(i => i.ComponentName))
                {
                    DrawMonoBehaviourLogInfo(info, true);
                    EditorGUILayout.Space(2);
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(5);
            }
        }

        private void DrawMonoBehaviourLogInfo(MonoBehaviourLogInfo info, bool isGrouped = false)
        {
            if (info.Component == null) return;

            EditorGUILayout.BeginHorizontal(isGrouped ? GUIStyle.none : EditorStyles.helpBox);

            if (!isGrouped)
            {
                EditorGUILayout.Space(5);
            }

            // GameObject and Component name
            string displayName = isGrouped
                ? $"└─ {info.ComponentName}"
                : $"{info.GameObjectName} / {info.ComponentName}";

            EditorGUILayout.LabelField(displayName, GUILayout.Width(280));

            // Log level enum popup
            EditorGUI.BeginChangeCheck();
            var newLevel = (MID_LogLevel)EditorGUILayout.EnumPopup(info.CurrentLogLevel, GUILayout.Width(100));
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(info.Component, "Change Log Level");
                info.LogLevelField.SetValue(info.Component, newLevel);
                EditorUtility.SetDirty(info.Component);
                info.CurrentLogLevel = newLevel;
            }

            GUILayout.FlexibleSpace();

            // Ping button
            if (GUILayout.Button("Select", GUILayout.Width(60)))
            {
                Selection.activeGameObject = info.Component.gameObject;
                EditorGUIUtility.PingObject(info.Component);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawQuickActions()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField("Set All MonoBehaviours to:", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            // First row of buttons
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("None", GUILayout.Height(30)))
            {
                SetAllLogLevels(MID_LogLevel.None);
            }

            if (GUILayout.Button("Error", GUILayout.Height(30)))
            {
                SetAllLogLevels(MID_LogLevel.Error);
            }

            if (GUILayout.Button("Info", GUILayout.Height(30)))
            {
                SetAllLogLevels(MID_LogLevel.Info);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(3);

            // Second row of buttons
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Debug", GUILayout.Height(30)))
            {
                SetAllLogLevels(MID_LogLevel.Debug);
            }

            if (GUILayout.Button("Verbose", GUILayout.Height(30)))
            {
                SetAllLogLevels(MID_LogLevel.Verbose);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // Additional actions
            EditorGUILayout.LabelField("Scene Actions:", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            if (GUILayout.Button("Export Log Levels to Console", GUILayout.Height(25)))
            {
                ExportLogLevelsToConsole();
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.EndVertical();
        }

        private void RefreshMonoBehaviourList()
        {
            _cachedLogInfos.Clear();

            // Find all MonoBehaviours in scene (including inactive)
            MonoBehaviour[] allMonoBehaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();

            foreach (var mb in allMonoBehaviours)
            {
                if (mb == null) continue;

                // Skip editor-only objects and prefabs
                if (mb.gameObject.scene.name == null) continue;

                Type type = mb.GetType();

                // Look for fields of type MID_LogLevel
                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                foreach (var field in fields)
                {
                    if (field.FieldType == typeof(MID_LogLevel))
                    {
                        var currentValue = (MID_LogLevel)field.GetValue(mb);

                        _cachedLogInfos.Add(new MonoBehaviourLogInfo
                        {
                            Component = mb,
                            GameObjectName = mb.gameObject.name,
                            ComponentName = type.Name,
                            LogLevelField = field,
                            CurrentLogLevel = currentValue
                        });

                        break; // Only add once per MonoBehaviour
                    }
                }
            }
        }

        private void SetAllLogLevels(MID_LogLevel level)
        {
            if (_cachedLogInfos.Count == 0) return;

            if (!EditorUtility.DisplayDialog("Set All Log Levels",
                $"Set all {_cachedLogInfos.Count} MonoBehaviour log levels to {level}?",
                "Set All", "Cancel"))
            {
                return;
            }

            int changedCount = 0;

            foreach (var info in _cachedLogInfos)
            {
                if (info.Component != null)
                {
                    Undo.RecordObject(info.Component, "Set All Log Levels");
                    info.LogLevelField.SetValue(info.Component, level);
                    info.CurrentLogLevel = level;
                    EditorUtility.SetDirty(info.Component);
                    changedCount++;
                }
            }

            Debug.Log($"[MID_Logger] Set {changedCount} MonoBehaviour(s) to log level: {level}");

            _needsRefresh = true;
        }

        private void ExportLogLevelsToConsole()
        {
            if (_cachedLogInfos.Count == 0)
            {
                Debug.Log("[MID_Logger] No MonoBehaviours with log levels found in scene.");
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("===== SCENE LOG LEVELS =====");
            sb.AppendLine($"Total MonoBehaviours: {_cachedLogInfos.Count}");
            sb.AppendLine();

            var grouped = _cachedLogInfos
                .OrderBy(i => i.CurrentLogLevel)
                .ThenBy(i => i.GameObjectName)
                .ThenBy(i => i.ComponentName)
                .GroupBy(i => i.CurrentLogLevel);

            foreach (var group in grouped)
            {
                sb.AppendLine($"--- {group.Key} ({group.Count()}) ---");
                foreach (var info in group)
                {
                    sb.AppendLine($"  • {info.GameObjectName} / {info.ComponentName}");
                }
                sb.AppendLine();
            }

            Debug.Log(sb.ToString());
        }

        private void CreateNewSettings()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create MID Logger Settings",
                "MID_LoggerSettings",
                "asset",
                "Create a new MID Logger Settings asset. It should be placed in a Resources folder.");

            if (!string.IsNullOrEmpty(path))
            {
                var newSettings = CreateInstance<MID_LoggerSettings>();
                AssetDatabase.CreateAsset(newSettings, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                _settings = newSettings;
                EditorUtility.SetDirty(_settings);

                if (!path.Contains("/Resources/"))
                {
                    EditorUtility.DisplayDialog("Warning",
                        "The settings asset should be placed in a Resources folder for runtime access.\n\n" +
                        "Current path: " + path,
                        "OK");
                }
            }
        }

        private void DrawSeparator()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 1);
            rect.height = 1;
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        }

        private class MonoBehaviourLogInfo
        {
            public MonoBehaviour Component;
            public string GameObjectName;
            public string ComponentName;
            public FieldInfo LogLevelField;
            public MID_LogLevel CurrentLogLevel;
        }
    }
}
#endif