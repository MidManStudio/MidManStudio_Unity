// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/samples.md, section "MID_AutoRefDemo_BasicMatch.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Core.AutoReference;

namespace MidManStudio.Core.Tests.AutoReference
{
    /// <summary>Empty stand-in for an icon element, so the demos need no UGUI or TextMeshPro. Used by demos 1 and 3.</summary>
    public class MID_DemoIconSlot : MonoBehaviour { }
    /// <summary>Empty stand-in for a label element. Used by all three demos.</summary>
    public class MID_DemoLabelSlot : MonoBehaviour { }

    /// <summary>Script under test for demo 1: one icon field and one label field, each with a single candidate child.</summary>
    [MID_AutoRefable]
    public class MID_DemoSimplePanel : MonoBehaviour
    {
        [SerializeField] private MID_DemoIconSlot _icon;
        [SerializeField] private MID_DemoLabelSlot _title;

        public MID_DemoIconSlot Icon => _icon;
        public MID_DemoLabelSlot Title => _title;
    }

    /// <summary>
    /// Auto Reference demo 1, basic single-candidate match. It builds a root with a <c>MID_DemoSimplePanel</c> and two
    /// children named Icon and Title, adds a real <c>MID_AutoRef</c> component (the path the Inspector button uses)
    /// with Include Children on, calls <c>ResolveNow</c>, and checks that both fields got the right child. Runs in
    /// Start when Run On Start is on (default), or from the context menu "Run Test", and prints PASS and FAIL lines to
    /// the Console. The objects it creates stay in the scene.
    /// </summary>
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
