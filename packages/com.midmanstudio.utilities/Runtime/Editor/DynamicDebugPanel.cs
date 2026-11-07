// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/helpers.md, section "DynamicDebugPanel.cs"
// ============================================================================
using UnityEngine;
using System;
using System.Collections.Generic;

namespace MidManStudio.Core.EditorTools
{
    /// <summary>
    /// On-screen debug panel with a Stats view (sections of display, slider, toggle, button and progress-bar
    /// values) and a Logs view, plus built-in FPS, frame time and memory stats. Put it on one GameObject; a second
    /// copy destroys itself. The panel is drawn with IMGUI only inside the Unity Editor (the drawing code is under
    /// <c>#if UNITY_EDITOR</c>), so it is invisible in builds, although its <c>Update</c> still runs there.
    /// </summary>
    public class DynamicDebugPanel : MonoBehaviour
    {
        #region Singleton
        /// <summary>
        /// The live panel, or null. Set in Awake (a duplicate destroys its own GameObject). The panel is not
        /// created on demand, so check for null before use.
        /// </summary>
        public static DynamicDebugPanel Instance { get; private set; }
        #endregion

        #region Configuration
        [Header("Panel Settings")]
        [SerializeField] private bool showPanel = true;
        [SerializeField] private Rect panelRect = new Rect(10, 10, 350, 400);
        [SerializeField] private Rect minimizedRect = new Rect(10, 10, 280, 30);
        [SerializeField] private bool allowDragging = true;

        [Header("Auto-Scroll Settings")]
        [SerializeField] private bool autoScroll = true;
        [SerializeField] private int maxLogEntries = 100;
        #endregion

        #region Panel State
        private enum PanelState
        {
            Expanded,
            Minimized,
            Closed
        }

        private enum ViewMode
        {
            Stats,
            Logs
        }

        private PanelState currentState = PanelState.Expanded;
        private ViewMode currentViewMode = ViewMode.Stats;
        #endregion

        #region Debug Data Structures
        /// <summary>One row of the Stats view.</summary>
        public struct DebugValue
        {
            public string name;
            public object value;
            public DebugValueType type;
            public float minValue;
            public float maxValue;
            /// <summary>Called with the new value for a Slider, and with 0 when a Button is clicked. Toggles have no callback.</summary>
            public System.Action<float> onSliderChanged;
            public string format;

            public DebugValue(string name, object value, DebugValueType type = DebugValueType.Display,
                             float min = 0, float max = 100, System.Action<float> onChanged = null, string format = "F2")
            {
                this.name = name;
                this.value = value;
                this.type = type;
                this.minValue = min;
                this.maxValue = max;
                this.onSliderChanged = onChanged;
                this.format = format;
            }
        }

        /// <summary>How a value is drawn and edited.</summary>
        public enum DebugValueType
        {
            /// <summary>Read-only text, formatted with the value's format string.</summary>
            Display,
            /// <summary>Editable slider between the min and max. Changes call the callback.</summary>
            Slider,
            /// <summary>Editable checkbox. It only changes the stored value; there is no callback and no way to read it back.</summary>
            Toggle,
            /// <summary>Button that calls the callback with 0.</summary>
            Button,
            /// <summary>Bar for a value from 0 to 1. The value is not clamped.</summary>
            ProgressBar
        }

        /// <summary>A titled, collapsible group of values in the Stats view.</summary>
        public struct DebugSection
        {
            public string title;
            public Dictionary<string, DebugValue> values;
            public bool isExpanded;
            public Color titleColor;

            public DebugSection(string title, Color titleColor = default)
            {
                this.title = title;
                this.values = new Dictionary<string, DebugValue>();
                this.isExpanded = true;
                this.titleColor = titleColor == default ? Color.white : titleColor;
            }
        }
        #endregion

        #region Private Variables
        private Dictionary<string, DebugSection> debugSections = new Dictionary<string, DebugSection>();
        private List<string> logEntries = new List<string>();
        private Vector2 scrollPosition = Vector2.zero;
        private bool isDragging = false;
        private Vector2 dragOffset;

        // FPS Tracking
        private float[] fpsBuffer = new float[60];
        private int fpsBufferIndex = 0;
        private float minFPS = float.MaxValue;
        private float maxFPS = 0f;
        private float avgFPS = 0f;

