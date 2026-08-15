// Scriptable Object Viewer — scans every ScriptableObject asset in the
// project, groups them by concrete class name (e.g. every ProjectileConfigSO
// lands in its own category regardless of which folder it lives in), and
// lets you browse category-to-category via a dropdown, search/filter across
// names, and preview+edit the selected asset inline via a live embedded
// inspector — without leaving this window or hunting through Project folders.
//
// Open via: MidManStudio > Utilities > Scriptable Object Viewer
//
// CATEGORIZATION: keyed by asset.GetType() (the concrete runtime type), not
// by folder or by base type — a ProjectileConfigSO and a ProjectilePatternSO
// sitting in the same folder land in two different categories, exactly as
// asked for ("arranges and categorizes based on their class name").
//
// LIVE INSPECTOR: the detail panel uses UnityEditor.Editor.CreateEditor +
// an IMGUIContainer, so it renders whatever CustomEditor (if any) the
// selected type actually has registered — including MID_BaseSOEditor for
// anything deriving from MID_BaseSO — rather than a generic read-only
// dump. Edits made there are real, immediate SerializedObject edits on the
// asset, same as the normal Inspector.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using MidManStudio.Core;
using MidManStudio.Core.EditorTools;

namespace MidManStudio.Core.EditorUtils.ScriptableObjectViewer
{
    public class MID_ScriptableObjectViewerWindow : EditorWindow
    {
        private const string ALL_CATEGORIES = "All Categories";

        private readonly struct Entry
        {
            public readonly ScriptableObject Asset;
            public readonly string           TypeName;
            public readonly string           AssetPath;
            public Entry(ScriptableObject asset, string typeName, string assetPath)
            {
                Asset = asset; TypeName = typeName; AssetPath = assetPath;
            }
        }

        // ── State ─────────────────────────────────────────────────────────────

        private const int PAGE_SIZE = 50;

        private readonly List<Entry>  _allEntries      = new();
        private readonly List<Entry>  _filteredEntries = new();
        private readonly List<Entry>  _pagedEntries    = new();
        private readonly List<string> _categoryChoices = new();

        private int _currentPage; // 0-indexed

        private string     _selectedCategory = ALL_CATEGORIES;
        private string     _searchText       = string.Empty;
        private DefaultAsset _folderScope;

        private ScriptableObject _selectedAsset;
        private Editor            _cachedEditor;

        // UI refs
        private DropdownField _categoryDropdown;
        private ToolbarSearchField _searchField;
        private ObjectField   _folderScopeField;
        private ListView       _resultsList;
        private Label          _statsLabel;
        private Label          _detailTitle;
        private IMGUIContainer _detailImgui;
        private VisualElement  _detailEmptyState;
        private Label          _pageLabel;
        private Button         _prevPageButton;
        private Button         _nextPageButton;

        // ── Menu ──────────────────────────────────────────────────────────────

        [MenuItem("MidManStudio/Utilities/Scriptable Object Viewer", priority = 120)]
        public static void Open()
        {
            var w = GetWindow<MID_ScriptableObjectViewerWindow>("SO Viewer");
            w.minSize = new Vector2(640, 420);
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void CreateGUI()
        {
            var uss  = MidEditorUIHelpers.FindUss("MID_ScriptableObjectViewerWindow");
            var uxml = MidEditorUIHelpers.FindUxml("MID_ScriptableObjectViewerWindow");

            if (uxml == null)
            {
                rootVisualElement.Add(new Label(
                    "⚠  MID_ScriptableObjectViewerWindow.uxml not found. Check it is inside an Editor folder."));
                return;
            }

            var tree = uxml.Instantiate();
            rootVisualElement.Add(tree);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);

            BindElements();

            // UI BUG FIX ("category dropdown only appears after typing in the
            // search bar"): populating a DropdownField's choices/value
            // synchronously inside CreateGUI() can race the window's own
            // first layout pass — the control exists in the tree but its
            // internal label hasn't been through a layout/repaint cycle yet,
            // so it renders empty until SOMETHING else (e.g. typing, which
            // touches layout via the search field's callback) forces a
            // repaint. Deferring Rescan() to run after that first pass — the
            // standard, documented fix for "my UI Toolkit window's initial
            // state doesn't render right" — avoids the race entirely rather
            // than working around its symptom.
            rootVisualElement.schedule.Execute(Rescan).ExecuteLater(0);
        }

