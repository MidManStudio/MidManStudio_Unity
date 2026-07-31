// SHOWCASE 1 — Basic single-candidate resolution, through the real MID_AutoRef
// component (not the static resolver directly). Builds a small hierarchy in code,
// resolves, and asserts both fields landed on the right objects.

using UnityEngine;
using MidManStudio.Core.AutoReference;

namespace MidManStudio.Core.Tests.AutoReference
{
    // ── Minimal stand-in components — no UGUI/TMP dependency required ─────────
    public class MID_DemoIconSlot : MonoBehaviour { }
    public class MID_DemoLabelSlot : MonoBehaviour { }

    // ── The script under test ──────────────────────────────────────────────────
    [MID_AutoRefable]
    public class MID_DemoSimplePanel : MonoBehaviour
    {
        [SerializeField] private MID_DemoIconSlot _icon;
        [SerializeField] private MID_DemoLabelSlot _title;

        public MID_DemoIconSlot Icon => _icon;
        public MID_DemoLabelSlot Title => _title;
    }

    // ── Runner ──────────────────────────────────────────────────────────────────
    public class MID_AutoRefDemo_BasicMatch : MonoBehaviour
    {
        [SerializeField] private bool _runOnStart = true;

        private int _passed;
        private int _failed;

        private void Start()
        {
            if (_runOnStart) RunTest();
        }

        [ContextMenu("Run Test")]
        public void RunTest()
        {
            _passed = 0;
            _failed = 0;
            Header("AutoRef Demo 1 — Basic Match");

            var root  = new GameObject("[AutoRef Demo] Basic Match");
            var panel = root.AddComponent<MID_DemoSimplePanel>();

            var icon = new GameObject("Icon").AddComponent<MID_DemoIconSlot>();
            icon.transform.SetParent(root.transform);

            var title = new GameObject("Title").AddComponent<MID_DemoLabelSlot>();
            title.transform.SetParent(root.transform);

            // Exercises the real component path (same as pressing the inspector button).
            var autoRef = root.AddComponent<MID_AutoRef>();
            autoRef.Options.includeChildren = true;
            autoRef.ResolveNow();

            Expect("Icon resolved",  panel.Icon == icon);
            Expect("Title resolved", panel.Title == title);

            Header($"Results: {_passed} PASSED | {_failed} FAILED");
        }

        private void Expect(string label, bool condition)
        {
            if (condition) { _passed++; Debug.Log($"  <color=lime>✓</color> {label}"); }
            else            { _failed++; Debug.LogError($"  <color=red>✗ FAIL: {label}</color>"); }
        }

        private static void Header(string title) => Debug.Log($"<color=yellow><b>━━━ {title} ━━━</b></color>");
    }
}
