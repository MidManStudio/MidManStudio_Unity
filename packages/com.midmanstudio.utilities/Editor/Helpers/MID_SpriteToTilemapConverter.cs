// Scans a source GameObject hierarchy for SpriteRenderer components and bakes
// them into Unity Tilemap cells, grouped by Sorting Layer + Sorting Order.
// Preserves exact world position via per-cell transform matrix offset.
// Supports dry-run preview, cell-collision detection, and full Undo.
//
// Open via: MidManStudio > Utilities > Sprite to Tilemap Converter
// Requires: Unity 2022.3+, 2D Tilemap package (included by default)

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.UIElements;

namespace MidManStudio.Core.EditorTools
{
    public class MID_SpriteToTilemapConverter : EditorWindow
    {
        // ── Data types ─────────────────────────────────────────────────────────

        private sealed class SpriteEntry
        {
            public SpriteRenderer Renderer;
            public string         SortingLayerName;
            public int            SortingOrder;
            public string         Key => $"{SortingLayerName}|{SortingOrder}";
        }

        private sealed class LayerGroup
        {
            public string            SortingLayerName;
            public int               SortingOrder;
            public string            Key     => $"{SortingLayerName}|{SortingOrder}";
            public List<SpriteEntry> Entries = new();
            public Tilemap           TargetTilemap;
        }

        private sealed class ConversionReport
        {
            public bool         IsDryRun;
            public bool         Success;
            public int          Converted;
            public int          Skipped;
            public int          TilesCreated;
            public int          TilesReused;
            public int          CellCollisions;
            public List<string> Lines = new();
            public void Add(string line) => Lines.Add(line);
        }

        // ── State ──────────────────────────────────────────────────────────────

        private GameObject       _sourceRoot;
        private string           _tileSavePath     = "Assets/MidManStudio/GeneratedTiles";
        private bool             _deleteSourceOnDone;

        private bool              _scanned;
        private List<SpriteEntry> _discovered = new();
        private List<LayerGroup>  _groups     = new();
        private ConversionReport  _lastReport;

        // ── UI refs ────────────────────────────────────────────────────────────

        private Label         _statsLabel;
        private VisualElement _groupsEmpty;
        private VisualElement _groupsColHeaders;
        private VisualElement _groupsList;
        private VisualElement _resultsSection;
        private VisualElement _resultsBox;
        private Label         _resultsLog;
        private Button        _convertBtn;
        private Button        _dryRunBtn;

        // ── Menu ───────────────────────────────────────────────────────────────

        [MenuItem("MidManStudio/Utilities/Sprite to Tilemap Converter", priority = 117)]
        public static void Open()
        {
            var w = GetWindow<MID_SpriteToTilemapConverter>("Sprite \u2192 Tilemap");
            w.minSize = new Vector2(580, 640);
        }

        // ── CreateGUI ──────────────────────────────────────────────────────────

        public void CreateGUI()
        {
            var uxml = MidEditorUIHelpers.FindUxml("MID_SpriteToTilemapConverter");
            var uss  = MidEditorUIHelpers.FindUss("MID_SpriteToTilemapConverter");

            if (uxml == null)
            {
                rootVisualElement.Add(new Label(
                    "\u26a0\ufe0f  MID_SpriteToTilemapConverter.uxml not found.\n" +
                    "Place the UXML and USS files in an Editor folder and reimport.")
                {
                    style =
                    {
                        whiteSpace = WhiteSpace.Normal,
                        color      = new StyleColor(new Color(1f, 0.8f, 0.2f)),
                        marginTop  = 20, marginLeft = 12
                    }
                });
                return;
            }

            var tree = uxml.Instantiate();
            tree.style.flexGrow = 1;
            rootVisualElement.Add(tree);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);

            WireHeader(tree);
            WireSetup(tree);
            WireGroups(tree);
            WireActions(tree);
            WireResults(tree);
        }

        // ── Header ─────────────────────────────────────────────────────────────

        private void WireHeader(VisualElement root)
        {
            var header = root.Q<VisualElement>("header");
            if (header == null) return;
            var grad = new GradientBannerElement
            {
                ColorTL = new Color(0.08f, 0.32f, 0.20f, 1f),
                ColorTR = new Color(0.08f, 0.20f, 0.32f, 1f),
                ColorBL = new Color(0.06f, 0.06f, 0.09f, 1f),
                ColorBR = new Color(0.06f, 0.06f, 0.08f, 1f)
            };
            grad.style.position = Position.Absolute;
            grad.style.top = grad.style.left = grad.style.right = grad.style.bottom = 0;
            header.Insert(0, grad);
        }

        // ── Setup ──────────────────────────────────────────────────────────────