        private void OnDestroy()
        {
            if (_cachedEditor != null) DestroyImmediate(_cachedEditor);
        }

        // ── Binding ───────────────────────────────────────────────────────────

        private void BindElements()
        {
            rootVisualElement.Q<Button>("refresh-btn").clicked += Rescan;

            _categoryDropdown = rootVisualElement.Q<DropdownField>("category-dropdown");
            _categoryDropdown.RegisterValueChangedCallback(e =>
            {
                _selectedCategory = e.newValue;
                ApplyFilters();
            });

            _searchField = rootVisualElement.Q<ToolbarSearchField>("search-field");
            _searchField.RegisterValueChangedCallback(e =>
            {
                _searchText = e.newValue ?? string.Empty;
                ApplyFilters();
            });

            _folderScopeField = rootVisualElement.Q<ObjectField>("folder-scope-field");
            _folderScopeField.objectType = typeof(DefaultAsset);
            _folderScopeField.RegisterValueChangedCallback(e =>
            {
                _folderScope = e.newValue as DefaultAsset;
                // A non-folder DefaultAsset (e.g. a stray .txt) makes AssetDatabase.
                // IsValidFolder false — treated the same as "no scope" rather than
                // silently filtering everything out.
                if (_folderScope != null && !AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(_folderScope)))
                    _folderScope = null;
                ApplyFilters();
            });

            _resultsList = rootVisualElement.Q<ListView>("results-list");
            SetupResultsList();

            _pageLabel      = rootVisualElement.Q<Label>("page-label");
            _prevPageButton = rootVisualElement.Q<Button>("prev-page-btn");
            _nextPageButton = rootVisualElement.Q<Button>("next-page-btn");
            _prevPageButton.clicked += () => GoToPage(-1);
            _nextPageButton.clicked += () => GoToPage(1);

            _statsLabel       = rootVisualElement.Q<Label>("stats-label");
            _detailTitle      = rootVisualElement.Q<Label>("detail-title");
            _detailEmptyState = rootVisualElement.Q<VisualElement>("detail-empty");

            var detailScroll = rootVisualElement.Q<ScrollView>("detail-scroll");
            _detailImgui = new IMGUIContainer(DrawDetailIMGUI);
            _detailImgui.style.flexGrow = 1;
            detailScroll.Add(_detailImgui);

            UpdateDetailVisibility();
        }

        private void SetupResultsList()
        {
            _resultsList.makeItem = () =>
            {
                var row = new VisualElement();
                row.AddToClassList("mid-row");
                var icon = new Image { name = "icon" };
                icon.AddToClassList("mid-row__icon");
                var textCol = new VisualElement();
                textCol.AddToClassList("mid-row__text-col");
                var nameLabel = new Label { name = "name" };
                nameLabel.AddToClassList("mid-row__name");
                var typeLabel = new Label { name = "type" };
                typeLabel.AddToClassList("mid-row__type");
                textCol.Add(nameLabel);
                textCol.Add(typeLabel);
                row.Add(icon);
                row.Add(textCol);
                return row;
            };

            _resultsList.bindItem = (el, i) =>
            {
                var entry = _pagedEntries[i];
                var icon = el.Q<Image>("icon");
                var nameLabel = el.Q<Label>("name");
                var typeLabel = el.Q<Label>("type");

                // CUSTOM ICON FIX ("doesn't show custom icons"): a MID_BaseSO's
                // custom icon is NOT a real Unity asset icon — MID_BaseSOEditor's
                // own header comment explains why (EditorGUIUtility.SetIconForObject
                // never actually supported arbitrary ScriptableObject instances).
                // The real mechanism is MID_BaseSOProjectIconDrawer, which paints
                // ResolveIcon() directly over each Project-window row via
                // EditorApplication.projectWindowItemOnGUI — a hook that only
                // fires for the actual Project window, not this ListView. So
                // AssetPreview.GetMiniThumbnail alone can never surface a
                // MID_BaseSO custom icon; check ResolveIcon() first, same as
                // that drawer does, and only fall back to the generic thumbnail
                // for anything that isn't a MID_BaseSO (or has no custom icon set).
                Texture2D resolvedIcon = (entry.Asset as MID_BaseSO)?.ResolveIcon();
                icon.image = resolvedIcon != null ? resolvedIcon
                             : AssetPreview.GetMiniThumbnail(entry.Asset)
                             ?? EditorGUIUtility.IconContent("ScriptableObject Icon").image;
                nameLabel.text = entry.Asset != null ? entry.Asset.name : "(missing)";
                // Only shown at all when browsing "All Categories" — redundant
                // (and just visual noise) once you're already inside one category.
                typeLabel.text = _selectedCategory == ALL_CATEGORIES ? entry.TypeName : string.Empty;
                typeLabel.style.display = _selectedCategory == ALL_CATEGORIES
                    ? DisplayStyle.Flex : DisplayStyle.None;
            };

            _resultsList.itemsSource = _pagedEntries;
            _resultsList.selectionType = SelectionType.Single;
            _resultsList.selectionChanged += sel =>
            {
                var entry = sel.Cast<Entry>().FirstOrDefault();
                SelectAsset(entry.Asset);
            };
        }

