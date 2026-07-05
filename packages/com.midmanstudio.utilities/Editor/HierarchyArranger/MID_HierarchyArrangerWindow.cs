// MidManStudio > Utilities > Hierarchy Arranger

#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using MidManStudio.Core.EditorUtils.HierarchyArranger;

using MidManStudio.Core.EditorTools;
namespace MidManStudio.Core.EditorUtils.HierarchyArranger
{
    public class MID_HierarchyArrangerWindow : EditorWindow
    {
        private readonly MID_HierarchyArrangeOptions _options = new();

        private EnumField _modeField;
        private EnumField _groupOrderField;
        private Toggle    _recurseToggle;
        private Slider    _thresholdSlider;
        private Toggle    _sepEnabledToggle;
        private TextField _sepUnitField;
        private IntegerField _sepCountField;
        private Toggle    _sepLabelToggle;
        private Label     _resultLabel;

        [MenuItem("MidManStudio/Utilities/Hierarchy Arranger", priority = 119)]
        public static void Open()
        {
            var w = GetWindow<MID_HierarchyArrangerWindow>("Hierarchy Arranger");
            w.minSize = new Vector2(380, 420);
        }

        public void CreateGUI()
        {
            var uss  = MidEditorUIHelpers.FindUss("MID_HierarchyArrangerWindow");
            var uxml = MidEditorUIHelpers.FindUxml("MID_HierarchyArrangerWindow");

            if (uxml == null)
            {
                rootVisualElement.Add(new Label("⚠ MID_HierarchyArrangerWindow.uxml not found."));
                return;
            }

            var tree = uxml.Instantiate();
            rootVisualElement.Add(tree);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);

            BindElements();
        }

        private void BindElements()
        {
            _modeField       = rootVisualElement.Q<EnumField>("opt-mode");
            _groupOrderField = rootVisualElement.Q<EnumField>("opt-group-order");
            _recurseToggle   = rootVisualElement.Q<Toggle>("opt-recurse");
            _thresholdSlider = rootVisualElement.Q<Slider>("opt-threshold");
            _sepEnabledToggle = rootVisualElement.Q<Toggle>("opt-sep-enabled");
            _sepUnitField    = rootVisualElement.Q<TextField>("opt-sep-unit");
            _sepCountField   = rootVisualElement.Q<IntegerField>("opt-sep-count");
            _sepLabelToggle  = rootVisualElement.Q<Toggle>("opt-sep-label");
            _resultLabel     = rootVisualElement.Q<Label>("result-label");

            _modeField.Init(_options.mode);
            _modeField.RegisterValueChangedCallback(e => _options.mode = (MID_HierarchyArrangeMode)e.newValue);

            _groupOrderField.Init(_options.groupOrder);
            _groupOrderField.RegisterValueChangedCallback(e => _options.groupOrder = (MID_HierarchyGroupOrder)e.newValue);

            _recurseToggle.value = _options.recurseIntoChildren;
            _recurseToggle.RegisterValueChangedCallback(e => _options.recurseIntoChildren = e.newValue);

            _thresholdSlider.value = _options.similarityThreshold;
            _thresholdSlider.RegisterValueChangedCallback(e => _options.similarityThreshold = e.newValue);

            _sepEnabledToggle.value = _options.separators.enabled;
            _sepEnabledToggle.RegisterValueChangedCallback(e => _options.separators.enabled = e.newValue);

            _sepUnitField.value = _options.separators.repeatUnit;
            _sepUnitField.RegisterValueChangedCallback(e =>
                _options.separators.repeatUnit = string.IsNullOrEmpty(e.newValue) ? "-" : e.newValue);

            _sepCountField.value = _options.separators.repeatCount;
            _sepCountField.RegisterValueChangedCallback(e =>
                _options.separators.repeatCount = Mathf.Clamp(e.newValue, 1, 100));

            _sepLabelToggle.value = _options.separators.includeLabel;
            _sepLabelToggle.RegisterValueChangedCallback(e => _options.separators.includeLabel = e.newValue);

            rootVisualElement.Q<Button>("arrange-selected-btn").clicked += ArrangeSelected;
            rootVisualElement.Q<Button>("arrange-roots-btn").clicked    += ArrangeSceneRoots;
        }

        private void ArrangeSelected()
        {
            var selected = Selection.transforms;
            if (selected == null || selected.Length == 0)
            {
                _resultLabel.text = "Nothing selected — select the parent(s) whose children you want arranged.";
                return;
            }

            int processed = MID_HierarchyArranger.ArrangeMany(selected, _options);
            _resultLabel.text = $"Arranged {processed} object(s) under {selected.Length} selected parent(s).";
        }

        private void ArrangeSceneRoots()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects().Select(g => g.transform);

            // Root objects have no shared parent, so arrange them as one virtual
            // group by wrapping — simplest correct approach: treat the scene
            // itself as the "parent" via a temporary pass over the root list.
            int processed = ArrangeRootLevel(scene, _options);
            _resultLabel.text = $"Arranged {processed} root object(s) in '{scene.name}'.";
        }

        private static int ArrangeRootLevel(UnityEngine.SceneManagement.Scene scene, MID_HierarchyArrangeOptions options)
        {
            // Root GameObjects don't share a Transform parent, so sibling-index
            // tricks need GameObjectUtility's scene root ordering instead.
            var roots = scene.GetRootGameObjects().ToList();
            foreach (var go in roots)
                Undo.RegisterCompleteObjectUndo(go.transform, "Arrange Hierarchy");

            // Reuse the same grouping logic by treating roots as a flat list —
            // easiest correct way: temporarily parent them under a scratch object,
            // arrange, then unparent back to root, preserving the computed order.
            var scratch = new GameObject("~ArrangeScratch~") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(scratch, scene);

            foreach (var go in roots)
                if (go != scratch) Undo.SetTransformParent(go.transform, scratch.transform, "Arrange Hierarchy");

            int processed = MID_HierarchyArranger.Arrange(scratch.transform, options);

            for (int i = scratch.transform.childCount - 1; i >= 0; i--)
            {
                var child = scratch.transform.GetChild(i);
                if (child.GetComponent<Core.HierarchyArranger.MID_HierarchySeparatorMarker>() != null)
                {
                    // Scene root can't hold a bare separator without a parent context
                    // that makes sense visually — drop root-level separators for now.
                    Undo.DestroyObjectImmediate(child.gameObject);
                    continue;
                }
                Undo.SetTransformParent(child, null, "Arrange Hierarchy");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(child.gameObject, scene);
            }

            Undo.DestroyObjectImmediate(scratch);
            return processed;
        }
    }
}
#endif