        private void WireSetup(VisualElement root)
        {
            var slot = root.Q<VisualElement>("source-field-slot");
            if (slot != null)
            {
                var of = new ObjectField("Source Root")
                {
                    objectType        = typeof(GameObject),
                    allowSceneObjects = true,
                    value             = _sourceRoot
                };
                of.AddToClassList("stc-object-field");
                of.RegisterValueChangedCallback(evt =>
                {
                    _sourceRoot = evt.newValue as GameObject;
                    Reset();
                });
                slot.Add(of);
            }

            var pathField = root.Q<TextField>("tile-path-field");
            if (pathField != null)
            {
                pathField.SetValueWithoutNotify(_tileSavePath);
                pathField.RegisterValueChangedCallback(evt => _tileSavePath = evt.newValue.Trim());
            }

            root.Q<Button>("browse-btn")?.RegisterCallback<ClickEvent>(_ =>
            {
                string raw = EditorUtility.OpenFolderPanel("Select Tile Output Folder", "Assets", "");
                if (string.IsNullOrEmpty(raw)) return;
                if (raw.StartsWith(Application.dataPath))
                    raw = "Assets" + raw.Substring(Application.dataPath.Length);
                _tileSavePath = raw;
                root.Q<TextField>("tile-path-field")?.SetValueWithoutNotify(_tileSavePath);
            });

            root.Q<Toggle>("delete-source-toggle")?.RegisterValueChangedCallback(
                evt => _deleteSourceOnDone = evt.newValue);
        }

        // ── Groups ─────────────────────────────────────────────────────────────

        private void WireGroups(VisualElement root)
        {
            _statsLabel       = root.Q<Label>("scan-stats");
            _groupsEmpty      = root.Q<VisualElement>("groups-empty");
            _groupsColHeaders = root.Q<VisualElement>("groups-col-headers");
            _groupsList       = root.Q<VisualElement>("groups-list");

            root.Q<Button>("scan-btn")?.RegisterCallback<ClickEvent>(_ => Scan());
            RefreshGroupsUI();
        }

        // ── Actions ────────────────────────────────────────────────────────────

        private void WireActions(VisualElement root)
        {
            _dryRunBtn  = root.Q<Button>("dry-run-btn");
            _convertBtn = root.Q<Button>("convert-btn");

            _dryRunBtn?.RegisterCallback<ClickEvent>(_ => RunConvert(dryRun: true));

            _convertBtn?.RegisterCallback<ClickEvent>(_ =>
            {
                if (!_scanned || _groups.Count == 0)
                { FlashResult("Scan first before converting.", isError: true); return; }

                int unmapped = _groups.Count(g => g.TargetTilemap == null);
                if (unmapped > 0 &&
                    !EditorUtility.DisplayDialog("Unmapped Groups",
                        $"{unmapped} group(s) have no Target Tilemap — those sprites will be skipped.\n\nProceed?",
                        "Proceed", "Cancel"))
                    return;

                RunConvert(dryRun: false);
            });

            RefreshActionButtons();
        }

        // ── Results ────────────────────────────────────────────────────────────

        private void WireResults(VisualElement root)
        {
            _resultsSection = root.Q<VisualElement>("results-section");
            _resultsBox     = root.Q<VisualElement>("results-box");
            _resultsLog     = root.Q<Label>("results-log");
            if (_resultsSection != null)
                _resultsSection.style.display = DisplayStyle.None;
        }

        // ── Scan ───────────────────────────────────────────────────────────────

        private void Scan()
        {
            if (_sourceRoot == null)
            { FlashResult("Assign a Source Root before scanning.", isError: true); return; }

            _discovered.Clear();
            _groups.Clear();
            _scanned = false;

            foreach (var sr in _sourceRoot.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr.sprite == null) continue;
                _discovered.Add(new SpriteEntry
                {
                    Renderer         = sr,
                    SortingLayerName = sr.sortingLayerName,
                    SortingOrder     = sr.sortingOrder
                });
            }

            foreach (var g in _discovered
                .GroupBy(e => e.Key)
                .OrderBy(g => g.First().SortingLayerName)
                .ThenBy(g => g.First().SortingOrder))
            {
                _groups.Add(new LayerGroup
                {
                    SortingLayerName = g.First().SortingLayerName,
                    SortingOrder     = g.First().SortingOrder,
                    Entries          = g.ToList()
                });
            }

            _scanned = true;
            RefreshGroupsUI();
            RefreshActionButtons();