        // ── Scan ──────────────────────────────────────────────────────────────

        private void Rescan()
        {
            _allEntries.Clear();

            var guids = AssetDatabase.FindAssets("t:ScriptableObject");
            var seen  = new HashSet<string>(); // FindAssets can repeat a guid via subasset matches

            foreach (var guid in guids)
            {
                if (!seen.Add(guid)) continue;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (asset == null) continue; // e.g. a script asset FindAssets sometimes surfaces, not an actual SO instance

                _allEntries.Add(new Entry(asset, asset.GetType().Name, path));
            }

            RebuildCategoryChoices();
            ApplyFilters();
        }

        private void RebuildCategoryChoices()
        {
            _categoryChoices.Clear();
            _categoryChoices.Add(ALL_CATEGORIES);

            var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _allEntries)
                counts[e.TypeName] = counts.TryGetValue(e.TypeName, out var c) ? c + 1 : 1;

            foreach (var kv in counts)
                _categoryChoices.Add($"{kv.Key} ({kv.Value})");

            // Preserve the previous selection if that category still exists (by
            // name, ignoring the trailing count) — otherwise fall back to All
            // rather than silently landing on an unrelated category after a rescan.
            string prevBase = StripCount(_selectedCategory);
            string match = _categoryChoices.FirstOrDefault(c => StripCount(c) == prevBase);
            _selectedCategory = match ?? ALL_CATEGORIES;

