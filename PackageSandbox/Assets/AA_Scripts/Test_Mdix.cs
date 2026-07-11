// PackageSandbox/Assets/AA_Scripts/Test_Mdix.cs
//
// Smoke test for com.midmanstudio.mdix -- attach to any GameObject and hit
// Play (or call RunAllTests() from the context menu). Everything runs
// through Debug.Log/LogError with a pass/fail summary at the end; nothing
// here uses Unity Test Framework's NUnit runner on purpose, matching how
// the other ad-hoc sandbox scripts (Test_1.cs etc.) are set up -- this is
// meant to be run by eye in a real player/editor session, not by CI.
//
// Covers, roughly in order of how likely each is to actually break:
//   1. MergeSources / MergeSourcesWeighted -- the new native merge FFI path
//      (mdix_merge_sources[_weighted] in mdix-ffi), completely unexercised
//      until this runs for real. Includes a conflict-report check and an
//      array ConcatDedup check, since both are brand new behavior.
//   2. Basic LoadStr + typed getters -- should be the least surprising part,
//      included mainly as a sanity baseline for everything else.
//   3. Dispose safety -- double-dispose, and use-after-dispose returning a
//      failed Result instead of crashing.
//   4. Hot reload -- EnableHotReload + a real file write, checking that
//      OnReloaded fires with the *same* MdixDatabase instance (this session's
//      in-place-reload fix) rather than silently doing nothing.
//
// If anything here throws instead of logging a controlled failure, that's
// the interesting result -- an uncaught exception means something in the
// native bridge itself (not just application logic) is broken.

using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using MidManStudio.Mdix;
using MidManStudio.Mdix.Core;

public class Test_Mdix : MonoBehaviour
{
    private int _passed;
    private int _failed;

    private void Start()
    {
        RunAllTests();
    }

    [ContextMenu("Run All Mdix Tests")]
    private void RunAllTests()
    {
        _passed = 0;
        _failed = 0;

        Log("===== com.midmanstudio.mdix smoke test starting =====");

        Test_BasicLoadAndGetters();
        Test_MergeSources_DisjointKeys();
        Test_MergeSources_WeightedPriority_ConflictReport();
        Test_MergeSources_ThrowOnConflict_Fails();
        Test_MergeSources_ArrayConcatDedup();
        Test_Merge_AlreadyLoadedDatabases();
        Test_DisposeSafety();

        Log($"===== Sync tests done: {_passed} passed, {_failed} failed =====");

        StartCoroutine(Test_HotReload());
    }

    // ── 1. Basic load + typed getters ───────────────────────────────────────

    private void Test_BasicLoadAndGetters()
    {
        const string source = @"
@DATA(
    name = ""Test Config"",
    port = 8080,
    ratio = 0.5,
    debug = true
)";
        var result = Dix.LoadStr(source);
        if (!Check("BasicLoad: LoadStr succeeds", result.IsSuccess, result))
            return;

        using var db = result.SuccessResult;

        Check("BasicLoad: GetString(name)",
            db.GetString("name") is { IsSuccess: true, SuccessResult: "Test Config" });

        Check("BasicLoad: GetInt(port)",
            db.GetInt("port") is { IsSuccess: true, SuccessResult: 8080 });

        Check("BasicLoad: GetBool(debug)",
            db.GetBool("debug") is { IsSuccess: true, SuccessResult: true });

        Check("BasicLoad: Exists(name) is true", db.Exists("name"));
        Check("BasicLoad: Exists(nope) is false", !db.Exists("nope"));

        var keysResult = db.GetKeys();
        Check("BasicLoad: GetKeys succeeds", keysResult.IsSuccess, keysResult);
    }

    // ── 2. MergeSources — the new native path, disjoint keys ────────────────

