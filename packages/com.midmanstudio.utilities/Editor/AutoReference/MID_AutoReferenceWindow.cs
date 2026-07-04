// Bulk auto-reference tool. Scans open scenes for GameObjects carrying any
// [MID_AutoRefable] script, lets you bulk-add MID_AutoRef components to the
// ones missing it (manually or automatically on scan), and bulk-resolve with
// a warnings log (unresolved / ambiguous).
// Open via: MidManStudio > Utilities > Auto Reference

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using MidManStudio.Core.AutoReference;
using MidManStudio.Core.EditorTools;

namespace MidManStudio.Core.EditorUtils.AutoReference
{
    public class MID_AutoReferenceWindow : EditorWindow
    {
        private enum LogSeverity { Ok, Ambiguous, Warn }

        private readonly struct LogEntry
        {
            public readonly string Text;
            public readonly LogSeverity Severity;
            public LogEntry(string text, LogSeverity severity) { Text = text; Severity = severity; }
        }

        // ── State ──────────────────────────────────────────────────────────────

        private readonly MID_AutoRefOptions _options    = new();
        private readonly List<GameObject>   _targets     = new();
        private readonly List<LogEntry>     _logEntries  = new();

        private bool _autoAddOnScan; // off by default — a scan never mutates the scene on its own unless you opt in

        private ListView _targetsList;
        private ListView _logList;
        private Label    _statsLabel;

        private Toggle       _overwriteToggle;
        private Toggle       _includeChildrenToggle;
        private Toggle       _includeInactiveToggle;
        private Toggle       _includeExternalToggle;
        private ObjectField  _externalRootField;
        private Toggle       _logUnresolvedToggle;
        private Toggle       _logAmbiguousToggle;
        private Toggle       _autoAddOnScanToggle;

        // ── Menu ───────────────────────────────────────────────────────────────

        [MenuItem("MidManStudio/Utilities/Auto Reference", priority = 118)]
        public static void Open()
        {
            var w = GetWindow<MID_AutoReferenceWindow>("Auto Reference");
            w.minSize = new Vector2(600, 480);
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public void CreateGUI()
        {
            var uss  = MidEditorUIHelpers.FindUss("MID_AutoReferenceWindow");
            var uxml = MidEditorUIHelpers.FindUxml("MID_AutoReferenceWindow");

            if (uxml == null)
            {
                rootVisualElement.Add(new Label(
                    "⚠  MID_AutoReferenceWindow.uxml not found. Check it is inside an Editor folder."));
                return;
            }

            var tree = uxml.Instantiate();
            rootVisualElement.Add(tree);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);

            BindElements();
            ScanScene();
        }

        // ── Binding ────────────────────────────────────────────────────────────

        private void BindElements()
        {
            rootVisualElement.Q<Button>("refresh-btn").clicked           += ScanScene;
            rootVisualElement.Q<Button>("scan-btn").clicked              += ScanScene;
            rootVisualElement.Q<Button>("add-missing-btn").clicked       += AddMissingComponents;
            rootVisualElement.Q<Button>("resolve-selected-btn").clicked  += ResolveSelected;
            rootVisualElement.Q<Button>("resolve-all-btn").clicked       += () => ResolveTargets(_targets);

            _overwriteToggle       = rootVisualElement.Q<Toggle>("opt-overwrite");
            _includeChildrenToggle = rootVisualElement.Q<Toggle>("opt-include-children");
            _includeInactiveToggle = rootVisualElement.Q<Toggle>("opt-include-inactive");
            _includeExternalToggle = rootVisualElement.Q<Toggle>("opt-include-external");
            _externalRootField     = rootVisualElement.Q<ObjectField>("opt-external-root");
            _logUnresolvedToggle   = rootVisualElement.Q<Toggle>("opt-log-unresolved");
            _logAmbiguousToggle    = rootVisualElement.Q<Toggle>("opt-log-ambiguous");
            _autoAddOnScanToggle   = rootVisualElement.Q<Toggle>("opt-auto-add-on-scan");
            BindOptions();

            _targetsList = rootVisualElement.Q<ListView>("targets-list");
            _logList     = rootVisualElement.Q<ListView>("log-list");
            _statsLabel  = rootVisualElement.Q<Label>("stats-label");

            SetupTargetsList();
            SetupLogList();
        }

        private void BindOptions()
        {
            _overwriteToggle.value = _options.overwriteExisting;
            _overwriteToggle.RegisterValueChangedCallback(e => _options.overwriteExisting = e.newValue);

            _includeChildrenToggle.value = _options.includeChildren;
            _includeChildrenToggle.RegisterValueChangedCallback(e => _options.includeChildren = e.newValue);

            _includeInactiveToggle.value = _options.includeInactiveChildren;
            _includeInactiveToggle.RegisterValueChangedCallback(e => _options.includeInactiveChildren = e.newValue);

            _includeExternalToggle.value = _options.includeExternalRoot;
            _includeExternalToggle.RegisterValueChangedCallback(e => _options.includeExternalRoot = e.newValue);

            _externalRootField.value = _options.externalSearchRoot;
            _externalRootField.RegisterValueChangedCallback(e => _options.externalSearchRoot = e.newValue as Transform);

            _logUnresolvedToggle.value = _options.logUnresolved;
            _logUnresolvedToggle.RegisterValueChangedCallback(e => _options.logUnresolved = e.newValue);

            _logAmbiguousToggle.value = _options.logAmbiguousResolved;
            _logAmbiguousToggle.RegisterValueChangedCallback(e => _options.logAmbiguousResolved = e.newValue);

            _autoAddOnScanToggle.value = _autoAddOnScan; // false by default
            _autoAddOnScanToggle.RegisterValueChangedCallback(e => _autoAddOnScan = e.newValue);
        }