        // GUI Styles
        private GUIStyle _panelStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _miniButtonStyle;
        private bool stylesInitialized = false;
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            // Use showPanel to determine initial panel state
            currentState = showPanel ? PanelState.Expanded : PanelState.Closed;
            InitializeDefaultSections();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            UpdateSystemStats();
        }

#if UNITY_EDITOR
        private void OnGUI()
        {
            if (currentState == PanelState.Closed) return;

            InitializeStyles();

            if (currentState == PanelState.Minimized)
                DrawMinimizedPanel();
            else
                DrawExpandedPanel();
        }
#endif
        #endregion

        #region Initialization
        private void InitializeDefaultSections()
        {
            AddSection("System", Color.cyan);
            AddValue("System", "FPS",       0f, DebugValueType.Display, format: "F1");
            AddValue("System", "FPS (Avg)", 0f, DebugValueType.Display, format: "F1");
            AddValue("System", "FPS (Min)", 0f, DebugValueType.Display, format: "F1");
            AddValue("System", "FPS (Max)", 0f, DebugValueType.Display, format: "F1");
            AddValue("System", "Frame Time",0f, DebugValueType.Display, format: "F2");
            AddValue("System", "Memory",    0f, DebugValueType.Display, format: "F1");
        }

        private void InitializeStyles()
        {
            if (stylesInitialized) return;

            _panelStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTex(1, 1, new Color(0.1f, 0.1f, 0.1f, 0.9f)) },
                padding = new RectOffset(10, 10, 10, 10)
            };

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize  = 12,
                normal    = { textColor = Color.white }
            };

            _valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                normal   = { textColor = Color.gray }
            };

            _buttonStyle     = new GUIStyle(GUI.skin.button) { fontSize = 10 };
            _miniButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 8, padding = new RectOffset(2, 2, 2, 2) };

            stylesInitialized = true;
        }

        private Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var t = new Texture2D(width, height);
            t.SetPixels(pix);
            t.Apply();
            return t;
        }
        #endregion

        #region Panel Drawing