    private void Test_MergeSources_DisjointKeys()
    {
        var result = Dix.MergeSources(new[]
        {
            "@DATA( host = \"localhost\", port = 8080 )",
            "@DATA( timeout = 5000, ssl = true )",
        });

        if (!Check("MergeSources: disjoint keys succeeds", result.IsSuccess, result))
            return;

        using var outcome = result.SuccessResult;
        var db = outcome.Database;

        Check("MergeSources: host survived", db.GetString("host") is { IsSuccess: true, SuccessResult: "localhost" });
        Check("MergeSources: port survived", db.GetInt("port") is { IsSuccess: true, SuccessResult: 8080 });
        Check("MergeSources: timeout survived", db.GetInt("timeout") is { IsSuccess: true, SuccessResult: 5000 });
        Check("MergeSources: no conflicts on disjoint keys", outcome.Conflicts.Count == 0,
            $"got {outcome.Conflicts.Count}: {string.Join(", ", outcome.Conflicts)}");
    }

    // ── 3. MergeSourcesWeighted — WeightedPriority + real conflict report ───

    private void Test_MergeSources_WeightedPriority_ConflictReport()
    {
        var result = Dix.MergeSourcesWeighted(new (string, double)[]
        {
            ("@DATA( port = 1111 )", 1.0),
            ("@DATA( port = 2222 )", 0.5),
        });

        if (!Check("WeightedPriority: merge succeeds", result.IsSuccess, result))
            return;

        using var outcome = result.SuccessResult;
        var db = outcome.Database;

        Check("WeightedPriority: higher weight wins",
            db.GetInt("port") is { IsSuccess: true, SuccessResult: 1111 });

        Check("WeightedPriority: conflict was reported", outcome.Conflicts.Count == 1,
            $"got {outcome.Conflicts.Count}");

        if (outcome.Conflicts.Count == 1)
        {
            var c = outcome.Conflicts[0];
            Log($"  conflict report -> {c}");
            Check("WeightedPriority: conflict path is 'port'", c.Path == "port", c.Path);
            Check("WeightedPriority: winning source is 0", c.WinningSource == 0, c.WinningSource);
        }
    }

    // ── 4. ThrowOnConflict — should fail loudly, not silently pick one ──────

    private void Test_MergeSources_ThrowOnConflict_Fails()
    {
        var result = Dix.MergeSources(
            new[] { "@DATA( port = 1111 )", "@DATA( port = 2222 )" },
            MdixMergeStrategy.ThrowOnConflict);

        Check("ThrowOnConflict: merge fails as expected", result.IsFailure, result);
        if (result.IsFailure)
            Log($"  (expected) error -> {result.Error.Message}");
    }

    // ── 5. Array merging — new default (ConcatDedup) vs explicit Replace ────

    private void Test_MergeSources_ArrayConcatDedup()
    {
        var defaultResult = Dix.MergeSources(new[]
        {
            "@DATA( tags:: \"alpha\", \"beta\" )",
            "@DATA( tags:: \"beta\", \"gamma\" )",
        });

        if (Check("ArrayMerge: default (ConcatDedup) succeeds", defaultResult.IsSuccess, defaultResult))
        {
            using var outcome = defaultResult.SuccessResult;
            var lenResult = outcome.Database.GetArrayLength("tags");
            Check("ArrayMerge: ConcatDedup -> 3 entries (beta deduped)",
                lenResult is { IsSuccess: true, SuccessResult: 3 }, lenResult);
        }

        var replaceResult = Dix.MergeSources(
            new[] { "@DATA( tags:: \"alpha\", \"beta\" )", "@DATA( tags:: \"x\", \"y\", \"z\" )" },
            MdixMergeStrategy.WeightedPriority,
            MdixArrayMergeStrategy.Replace);

        if (Check("ArrayMerge: explicit Replace succeeds", replaceResult.IsSuccess, replaceResult))
        {
            using var outcome = replaceResult.SuccessResult;
            var lenResult = outcome.Database.GetArrayLength("tags");
            Check("ArrayMerge: Replace -> 2 entries (winner's array only)",
                lenResult is { IsSuccess: true, SuccessResult: 2 }, lenResult);
        }
    }

    // ── 6. Merge on already-loaded databases (goes through mdix_to_mdix) ────

