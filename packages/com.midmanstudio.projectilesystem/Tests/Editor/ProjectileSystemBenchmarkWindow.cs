// packages/com.midmanstudio.projectilesystem/Tests/Editor/ProjectileSystemBenchmarkWindow.cs
// Editor window companion to ProjectileSystemBenchmark.
// Open via: MidManStudio > Utilities > Tests > Projectile System Bench

#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using TestGame;
using MidManStudio.Projectiles.Adapters;

namespace MidManStudio.Projectiles.EditorTools
{
    public class ProjectileSystemBenchmarkWindow : EditorWindow
    {
        // ── State ─────────────────────────────────────────────────────────────

        private ProjectileSystemBenchmark _bench;
        private Vector2                   _scroll;

        private bool _fTick      = true;
        private bool _fCollision = true;
        private bool _fSpawn     = true;
        private bool _fInfo      = true;

        // ── Colours ───────────────────────────────────────────────────────────

        private static readonly Color ColRust    = new Color(0.96f, 0.55f, 0.20f, 1f);
        private static readonly Color ColManaged = new Color(0.40f, 0.72f, 1.00f, 1f);
        private static readonly Color ColPhysics = new Color(0.90f, 0.35f, 0.35f, 1f);
        private static readonly Color ColBurst   = new Color(0.30f, 0.95f, 0.45f, 1f);
        private static readonly Color ColDim     = new Color(0.50f, 0.50f, 0.50f, 1f);
        private static readonly Color ColBarBg   = new Color(0.14f, 0.14f, 0.14f, 0.55f);
        private static readonly Color ColHeader  = new Color(0.80f, 0.80f, 0.80f, 1f);
        private static readonly Color ColWin     = new Color(0.28f, 0.95f, 0.45f, 1f);

        // ── Menu item ─────────────────────────────────────────────────────────