#if UNITY_EDITOR
        private void DrawMinimizedPanel()
        {
            HandleMinimizedPanelDragging();
            GUILayout.BeginArea(minimizedRect, _panelStyle);
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("▼", _miniButtonStyle, GUILayout.Width(25)))
                currentState = PanelState.Expanded;

            GUILayout.Space(10);

            GUI.color = currentViewMode == ViewMode.Stats ? Color.green : Color.white;
            if (GUILayout.Button("Stats", _miniButtonStyle, GUILayout.Width(45)))
            { currentViewMode = ViewMode.Stats; currentState = PanelState.Expanded; }

            GUI.color = currentViewMode == ViewMode.Logs ? Color.yellow : Color.white;
            if (GUILayout.Button("Logs", _miniButtonStyle, GUILayout.Width(45)))
            { currentViewMode = ViewMode.Logs; currentState = PanelState.Expanded; }

            GUI.color = Color.white;
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("X", _miniButtonStyle, GUILayout.Width(20)))
                currentState = PanelState.Closed;

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawExpandedPanel()
        {
            HandlePanelDragging();
            GUILayout.BeginArea(panelRect, _panelStyle);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("▲", _miniButtonStyle, GUILayout.Width(25)))
                currentState = PanelState.Minimized;

            GUILayout.Space(10);

            GUI.color = currentViewMode == ViewMode.Stats ? Color.green : Color.white;
            if (GUILayout.Button("Stats", _buttonStyle, GUILayout.Width(50)))
                currentViewMode = ViewMode.Stats;

            GUI.color = currentViewMode == ViewMode.Logs ? Color.yellow : Color.white;
            if (GUILayout.Button("Logs", _buttonStyle, GUILayout.Width(50)))
                currentViewMode = ViewMode.Logs;

            GUI.color = Color.white;
            GUILayout.FlexibleSpace();

            if (currentViewMode == ViewMode.Stats && GUILayout.Button("Clear Stats", _buttonStyle, GUILayout.Width(75)))
                ClearAllStats();
            else if (currentViewMode == ViewMode.Logs && GUILayout.Button("Clear Logs", _buttonStyle, GUILayout.Width(75)))
                ClearLogs();

            GUILayout.EndHorizontal();
            GUILayout.Space(5);

            scrollPosition = GUILayout.BeginScrollView(scrollPosition);
            if (currentViewMode == ViewMode.Stats) DrawStatsContent();
            else                                    DrawLogsContent();
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        private void DrawStatsContent()
        {
            foreach (var kvp in debugSections)
                DrawSection(kvp.Key, kvp.Value);
        }

        private void DrawLogsContent()
        {
            if (logEntries.Count == 0) { GUILayout.Label("No logs available", _valueStyle); return; }
            for (int i = 0; i < logEntries.Count; i++)
                GUILayout.Label(logEntries[i], _valueStyle);
        }

        private void DrawSection(string sectionKey, DebugSection section)
        {
            GUILayout.Space(5);
            GUI.color = section.titleColor;
            bool expanded = GUILayout.Toggle(section.isExpanded, section.title, _headerStyle);
            GUI.color = Color.white;

            if (expanded != section.isExpanded)
            {
                var updatedSection = section;
                updatedSection.isExpanded = expanded;
                debugSections[sectionKey] = updatedSection;
            }

            if (!expanded) return;

            foreach (var valueKvp in section.values)
                DrawDebugValue(sectionKey, valueKvp.Key, valueKvp.Value);
        }

        private void DrawDebugValue(string sectionKey, string valueKey, DebugValue debugValue)
        {
            GUILayout.BeginHorizontal();

            switch (debugValue.type)
            {
                case DebugValueType.Display:
                    GUILayout.Label($"{debugValue.name}:", _valueStyle, GUILayout.Width(120));
                    GUILayout.Label(FormatValue(debugValue.value, debugValue.format), _valueStyle);
                    break;

                case DebugValueType.Slider:
                    GUILayout.Label($"{debugValue.name}:", _valueStyle, GUILayout.Width(120));
                    float sliderValue = Convert.ToSingle(debugValue.value);
                    float newValue = GUILayout.HorizontalSlider(sliderValue, debugValue.minValue, debugValue.maxValue);
                    GUILayout.Label(FormatValue(newValue, debugValue.format), _valueStyle, GUILayout.Width(50));
                    if (Mathf.Abs(newValue - sliderValue) > 0.001f)
                    {
                        debugValue.onSliderChanged?.Invoke(newValue);
                        UpdateValue(sectionKey, valueKey, newValue);
                    }
                    break;

                case DebugValueType.Toggle:
                    bool toggleValue = Convert.ToBoolean(debugValue.value);
                    bool newToggleValue = GUILayout.Toggle(toggleValue, debugValue.name, GUILayout.Width(150));
                    if (newToggleValue != toggleValue) UpdateValue(sectionKey, valueKey, newToggleValue);
                    break;

                case DebugValueType.Button:
                    if (GUILayout.Button(debugValue.name, _buttonStyle))
                        debugValue.onSliderChanged?.Invoke(0);
                    break;

                case DebugValueType.ProgressBar:
                    GUILayout.Label($"{debugValue.name}:", _valueStyle, GUILayout.Width(120));
                    float progress = Convert.ToSingle(debugValue.value);
                    Rect progressRect = GUILayoutUtility.GetRect(100, 16);
                    GUI.Box(progressRect, "");
                    GUI.Box(new Rect(progressRect.x, progressRect.y, progressRect.width * progress, progressRect.height), "");
                    GUILayout.Label($"{(progress * 100):F0}%", _valueStyle, GUILayout.Width(40));
                    break;
            }

            GUILayout.EndHorizontal();
        }

        private void HandlePanelDragging()
        {
            if (!allowDragging) return;
            Event e = Event.current;
            Rect headerRect = new Rect(panelRect.x, panelRect.y, panelRect.width, 25);
            if (e.type == EventType.MouseDown && headerRect.Contains(e.mousePosition))
            { isDragging = true; dragOffset = e.mousePosition - new Vector2(panelRect.x, panelRect.y); }
            else if (e.type == EventType.MouseDrag && isDragging)
            { panelRect.x = e.mousePosition.x - dragOffset.x; panelRect.y = e.mousePosition.y - dragOffset.y; }
            else if (e.type == EventType.MouseUp)
            { isDragging = false; }
        }

        private void HandleMinimizedPanelDragging()
        {
            if (!allowDragging) return;
            Event e = Event.current;
            if (e.type == EventType.MouseDown && minimizedRect.Contains(e.mousePosition))
            { isDragging = true; dragOffset = e.mousePosition - new Vector2(minimizedRect.x, minimizedRect.y); }
            else if (e.type == EventType.MouseDrag && isDragging)
            { minimizedRect.x = e.mousePosition.x - dragOffset.x; minimizedRect.y = e.mousePosition.y - dragOffset.y; }
            else if (e.type == EventType.MouseUp)
            { isDragging = false; }
        }