            _categoryDropdown.choices = _categoryChoices;
            _categoryDropdown.SetValueWithoutNotify(_selectedCategory);
            // Second safety net for the same rendering quirk the deferred
            // Rescan() in CreateGUI() already addresses — cheap, and this
            // runs on every subsequent Rescan() (e.g. the refresh button)
            // too, not just the first one.
            _categoryDropdown.MarkDirtyRepaint();
        }

        private static string StripCount(string categoryLabel)
        {
            if (categoryLabel == ALL_CATEGORIES) return ALL_CATEGORIES;
            int idx = categoryLabel.LastIndexOf(" (", StringComparison.Ordinal);
            return idx >= 0 ? categoryLabel.Substring(0, idx) : categoryLabel;
        }

        // ── Filtering ─────────────────────────────────────────────────────────

        private void ApplyFilters(bool resetPage = true)
        {
            _filteredEntries.Clear();

            string categoryBase = StripCount(_selectedCategory);
            bool   allCategories = categoryBase == ALL_CATEGORIES;
            string search = _searchText?.Trim() ?? string.Empty;
            string folderPath = _folderScope != null ? AssetDatabase.GetAssetPath(_folderScope) : null;

            IEnumerable<Entry> src = _allEntries;
            if (!allCategories)
                src = src.Where(e => string.Equals(e.TypeName, categoryBase, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(folderPath))
                src = src.Where(e => e.AssetPath.StartsWith(folderPath + "/", StringComparison.OrdinalIgnoreCase)
                                      || e.AssetPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(search))
                src = src.Where(e => e.Asset != null
                    && e.Asset.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

            src = allCategories
                ? src.OrderBy(e => e.TypeName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.Asset != null ? e.Asset.name : string.Empty, StringComparer.OrdinalIgnoreCase)
                : src.OrderBy(e => e.Asset != null ? e.Asset.name : string.Empty, StringComparer.OrdinalIgnoreCase);

            _filteredEntries.AddRange(src);

            // PAGINATION FIX ("categories dropdown only appears after typing —
            // due to All Categories being a long list"): the ListView used to
            // bind directly to _filteredEntries, which for "All Categories" on
            // a project with hundreds of ScriptableObjects meant laying out
            // hundreds of rows on the very first frame — exactly the kind of
            // heavy initial-layout work that raced the window's first paint
            // (see CreateGUI()'s deferred-Rescan fix for the other half of
            // this). Only ever binding PAGE_SIZE rows at a time keeps that
            // initial layout cheap regardless of how large the underlying
            // asset list is.
            if (resetPage) _currentPage = 0;
            int pageCount = Mathf.Max(1, Mathf.CeilToInt(_filteredEntries.Count / (float)PAGE_SIZE));
            _currentPage = Mathf.Clamp(_currentPage, 0, pageCount - 1);

            _pagedEntries.Clear();
            _pagedEntries.AddRange(_filteredEntries.Skip(_currentPage * PAGE_SIZE).Take(PAGE_SIZE));
            _resultsList.RefreshItems();

            UpdatePaginationControls(pageCount);

            int categoryCount = _categoryChoices.Count - 1; // minus "All Categories" itself
            _statsLabel.text = $"{_filteredEntries.Count} of {_allEntries.Count} asset(s) · {categoryCount} categor{(categoryCount == 1 ? "y" : "ies")}";

            // The previously-selected asset may no longer be in view (filtered
            // out, or deleted since last scan) — keep the detail panel honest
            // rather than showing a stale inspector for something not listed.
            if (_selectedAsset != null && !_filteredEntries.Any(e => e.Asset == _selectedAsset))
                SelectAsset(null);
        }

        private void UpdatePaginationControls(int pageCount)
        {
            _pageLabel.text = $"Page {_currentPage + 1} of {pageCount}";
            _prevPageButton.SetEnabled(_currentPage > 0);
            _nextPageButton.SetEnabled(_currentPage < pageCount - 1);
        }

        private void GoToPage(int delta)
        {
            _currentPage += delta;
            ApplyFilters(resetPage: false);
        }

        // ── Selection / Detail panel ─────────────────────────────────────────

        private void SelectAsset(ScriptableObject asset)
        {
            _selectedAsset = asset;

            if (_cachedEditor != null) { DestroyImmediate(_cachedEditor); _cachedEditor = null; }
            if (asset != null) _cachedEditor = Editor.CreateEditor(asset);

            if (asset != null)
            {
                EditorGUIUtility.PingObject(asset);
                Selection.activeObject = asset;
            }

            UpdateDetailVisibility();
            _detailImgui?.MarkDirtyRepaint();
        }

        private void UpdateDetailVisibility()
        {
            bool hasSelection = _selectedAsset != null;
            if (_detailTitle != null)
                _detailTitle.text = hasSelection ? _selectedAsset.name : "No Selection";
            if (_detailEmptyState != null)
                _detailEmptyState.style.display = hasSelection ? DisplayStyle.None : DisplayStyle.Flex;
            if (_detailImgui != null)
                _detailImgui.style.display = hasSelection ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void DrawDetailIMGUI()
        {
            if (_selectedAsset == null || _cachedEditor == null) return;

            // Guards against the asset having been deleted from disk while the
            // window stayed open with it selected — CreateEditor's target going
            // null mid-session, not a normal empty-state case.
            if (_cachedEditor.target == null)
            {
                SelectAsset(null);
                return;
            }

            EditorGUI.BeginChangeCheck();
            _cachedEditor.OnInspectorGUI();
            if (EditorGUI.EndChangeCheck())
                _resultsList.RefreshItems(); // e.g. a renamed asset should update its row immediately
        }
    }
}
#endif