    private void Test_Merge_AlreadyLoadedDatabases()
    {
        var primaryResult   = Dix.LoadStr("@DATA( a = 1, shared = \"primary\" )");
        var secondaryResult = Dix.LoadStr("@DATA( b = 2, shared = \"secondary\" )");

        if (!Check("Merge(db,db): both sources load", primaryResult.IsSuccess && secondaryResult.IsSuccess))
            return;

        using var primary   = primaryResult.SuccessResult;
        using var secondary = secondaryResult.SuccessResult;

        var mergeResult = Dix.Merge(primary, secondary);
        if (!Check("Merge(db,db): merge succeeds", mergeResult.IsSuccess, mergeResult))
            return;

        using var outcome = mergeResult.SuccessResult;
        Check("Merge(db,db): a survived", outcome.Database.GetInt("a") is { IsSuccess: true, SuccessResult: 1 });
        Check("Merge(db,db): b survived", outcome.Database.GetInt("b") is { IsSuccess: true, SuccessResult: 2 });
        Check("Merge(db,db): primary wins on shared key (weight 1.0 vs 0.5)",
            outcome.Database.GetString("shared") is { IsSuccess: true, SuccessResult: "primary" });

        // primary/secondary should be untouched by the merge -- Merge() reads
        // them via ToMdix, never mutates or disposes its inputs.
        Check("Merge(db,db): primary still usable after merge",
            primary.GetInt("a") is { IsSuccess: true, SuccessResult: 1 });
    }

    // ── 7. Dispose safety ────────────────────────────────────────────────────

    private void Test_DisposeSafety()
    {
        var result = Dix.LoadStr("@DATA( x = 1 )");
        if (!Check("DisposeSafety: load succeeds", result.IsSuccess, result))
            return;

        var db = result.SuccessResult;
        db.Dispose();

        bool secondDisposeThrew = false;
        try { db.Dispose(); }
        catch (Exception ex) { secondDisposeThrew = true; Log($"  second Dispose() threw: {ex}"); }
        Check("DisposeSafety: double Dispose() does not throw", !secondDisposeThrew);

        var afterDispose = db.GetInt("x");
        Check("DisposeSafety: use-after-dispose returns a failure Result (not a crash)",
            afterDispose.IsFailure, afterDispose);
    }

    // ── 8. Hot reload — real file I/O, checks the in-place-update fix ───────

    private IEnumerator Test_HotReload()
    {
        var path = Path.Combine(Application.temporaryCachePath, "mdix_hotreload_test.mdix");
        File.WriteAllText(path, "@DATA( value = 1 )", Encoding.UTF8);

        var loadResult = Dix.Load(path);
        if (!Check("HotReload: initial load succeeds", loadResult.IsSuccess, loadResult))
            yield break;

        using var db = loadResult.SuccessResult;
        Check("HotReload: initial value is 1",
            db.GetInt("value") is { IsSuccess: true, SuccessResult: 1 });

        MdixDatabase? reloadedRef = null;
        MdixError? failure = null;
        db.OnReloaded += reloaded => reloadedRef = reloaded;
        db.OnReloadFailed += err => failure = err;
        db.EnableHotReload();

        yield return new WaitForSeconds(0.3f);
        File.WriteAllText(path, "@DATA( value = 2 )", Encoding.UTF8);

        // Debounce is 500ms + a 100ms flush delay inside HandleFileChanged --
        // give it comfortable headroom rather than timing this tightly.
        yield return new WaitForSeconds(2f);

        if (failure != null)
            Check("HotReload: no reload failure", false, failure.Value.Message);

        Check("HotReload: OnReloaded fired", reloadedRef != null);
        Check("HotReload: OnReloaded handed back the SAME instance (in-place fix)",
            ReferenceEquals(reloadedRef, db));
        Check("HotReload: value updated to 2",
            db.GetInt("value") is { IsSuccess: true, SuccessResult: 2 });

        db.DisableHotReload();
        try { File.Delete(path); } catch { /* best-effort cleanup */ }

        Log($"===== Hot reload test done — running total: {_passed} passed, {_failed} failed =====");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private bool Check(string label, bool condition, object? context = null)
    {
        if (condition)
        {
            _passed++;
            Log($"[PASS] {label}");
        }
        else
        {
            _failed++;
            var suffix = context != null ? $" -- {context}" : string.Empty;
            LogError($"[FAIL] {label}{suffix}");
        }
        return condition;
    }

    private static void Log(string message)      => Debug.Log($"[MdixTest] {message}");
    private static void LogError(string message)  => Debug.LogError($"[MdixTest] {message}");
}