        private void SetupTargetsList()
        {
            _targetsList.makeItem = () =>
            {
                var row = new VisualElement();
                row.AddToClassList("mid-row");
                var dot = new Label { name = "dot" };
                dot.AddToClassList("mid-row__dot");
                var label = new Label { name = "label" };
                label.AddToClassList("mid-row__label");
                row.Add(dot);
                row.Add(label);
                return row;
            };

            _targetsList.bindItem = (el, i) =>
            {
                var go = _targets[i];
                bool hasComponent = go.GetComponent<MID_AutoRef>() != null;

                var dot   = el.Q<Label>("dot");
                var label = el.Q<Label>("label");

                dot.text = hasComponent ? "●" : "○";
                dot.EnableInClassList("mid-row__dot--on",  hasComponent);
                dot.EnableInClassList("mid-row__dot--off", !hasComponent);
                label.text = go.name;
            };

            _targetsList.itemsSource   = _targets;
            _targetsList.selectionType = SelectionType.Multiple;
            _targetsList.selectionChanged += sel =>
            {
                var gos = sel.Cast<GameObject>().ToArray();
                if (gos.Length == 0) return;
                Selection.objects = gos;
                EditorGUIUtility.PingObject(gos[0]);
            };
        }

        private void SetupLogList()
        {
            _logList.makeItem = () => new Label();
            _logList.bindItem = (el, i) =>
            {
                var entry = _logEntries[i];
                var label = (Label)el;
                label.text = entry.Text;
                label.ClearClassList();
                label.AddToClassList("mid-log");
                label.AddToClassList(entry.Severity switch
                {
                    LogSeverity.Warn      => "mid-log--warn",
                    LogSeverity.Ambiguous => "mid-log--ambiguous",
                    _                     => "mid-log--ok"
                });
            };
            _logList.itemsSource = _logEntries;
        }

        // ── Actions ────────────────────────────────────────────────────────────

        private void ScanScene()
        {
            _targets.Clear();
            var found = new HashSet<GameObject>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    if (MID_AutoReferenceResolver.IsAutoRefable(mb.GetType()))
                        found.Add(mb.gameObject);
                }
            }

            _targets.AddRange(found.OrderBy(g => g.name));
            _targetsList.RefreshItems();
            _statsLabel.text = $"{_targets.Count} object(s) with [MID_AutoRefable] scripts found.";

            if (_autoAddOnScan) AddMissingComponents();
        }

        private void AddMissingComponents()
        {
            int added = 0;
            foreach (var go in _targets)
            {
                if (go.GetComponent<MID_AutoRef>() != null) continue;
                Undo.AddComponent<MID_AutoRef>(go); // duplicate-safe: checked above + [DisallowMultipleComponent] backstop
                added++;
            }
            _targetsList.RefreshItems();
            _statsLabel.text = $"Added MID_AutoRef to {added} object(s).";
        }

        private void ResolveSelected()
        {
            var selected = _targetsList.selectedItems.Cast<GameObject>().ToList();
            if (selected.Count == 0)
            {
                _statsLabel.text = "No targets selected.";
                return;
            }
            ResolveTargets(selected);
        }

        private void ResolveTargets(IEnumerable<GameObject> targets)
        {
            _logEntries.Clear();
            int assigned = 0, skipped = 0, unresolved = 0, ambiguous = 0;

            foreach (var go in targets)
            {
                foreach (var r in MID_AutoReferenceResolver.Resolve(go, _options))
                {
                    switch (r.Outcome)
                    {
                        case MID_AutoRefOutcome.Assigned:
                            assigned++;
                            _logEntries.Add(new LogEntry(
                                $"✓  {go.name} · {r.ScriptTypeName}.{r.FieldName} → {r.AssignedObjectName}",
                                LogSeverity.Ok));
                            break;

                        case MID_AutoRefOutcome.AmbiguousResolved:
                            ambiguous++;
                            _logEntries.Add(new LogEntry(
                                $"~  {go.name} · {r.ScriptTypeName}.{r.FieldName} → {r.AssignedObjectName} " +
                                $"({r.CandidateCount} candidates, score {r.MatchScore:F2})",
                                LogSeverity.Ambiguous));
                            break;

                        case MID_AutoRefOutcome.NoCandidates:
                            unresolved++;
                            _logEntries.Add(new LogEntry(
                                $"✗  {go.name} · {r.ScriptTypeName}.{r.FieldName} — no match found ({r.FieldTypeName})",
                                LogSeverity.Warn));
                            break;

                        case MID_AutoRefOutcome.SkippedAlreadySet:
                            skipped++;
                            break;
                    }
                }
            }

            _logList.RefreshItems();
            _statsLabel.text = $"{assigned} assigned · {ambiguous} ambiguous · {unresolved} unresolved · {skipped} already set";
        }
    }
}
#endif
