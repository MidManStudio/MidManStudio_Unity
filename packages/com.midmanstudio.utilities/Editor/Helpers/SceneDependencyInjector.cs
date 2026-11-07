// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/editor-helpers.md, section "SceneDependencyInjector.cs"
// ============================================================================
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.EditorUtils;
namespace MidManStudio.Core.EditorTools
{
    /// <summary>One row of the dependency list: a prefab asset.</summary>
    [System.Serializable]
    public class DependencyItem : IArrayElementTitle
    {
        /// <summary>The prefab to instantiate.</summary>
        public GameObject prefab;

        /// <summary>The prefab's name, or "Missing_Dependency" when no prefab is set.</summary>
        public string Name =>
            prefab == null                    ? "Missing_Dependency" :
            string.IsNullOrWhiteSpace(prefab.name) ? "Unnamed_Prefab"     :
                                               prefab.name;
    }

    /// <summary>
    /// For testing a scene on its own without a bootstrap scene. It lists the manager prefabs the scene needs, and
    /// when Play Mode starts it instantiates each one that is not already present. Injected objects are moved to
    /// DontDestroyOnLoad and are destroyed again when Play Mode ends if cleanupOnStop is on.
    ///
    /// A prefab counts as present when any MonoBehaviour type on its root object already exists in a loaded scene
    /// (active objects only), or when an active object with the prefab's name exists. So a prefab that holds
    /// several managers is skipped as soon as one of them exists. Rows with no prefab are removed by OnValidate.
    ///
    /// Setup: add the component to a GameObject in the test scene, drag the manager prefabs into Required
    /// Dependencies, and press Play. Editor-only code: it is compiled only into the Editor assembly.
    /// </summary>
    [ExecuteInEditMode]
    public class SceneDependencyInjector : MonoBehaviour
    {
        [Header("Dependencies")]
        [Tooltip("Persistent manager prefabs to inject when entering play mode.")]
        [MID_NamedList]
        public List<DependencyItem> requiredDependencies = new List<DependencyItem>();

        [Header("Behaviour")]
        [Tooltip("Inject automatically when play mode starts.")]
        public bool autoInjectOnPlay = true;

        [Tooltip("Destroy injected objects when exiting play mode.")]
        public bool cleanupOnStop = true;

        [Header("Logging")]
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Info;

        [Header("State (read-only)")]
        [SerializeField] private List<GameObject> _injectedObjects = new List<GameObject>();
        [SerializeField] private bool _hasInjected;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            if (autoInjectOnPlay && !_hasInjected) InjectDependencies();
        }