        [MenuItem("MidManStudio/Projectile System/Tests/Projectile System Bench", priority = 122)]
        public static void Open()
        {
            var w = GetWindow<ProjectileSystemBenchmarkWindow>("Projectile Bench");
            w.minSize = new Vector2(560, 480);
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void OnEnable()
        {
            EditorApplication.update += Repaint;
            TryFind();
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        private void TryFind()
        {
            if (_bench == null)
                _bench = Object.FindObjectOfType<ProjectileSystemBenchmark>();
        }

        // ── GUI ───────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            TryFind();
            DrawToolbar();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to run benchmarks.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            if (_bench == null)
            {
                EditorGUILayout.HelpBox(
                    "No ProjectileSystemBenchmark found in scene.\n" +
                    "Add it to any GameObject in the test scene.",
                    MessageType.Warning);

                if (GUILayout.Button("Add to Scene", GUILayout.Height(28)))
                {
                    var go = new GameObject("[ProjectileSystemBenchmark]");
                    _bench = go.AddComponent<ProjectileSystemBenchmark>();
                    Selection.activeGameObject = go;
                    Undo.RegisterCreatedObjectUndo(go, "Add Bench");
                }
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawContextInfo();
            DrawSeparator();
            DrawControls();
            DrawSeparator();
            DrawTickSection();
            DrawSeparator();
            DrawCollisionSection();
            DrawSeparator();
            DrawSpawnSection();
            DrawSeparator();
            DrawLegend();

            EditorGUILayout.EndScrollView();
        }

        // ── Toolbar ───────────────────────────────────────────────────────────

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("Projectile System Benchmark",
                    EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();
                _bench = (ProjectileSystemBenchmark)EditorGUILayout.ObjectField(
                    _bench, typeof(ProjectileSystemBenchmark), true, GUILayout.Width(200));
            }
        }

        // ── Context info ──────────────────────────────────────────────────────

        private void DrawContextInfo()
        {
            _fInfo = EditorGUILayout.BeginFoldoutHeaderGroup(_fInfo, "What is being measured?");
            if (_fInfo)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    Row(ColRust,
                        "Rust FFI (tick / collision)",
                        "tick_projectiles() / check_hits_grid_ex() via P/Invoke.\n" +
                        "This IS the production path — all projectiles use this every FixedUpdate.");
                    EditorGUILayout.Space(3);
                    Row(ColManaged,
                        "C# Managed baseline",
                        "Equivalent C# loop doing the same straight-movement math.\n" +
                        "Shows raw overhead of the FFI crossing vs staying in managed code.");
                    EditorGUILayout.Space(3);
                    Row(ColPhysics,
                        "Physics2D baseline",
                        "Physics2D.OverlapCircleNonAlloc per projectile — the classic Unity approach.\n" +
                        "No colliders in the test scene so hit count = 0, but call overhead is real.");
                    EditorGUILayout.Space(3);
                    Row(ColBurst,
                        "Burst spawn path",
                        "BatchSpawnHelper uses Burst IJobParallelFor for ≥ 8 projectiles.\n" +
                        "Managed C# loop used for < 8 (Burst scheduling overhead exceeds gain).");
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(2);
        }

        // ── Run controls ──────────────────────────────────────────────────────

        private void DrawControls()
        {
            if (_bench.IsRunning)
            {
                var r = EditorGUILayout.GetControlRect(false, 20);
                r.x += 2; r.width -= 4;
                EditorGUI.ProgressBar(r, _bench.Progress, _bench.StatusMessage);
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                    EditorGUILayout.LabelField(_bench.StatusMessage,
                        EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            }

            EditorGUILayout.Space(2);

            using (new EditorGUILayout.HorizontalScope())
            {
                var oldBg = GUI.backgroundColor;
                GUI.enabled = !_bench.IsRunning;

                GUI.backgroundColor = new Color(0.25f, 0.80f, 0.30f);
                if (GUILayout.Button("▶  Run All", GUILayout.Height(30)))
                    _bench.RunAll();

                GUI.backgroundColor = ColRust * 0.85f;
                if (GUILayout.Button("Tick",      GUILayout.Height(30)))
                    _bench.RunTickBenchOnly();
                if (GUILayout.Button("Collision", GUILayout.Height(30)))
                    _bench.RunCollisionBenchOnly();
                if (GUILayout.Button("Spawn",     GUILayout.Height(30)))
                    _bench.RunSpawnBenchOnly();

                GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                GUI.enabled         = _bench.IsRunning;
                if (GUILayout.Button("■  Cancel", GUILayout.Height(30)))
                    _bench.Cancel();

                GUI.backgroundColor = oldBg;
                GUI.enabled         = true;
            }
        }

        // ── Tick section ──────────────────────────────────────────────────────

        private void DrawTickSection()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _fTick = EditorGUILayout.Foldout(_fTick,
                    "Position Tick  —  Rust FFI vs C# managed loop",
                    true, EditorStyles.foldoutHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Run", EditorStyles.toolbarButton, GUILayout.Width(36)))
                    _bench.RunTickBenchOnly();
            }
            if (!_fTick) return;

            var results = _bench.TickResults;
            if (results.Count == 0)
            {
                EditorGUILayout.HelpBox("Run Tick bench to see results.", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                "LOWER avg ms = faster.  HIGHER throughput = more projectiles per ms.\n" +
                "Rust FFI overhead is amortised over N projectiles — wins clearly at high counts.\n" +
                "At very low counts (<32) C# may tie or win due to P/Invoke call setup cost.",
                MessageType.None);

            // Group results by projectile count (pairs: Rust, C#)
            var groups = GroupByCount(results, "Rust FFI", "C# Loop");
            foreach (var g in groups)
            {
                EditorGUILayout.Space(4);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    HL(ColHeader, $"{g.Count} projectiles — {g.Iterations} iterations");

                    if (g.A.Valid && g.B.Valid)
                    {
                        double speedup = g.B.AvgMs > 0 ? g.B.AvgMs / g.A.AvgMs : 0;
                        double worst   = System.Math.Max(g.A.AvgMs, g.B.AvgMs);

                        TimeBar("Rust FFI ", g.A.AvgMs, worst, ColRust,
                            $"{g.A.AvgMs * 1000:F1}µs  {g.A_Tick.ThroughputKPerMs:F1}k projs/ms");
                        TimeBar("C# Loop  ", g.B.AvgMs, worst, ColManaged,
                            $"{g.B.AvgMs * 1000:F1}µs  {g.B_Tick.ThroughputKPerMs:F1}k projs/ms");

                        if (speedup > 0)
                        {
                            var old = GUI.color;
                            GUI.color = speedup > 1 ? ColWin : ColDim;
                            EditorGUILayout.LabelField(speedup > 1
                                ? $"Rust is {speedup:F2}× faster than C# loop"
                                : $"C# loop {1.0 / speedup:F2}× faster (low count — FFI overhead dominates)",
                                EditorStyles.miniBoldLabel);
                            GUI.color = old;
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField("Partial results…", EditorStyles.miniLabel);
                    }
                }
            }
        }

        // ── Collision section ─────────────────────────────────────────────────

        private void DrawCollisionSection()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _fCollision = EditorGUILayout.Foldout(_fCollision,
                    "Collision  —  Rust spatial grid vs Physics2D.OverlapCircle",
                    true, EditorStyles.foldoutHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Run", EditorStyles.toolbarButton, GUILayout.Width(36)))
                    _bench.RunCollisionBenchOnly();
            }
            if (!_fCollision) return;

            var results = _bench.CollisionResults;
            if (results.Count == 0)
            {
                EditorGUILayout.HelpBox("Run Collision bench to see results.", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                "Rust spatial grid: one FFI call checks ALL projectiles against ALL targets.\n" +
                "Physics2D: one call PER projectile — N calls total, each crossing C→managed boundary.\n" +
                "Note: Physics2D returns 0 hits (no colliders in test scene) — pure call overhead.",
                MessageType.None);

            var groups = GroupByCount(results, "Rust grid", "Physics2D");
            foreach (var g in groups)
            {
                EditorGUILayout.Space(4);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    HL(ColHeader, $"{g.Count} projs × {g.TargetCount} targets");

                    if (g.A.Valid && g.B.Valid)
                    {
                        double worst   = System.Math.Max(g.A.AvgMs, g.B.AvgMs);
                        double speedup = g.B.AvgMs > 0 ? g.B.AvgMs / g.A.AvgMs : 0;

                        TimeBar("Rust grid", g.A.AvgMs, worst, ColRust,
                            $"{g.A.AvgMs * 1000:F1}µs  avg hits {g.A.AvgHits:F1}");
                        TimeBar("Physics2D", g.B.AvgMs, worst, ColPhysics,
                            $"{g.B.AvgMs * 1000:F1}µs  avg hits {g.B.AvgHits:F1}");

                        if (speedup > 0)
                        {
                            var old = GUI.color;
                            GUI.color = ColWin;
                            EditorGUILayout.LabelField(
                                $"Rust grid {speedup:F1}× faster — advantage grows with projectile count",
                                EditorStyles.miniBoldLabel);
                            GUI.color = old;
                        }
                    }
                }
            }
        }

        // ── Spawn section ─────────────────────────────────────────────────────

        private void DrawSpawnSection()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _fSpawn = EditorGUILayout.Foldout(_fSpawn,
                    "Spawn  —  BatchSpawnHelper C# managed vs Burst fill",
                    true, EditorStyles.foldoutHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Run", EditorStyles.toolbarButton, GUILayout.Width(36)))
                    _bench.RunSpawnBenchOnly();
            }
            if (!_fSpawn) return;

            var results = _bench.SpawnResults;
            if (results.Count == 0)
            {
                EditorGUILayout.HelpBox("Run Spawn bench to see results.", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                $"BatchSpawnHelper.BurstThreshold = {BatchSpawnHelper.BurstThreshold}.\n" +
                "Below threshold: managed C# loop fills the struct array (Burst overhead > gain).\n" +
                "Above threshold: Burst IJobParallelFor fills the array in parallel.\n" +
                "Both paths end with ONE FFI call — spawn_batch() — regardless of count.",
                MessageType.None);

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    HL(ColManaged, "C# managed", 120);
                    HL(ColBurst,   "Burst",       80);
                    GUILayout.FlexibleSpace();
                }

                double maxMs = 0;
                foreach (var r in results)
                    if (r.AvgMs > maxMs) maxMs = r.AvgMs;

                foreach (var r in results)
                {
                    if (!r.Valid) continue;
                    bool isBurst = r.SpawnCount >= BatchSpawnHelper.BurstThreshold;
                    Color col    = isBurst ? ColBurst : ColManaged;
                    TimeBar(
                        $"n={r.SpawnCount,4}",
                        r.AvgMs, maxMs, col,
                        $"{r.AvgMs * 1000:F2}µs total   {r.AvgUsPerProjectile:F3}µs/proj   " +
                        (isBurst ? "[Burst]" : "[C#]"));
                }
            }
        }