#endif
        #endregion

        #region Public API
        /// <summary>Adds an empty section. Does nothing if a section with that name exists.</summary>
        public void AddSection(string sectionName, Color titleColor = default)
        {
            if (!debugSections.ContainsKey(sectionName))
                debugSections[sectionName] = new DebugSection(sectionName, titleColor);
        }

        /// <summary>
        /// Adds or replaces a value in a section, creating the section if needed. For a Slider pass the min, max
        /// and a callback. For a ProgressBar pass a value from 0 to 1.
        /// </summary>
        public void AddValue(string section, string name, object value, DebugValueType type = DebugValueType.Display,
                            float minValue = 0f, float maxValue = 100f, System.Action<float> onChanged = null, string format = "F2")
        {
            if (!debugSections.ContainsKey(section)) AddSection(section);
            var debugSection = debugSections[section];
            debugSection.values[name] = new DebugValue(name, value, type, minValue, maxValue, onChanged, format);
            debugSections[section] = debugSection;
        }

        /// <summary>Sets the stored value of an existing row. Does nothing if the section or row does not exist.</summary>
        public void UpdateValue(string section, string name, object value)
        {
            if (!debugSections.ContainsKey(section) || !debugSections[section].values.ContainsKey(name)) return;
            var debugSection = debugSections[section];
            var debugValue   = debugSection.values[name];
            debugValue.value = value;
            debugSection.values[name] = debugValue;
            debugSections[section]    = debugSection;
        }

        /// <summary>Removes a row from a section. Does nothing if the section does not exist.</summary>
        public void RemoveValue(string section, string name)
        {
            if (!debugSections.ContainsKey(section)) return;
            var debugSection = debugSections[section];
            debugSection.values.Remove(name);
            debugSections[section] = debugSection;
        }

        /// <summary>Adds a time-stamped line to the Logs view. The oldest line is dropped past <c>maxLogEntries</c>.</summary>
        public void AddLog(string message)
        {
            logEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (logEntries.Count > maxLogEntries) logEntries.RemoveAt(0);
            if (autoScroll && currentViewMode == ViewMode.Logs) scrollPosition.y = float.MaxValue;
        }

        /// <summary>Removes all lines from the Logs view.</summary>
        public void ClearLogs()     => logEntries.Clear();

        /// <summary>Removes every section, including the ones you added, then re-creates the built-in System section.</summary>
        public void ClearAllStats()
        {
            debugSections.Clear();
            InitializeDefaultSections();
        }

        /// <summary>Cycles Closed to Expanded, Expanded to Minimized, Minimized to Expanded. It never closes an open panel; use <see cref="SetPanelState"/> for that.</summary>
        public void TogglePanel()
        {
            currentState = currentState switch
            {
                PanelState.Closed    => PanelState.Expanded,
                PanelState.Expanded  => PanelState.Minimized,
                _                    => PanelState.Expanded
            };
        }

        /// <summary>Shows the panel expanded (true) or closes it (false).</summary>
        public void SetPanelState(bool show) =>
            currentState = show ? PanelState.Expanded : PanelState.Closed;

        [ContextMenu("Reset FPS Stats")]
        public void ResetFPSStats()
        {
            for (int i = 0; i < fpsBuffer.Length; i++) fpsBuffer[i] = 0f;
            fpsBufferIndex = 0;
            minFPS = float.MaxValue;
            maxFPS = 0f;
            avgFPS = 0f;
            AddLog("FPS statistics reset");
        }
        #endregion

        #region System Stats Update
        private void UpdateSystemStats()
        {
            float currentFPS = 1f / Time.unscaledDeltaTime;
            fpsBuffer[fpsBufferIndex] = currentFPS;
            fpsBufferIndex = (fpsBufferIndex + 1) % fpsBuffer.Length;

            float sum = 0f;
            for (int i = 0; i < fpsBuffer.Length; i++) sum += fpsBuffer[i];
            avgFPS = sum / fpsBuffer.Length;

            if (currentFPS < minFPS) minFPS = currentFPS;
            if (currentFPS > maxFPS) maxFPS = currentFPS;

            UpdateValue("System", "FPS",        currentFPS);
            UpdateValue("System", "FPS (Avg)",  avgFPS);
            UpdateValue("System", "FPS (Min)",  minFPS);
            UpdateValue("System", "FPS (Max)",  maxFPS);
            UpdateValue("System", "Frame Time", Time.unscaledDeltaTime * 1000f);
            UpdateValue("System", "Memory",     UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1024f / 1024f);
        }
        #endregion


        #region Helper Methods
        private string FormatValue(object value, string format)
        {
            if (value is float f)  return f.ToString(format);
            if (value is double d) return d.ToString(format);
            if (value is int i)    return i.ToString();
            return value?.ToString() ?? "null";
        }
        #endregion
    }
}