            if (_discovered.Count == 0)
                FlashResult("No SpriteRenderers with assigned sprites found in Source Root.", isError: true);
        }

        // ── Convert ────────────────────────────────────────────────────────────

        private void RunConvert(bool dryRun)
        {
            if (!_scanned || _groups.Count == 0)
            { FlashResult("Nothing to convert — scan first.", isError: true); return; }

            if (!dryRun && !_tileSavePath.StartsWith("Assets"))
            { FlashResult("Tile Output Path must be inside the Assets folder.", isError: true); return; }

            var report    = new ConversionReport { IsDryRun = dryRun };
            var tileCache = new Dictionary<Sprite, Tile>();
            var tempTiles = new List<Tile>(); // dry-run instances to destroy

            if (!dryRun && !AssetDatabase.IsValidFolder(_tileSavePath))
            {
                Directory.CreateDirectory(_tileSavePath);
                AssetDatabase.Refresh();
            }

            foreach (var group in _groups)
            {
                if (group.TargetTilemap == null)
                {
                    report.Add($"\u26a0  [{group.Key}] — no Tilemap assigned, " +
                               $"{group.Entries.Count} sprite(s) skipped.");
                    report.Skipped += group.Entries.Count;
                    continue;
                }

                var grid = group.TargetTilemap.layoutGrid;
                if (grid == null)
                {
                    report.Add($"\u2717  [{group.Key}] — Tilemap '{group.TargetTilemap.name}' " +
                               "has no parent Grid component. Skipped.");
                    report.Skipped += group.Entries.Count;
                    continue;
                }

                if (!dryRun)
                    Undo.RecordObject(group.TargetTilemap, "Sprite \u2192 Tilemap Conversion");

                var usedCells = new Dictionary<Vector3Int, string>();

                foreach (var entry in group.Entries)
                {
                    var sr = entry.Renderer;
                    if (sr == null || sr.sprite == null) continue;

                    // ── Tile lookup / creation ────────────────────────────────
                    if (!tileCache.TryGetValue(sr.sprite, out Tile tile))
                    {
                        string safe = sr.sprite.name
                            .Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
                        string path = $"{_tileSavePath}/{safe}.asset";

                        if (!dryRun)
                        {
                            tile = AssetDatabase.LoadAssetAtPath<Tile>(path);
                            if (tile == null)
                            {
                                tile = ScriptableObject.CreateInstance<Tile>();
                                tile.sprite = sr.sprite;
                                AssetDatabase.CreateAsset(tile, path);
                                report.TilesCreated++;
                            }
                            else
                            {
                                report.TilesReused++;
                            }
                        }
                        else
                        {
                            bool exists = AssetDatabase.LoadAssetAtPath<Tile>(path) != null;
                            if (exists) report.TilesReused++; else report.TilesCreated++;
                            tile = ScriptableObject.CreateInstance<Tile>();
                            tile.sprite = sr.sprite;
                            tempTiles.Add(tile);
                        }
                        tileCache[sr.sprite] = tile;
                    }

                    // ── World pos → cell + sub-cell offset ────────────────────
                    Vector3Int cell       = grid.WorldToCell(sr.transform.position);
                    Vector3    cellCenter = group.TargetTilemap.GetCellCenterWorld(cell);
                    Vector3    offset     = sr.transform.position - cellCenter;

                    // ── Cell collision detection ───────────────────────────────
                    if (usedCells.TryGetValue(cell, out string prevName))
                    {
                        report.Add($"\u26a0  Cell collision at {cell} in [{group.Key}]: " +
                                   $"'{sr.sprite.name}' vs '{prevName}'. " +
                                   "Use different sorting orders for overlapping sprites.");
                        report.CellCollisions++;
                    }
                    else
                    {
                        usedCells[cell] = sr.sprite.name;
                    }

                    // ── Place ─────────────────────────────────────────────────
                    if (!dryRun)
                    {
                        group.TargetTilemap.SetTile(cell, tile);
                        group.TargetTilemap.SetTileFlags(cell, TileFlags.None);
                        group.TargetTilemap.SetColor(cell, sr.color);
                        group.TargetTilemap.SetTransformMatrix(cell,
                            Matrix4x4.TRS(offset, sr.transform.localRotation, sr.transform.localScale));
                    }

                    report.Converted++;
                }
            }

            // ── Finalise ───────────────────────────────────────────────────────
            if (!dryRun)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                if (_deleteSourceOnDone && _sourceRoot != null)
                {
                    Undo.DestroyObjectImmediate(_sourceRoot);
                    report.Add("\u2713  Source root destroyed (Ctrl+Z to undo).");
                }
            }
            else
            {
                foreach (var t in tempTiles) DestroyImmediate(t);
            }

            report.Success = report.Skipped == 0 && report.CellCollisions == 0;
            _lastReport    = report;
            DisplayReport();
        }

        // ── UI helpers ─────────────────────────────────────────────────────────

        private void RefreshGroupsUI()
        {
            if (_groupsList == null) return;
            _groupsList.Clear();

            bool empty = !_scanned || _groups.Count == 0;

            if (_groupsEmpty != null)
                _groupsEmpty.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
            if (_groupsColHeaders != null)
                _groupsColHeaders.style.display = empty ? DisplayStyle.None : DisplayStyle.Flex;
            if (_groupsList != null)
                _groupsList.style.display = empty ? DisplayStyle.None : DisplayStyle.Flex;

            if (_statsLabel != null)
            {
                _statsLabel.text = _scanned
                    ? $"{_discovered.Count} sprite{(_discovered.Count == 1 ? "" : "s")}  \u00b7  " +
                      $"{_groups.Count} group{(_groups.Count == 1 ? "" : "s")}"
                    : "Not scanned";
            }

            if (empty) return;

            foreach (var group in _groups)
                _groupsList.Add(BuildGroupRow(group));
        }

        private VisualElement BuildGroupRow(LayerGroup group)
        {
            var row = new VisualElement();
            row.AddToClassList("stc-group-row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems    = Align.Center;

            var layerLbl = new Label(group.SortingLayerName);
            layerLbl.AddToClassList("stc-col--layer");
            layerLbl.AddToClassList("stc-group-layer");

            var orderLbl = new Label(group.SortingOrder.ToString());
            orderLbl.AddToClassList("stc-col--order");
            orderLbl.AddToClassList("stc-group-order");

            var countLbl = new Label(group.Entries.Count.ToString());
            countLbl.AddToClassList("stc-col--count");
            countLbl.AddToClassList("stc-group-count");

            var mapField = new ObjectField
            {
                objectType        = typeof(Tilemap),
                allowSceneObjects = true,
                value             = group.TargetTilemap,
                label             = ""
            };
            mapField.AddToClassList("stc-col--target");
            mapField.AddToClassList("stc-group-tilemap");
            mapField.RegisterValueChangedCallback(evt =>
                group.TargetTilemap = evt.newValue as Tilemap);

            row.Add(layerLbl);
            row.Add(orderLbl);
            row.Add(countLbl);
            row.Add(mapField);
            return row;
        }

        private void RefreshActionButtons()
        {
            bool canAct = _scanned && _groups.Count > 0;
            _convertBtn?.SetEnabled(canAct);
            _dryRunBtn?.SetEnabled(canAct);
        }

        private void Reset()
        {
            _discovered.Clear();
            _groups.Clear();
            _scanned = false;
            RefreshGroupsUI();
            RefreshActionButtons();
            if (_resultsSection != null)
                _resultsSection.style.display = DisplayStyle.None;
        }

        private void FlashResult(string message, bool isError)
        {
            if (_resultsSection == null) return;
            _resultsSection.style.display = DisplayStyle.Flex;
            if (_resultsLog != null) _resultsLog.text = message;
            _resultsBox?.EnableInClassList("stc-results--error",   isError);
            _resultsBox?.EnableInClassList("stc-results--success", !isError);
            _resultsBox?.EnableInClassList("stc-results--warning", false);
        }

        private void DisplayReport()
        {
            if (_resultsSection == null || _lastReport == null) return;
            _resultsSection.style.display = DisplayStyle.Flex;

            var r  = _lastReport;
            var sb = new System.Text.StringBuilder();

            sb.AppendLine(r.IsDryRun ? "\u2500\u2500 DRY RUN (nothing written) \u2500\u2500"
                                     : "\u2500\u2500 CONVERSION COMPLETE \u2500\u2500");
            sb.AppendLine($"Sprites converted : {r.Converted}");
            if (r.Skipped > 0)
                sb.AppendLine($"Sprites skipped   : {r.Skipped}");
            sb.AppendLine(r.IsDryRun
                ? $"Tiles (would create) : {r.TilesCreated}   Already exist : {r.TilesReused}"
                : $"Tiles created : {r.TilesCreated}   Reused : {r.TilesReused}");
            if (r.CellCollisions > 0)
                sb.AppendLine($"Cell collisions   : {r.CellCollisions}  \u2190 use different sort orders");

            if (r.Lines.Count > 0)
            {
                sb.AppendLine();
                foreach (var line in r.Lines) sb.AppendLine(line);
            }

            if (_resultsLog != null) _resultsLog.text = sb.ToString().TrimEnd();

            bool hasWarnings = r.Skipped > 0 || r.CellCollisions > 0;
            _resultsBox?.EnableInClassList("stc-results--success", r.Success && !hasWarnings);
            _resultsBox?.EnableInClassList("stc-results--warning", hasWarnings);
            _resultsBox?.EnableInClassList("stc-results--error",   false);
        }
    }
}
#endif