        private void OnDisable()
        {
            
            if (!Application.isPlaying) return;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        // ── Play-mode handling ────────────────────────────────────────────────

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    if (autoInjectOnPlay && !_hasInjected) InjectDependencies();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    if (cleanupOnStop) CleanupInjectedObjects();
                    break;
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Instantiates every listed prefab that is not already present, once. Does nothing when it has already
        /// injected (use <see cref="ForceReinject"/> to run again). Rows without a prefab are skipped with a warning.
        /// </summary>
        [ContextMenu("Inject Dependencies")]
        public void InjectDependencies()
        {
            if (_hasInjected)
            {
                MID_Logger.LogInfo(_logLevel, "Already injected. Use Force Reinject to run again.",
                    nameof(SceneDependencyInjector));
                return;
            }

            MID_Logger.LogInfo(_logLevel, "Starting dependency injection...",
                nameof(SceneDependencyInjector));

            int injected = 0;
            foreach (var item in requiredDependencies)
            {
                if (item?.prefab == null)
                {
                    MID_Logger.LogWarning(_logLevel, "Null entry in dependency list — skipping.",
                        nameof(SceneDependencyInjector));
                    continue;
                }

                if (IsDependencyPresent(item.prefab))
                {
                    MID_Logger.LogDebug(_logLevel, $"✓ {item.prefab.name} already in scene.",
                        nameof(SceneDependencyInjector));
                    continue;
                }

                var obj = InjectSingle(item.prefab);
                if (obj != null)
                {
                    _injectedObjects.Add(obj);
                    injected++;
                    MID_Logger.LogInfo(_logLevel, $"✓ Injected: {item.prefab.name}",
                        nameof(SceneDependencyInjector));
                }
            }

            _hasInjected = true;
            MID_Logger.LogInfo(_logLevel, $"Injection complete. {injected} object(s) added.",
                nameof(SceneDependencyInjector));
        }

        /// <summary>Destroys the objects injected earlier, clears the injected flag and injects again.</summary>
        [ContextMenu("Force Reinject All")]
        public void ForceReinject()
        {
            CleanupInjectedObjects();
            _hasInjected = false;
            InjectDependencies();
        }

        /// <summary>Destroys the objects this component injected and clears the injected flag.</summary>
        [ContextMenu("Cleanup Injected Objects")]
        public void CleanupInjectedObjects()
        {
            if (_injectedObjects.Count == 0)
            {
                MID_Logger.LogInfo(_logLevel, "Nothing to clean up.",
                    nameof(SceneDependencyInjector));
                return;
            }

            int count = 0;
            foreach (var obj in _injectedObjects.ToList())
            {
                if (obj == null) continue;
                MID_Logger.LogDebug(_logLevel, $"Destroying: {obj.name}",
                    nameof(SceneDependencyInjector));
                if (Application.isPlaying) Destroy(obj);
                else DestroyImmediate(obj);
                count++;
            }

            _injectedObjects.Clear();
            _hasInjected = false;
            MID_Logger.LogInfo(_logLevel, $"Cleanup complete. {count} object(s) destroyed.",
                nameof(SceneDependencyInjector));
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Present when ANY MonoBehaviour type on the prefab's root already exists in a loaded scene (active objects
        // only), or an active object with the prefab's name exists.
        private bool IsDependencyPresent(GameObject prefab)
        {
            foreach (var comp in prefab.GetComponents<MonoBehaviour>())
            {
                if (comp == null) continue;
                if (FindObjectsByType(comp.GetType(), FindObjectsSortMode.None).Length > 0)
                    return true;
            }
            return GameObject.Find(prefab.name) != null;
        }

        private GameObject InjectSingle(GameObject prefab)
        {
            try
            {
                var instance = Instantiate(prefab);
                instance.name = prefab.name; // strip "(Clone)"
                if (prefab.scene.name == null) DontDestroyOnLoad(instance); // prefab asset
                return instance;
            }
            catch (System.Exception ex)
            {
                MID_Logger.LogError(_logLevel, $"Failed to inject {prefab.name}: {ex.Message}",
                    nameof(SceneDependencyInjector));
                return null;
            }
        }

        // Drops rows with no prefab, so an empty row added with "+" is removed the next time the component is validated.
        private void OnValidate()
        {
            requiredDependencies ??= new List<DependencyItem>();
            requiredDependencies.RemoveAll(item => item == null || item.prefab == null);
        }
    }

    // ── Custom inspector ──────────────────────────────────────────────────────

    /// <summary>
    /// Inspector for <see cref="SceneDependencyInjector"/>: the settings, a hand-drawn dependency list (add, remove,
    /// clear all, remove nulls), the read-only state, and buttons for inject, force reinject and cleanup. It does
    /// not use the <c>[MID_NamedList]</c> drawer that the list field carries.
    /// </summary>
    [CustomEditor(typeof(SceneDependencyInjector))]
   public class SceneDependencyInjectorEditor : UnityEditor.Editor
    {
        private SerializedProperty _dependencies, _autoInject, _cleanup, _logLevel,
                                   _injected, _hasInjected;

        private void OnEnable()
        {
            _dependencies = serializedObject.FindProperty("requiredDependencies");
            _autoInject   = serializedObject.FindProperty("autoInjectOnPlay");
            _cleanup      = serializedObject.FindProperty("cleanupOnStop");
            _logLevel     = serializedObject.FindProperty("_logLevel");
            _injected     = serializedObject.FindProperty("_injectedObjects");
            _hasInjected  = serializedObject.FindProperty("_hasInjected");
        }

        public override void OnInspectorGUI()
        {
            var inj = (SceneDependencyInjector)target;
            serializedObject.Update();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Scene Dependency Injector", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Drag persistent manager prefabs below. They will be auto-instantiated " +
                "when entering Play Mode if not already present in the scene.",
                MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_autoInject);
            EditorGUILayout.PropertyField(_cleanup);
            EditorGUILayout.PropertyField(_logLevel);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Required Dependencies", EditorStyles.boldLabel);
            DrawDependenciesList(inj);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Runtime State (read-only)", EditorStyles.boldLabel);
            GUI.enabled = false;
            EditorGUILayout.PropertyField(_hasInjected);
            EditorGUILayout.PropertyField(_injected, true);
            GUI.enabled = true;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);

            GUI.backgroundColor = Color.green;
            if (GUILayout.Button("Inject Dependencies", GUILayout.Height(30)))
                inj.InjectDependencies();
            GUI.backgroundColor = Color.white;

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = Color.yellow;
            if (GUILayout.Button("Force Reinject All")) inj.ForceReinject();
            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("Cleanup Injected"))   inj.CleanupInjectedObjects();
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawDependenciesList(SceneDependencyInjector inj)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Dependencies ({_dependencies.arraySize})",
                EditorStyles.boldLabel);
            if (GUILayout.Button("+", GUILayout.Width(25)))
            {
                Undo.RecordObject(inj, "Add Dependency");
                inj.requiredDependencies.Add(new DependencyItem());
                EditorUtility.SetDirty(inj);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            for (int i = 0; i < _dependencies.arraySize; i++)
            {
                var element = _dependencies.GetArrayElementAtIndex(i);
                var prefabProp = element.FindPropertyRelative("prefab");

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                EditorGUILayout.LabelField($"{i}", GUILayout.Width(20));
                EditorGUI.BeginChangeCheck();
                var newPrefab = (GameObject)EditorGUILayout.ObjectField(
                    prefabProp.objectReferenceValue, typeof(GameObject), false);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(inj, "Change Dependency");
                    prefabProp.objectReferenceValue = newPrefab;
                    EditorUtility.SetDirty(inj);
                }
                GUI.backgroundColor = Color.red;
                if (GUILayout.Button("×", GUILayout.Width(22)))
                {
                    Undo.RecordObject(inj, "Remove Dependency");
                    _dependencies.DeleteArrayElementAtIndex(i);
                    EditorUtility.SetDirty(inj);
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                GUI.backgroundColor = Color.white;
                EditorGUILayout.EndHorizontal();
            }

            if (_dependencies.arraySize == 0)
                EditorGUILayout.HelpBox(
                    "No dependencies. Click '+' to add prefabs.", MessageType.Info);

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear All"))
            {
                if (EditorUtility.DisplayDialog("Clear Dependencies",
                    "Remove all entries?", "Yes", "No"))
                {
                    Undo.RecordObject(inj, "Clear Dependencies");
                    _dependencies.ClearArray();
                    EditorUtility.SetDirty(inj);
                }
            }
            if (GUILayout.Button("Remove Nulls"))
            {
                Undo.RecordObject(inj, "Remove Nulls");
                for (int i = _dependencies.arraySize - 1; i >= 0; i--)
                {
                    var p = _dependencies.GetArrayElementAtIndex(i).FindPropertyRelative("prefab");
                    if (p.objectReferenceValue == null) _dependencies.DeleteArrayElementAtIndex(i);
                }
                EditorUtility.SetDirty(inj);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }
    }
}
#endif