        // ── Legend ────────────────────────────────────────────────────────────

        private void DrawLegend()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Swatch(ColRust);    Label("Rust FFI",  76);
                Swatch(ColManaged); Label("C# managed", 82);
                Swatch(ColPhysics); Label("Physics2D",  72);
                Swatch(ColBurst);   Label("Burst",      50);
                GUILayout.FlexibleSpace();
                Swatch(ColWin);     Label("faster path", 80);
            }
        }

        // ── Drawing helpers ───────────────────────────────────────────────────

        private void TimeBar(string labelText, double ms, double maxMs, Color col, string detail)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var old = GUI.color; GUI.color = col;
                EditorGUILayout.LabelField(labelText, EditorStyles.miniLabel, GUILayout.Width(72));
                GUI.color = old;

                Rect r = EditorGUILayout.GetControlRect(false, 14, GUILayout.Width(140));
                r.y += 2; r.height = 10;
                EditorGUI.DrawRect(r, ColBarBg);
                if (maxMs > 0 && ms > 0)
                {
                    float f = Mathf.Clamp01((float)(ms / maxMs));
                    Rect fill = r; fill.width = Mathf.Max(r.width * f, 2f);
                    EditorGUI.DrawRect(fill, col);
                }

                GUI.color = ColDim;
                EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
                GUI.color = old;
            }
        }

        private void HL(Color col, string text, float w = 0)
        {
            var old = GUI.color; GUI.color = col;
            if (w > 0)
                EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel, GUILayout.Width(w));
            else
                EditorGUILayout.LabelField(text, EditorStyles.miniBoldLabel);
            GUI.color = old;
        }

        private void Row(Color col, string label, string body)
        {
            var old = GUI.color;
            GUI.color = col;
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            GUI.color = ColDim;
            EditorGUILayout.LabelField(body, EditorStyles.wordWrappedMiniLabel);
            GUI.color = old;
        }

        private void Swatch(Color col)
        {
            Rect r = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12));
            r.y += 2; r.height = 8; r.width = 8;
            EditorGUI.DrawRect(r, col);
            GUILayout.Space(2);
        }

        private static void Label(string t, float w) =>
            EditorGUILayout.LabelField(t, EditorStyles.miniLabel, GUILayout.Width(w));

        private void DrawSeparator()
        {
            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.45f, 0.45f, 0.45f, 0.35f));
            EditorGUILayout.Space(3);
        }

        // ── Result grouping ───────────────────────────────────────────────────

        private struct ResultPair
        {
            public int                  Count;
            public int                  TargetCount;
            public int                  Iterations;
            public TickBenchResult      A_Tick;
            public TickBenchResult      B_Tick;
            public CollisionBenchResult A;
            public CollisionBenchResult B;
            public bool IsCollision;
        }

        private List<ResultPair> GroupByCount(
            List<TickBenchResult> results, string labelA, string labelB)
        {
            var pairs = new List<ResultPair>();
            for (int i = 0; i + 1 < results.Count; i += 2)
            {
                pairs.Add(new ResultPair
                {
                    Count      = results[i].ProjectileCount,
                    Iterations = results[i].Iterations,
                    A_Tick     = results[i],
                    B_Tick     = results[i + 1],
                    IsCollision = false
                });
            }
            return pairs;
        }

        private List<ResultPair> GroupByCount(
            List<CollisionBenchResult> results, string labelA, string labelB)
        {
            var pairs = new List<ResultPair>();
            for (int i = 0; i + 1 < results.Count; i += 2)
            {
                pairs.Add(new ResultPair
                {
                    Count       = results[i].ProjectileCount,
                    TargetCount = results[i].TargetCount,
                    Iterations  = results[i].Iterations,
                    A           = results[i],
                    B           = results[i + 1],
                    IsCollision = true
                });
            }
            return pairs;
        }
    }
}
#endif
