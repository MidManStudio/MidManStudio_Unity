// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/samples.md, section "MID_AutoRefDemo_ExternalAndOverwrite.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Core.AutoReference;

namespace MidManStudio.Core.Tests.AutoReference
{
    /// <summary>
    /// Script under test for demo 3: <c>_externalIcon</c> (the icon lives outside the panel's hierarchy),
    /// <c>_preAssigned</c> (assigned by hand before resolving) and <c>_manualOnly</c> (marked
    /// <c>[MID_NoAutoRef]</c>, so the resolver must leave it null).
    /// </summary>
    [MID_AutoRefable]
    public class MID_DemoExternalPanel : MonoBehaviour
    {
        [SerializeField] private MID_DemoIconSlot _externalIcon;
        [SerializeField] private MID_DemoLabelSlot _preAssigned;

        [MID_NoAutoRef]
        [SerializeField] private MID_DemoLabelSlot _manualOnly;

        public MID_DemoIconSlot ExternalIcon => _externalIcon;
        public MID_DemoLabelSlot PreAssigned => _preAssigned;
        public MID_DemoLabelSlot ManualOnly => _manualOnly;

        public void SetPreAssigned(MID_DemoLabelSlot value) => _preAssigned = value;
    }

    /// <summary>
    /// Auto Reference demo 3, external search root, overwrite protection and the opt-out attribute. Pass 1 runs with
    /// Overwrite Existing off and checks that the icon is found under a detached root, the pre-assigned field is kept
    /// and the <c>[MID_NoAutoRef]</c> field stays null. Pass 2 runs with Overwrite Existing on and checks that the
    /// opt-out field is still null. Its check for the pre-assigned field after pass 2 only tests that it is not null,
    /// which is also true when nothing was overwritten, so it does not prove the overwrite. Uses types from the demo 1
    /// file. Runs in Start when Run On Start is on (default), or from the context menu "Run Test".
    /// </summary>
    public class MID_AutoRefDemo_ExternalAndOverwrite : MonoBehaviour
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
            Header("AutoRef Demo 3 — External Root & Overwrite Protection");

            var root  = new GameObject("[AutoRef Demo] External & Overwrite");
            var panel = root.AddComponent<MID_DemoExternalPanel>();

            // Nothing under `root` for the icon — it only exists under a detached root.
            var externalRoot = new GameObject("[AutoRef Demo] External Root");
            var icon = new GameObject("Icon").AddComponent<MID_DemoIconSlot>();
            icon.transform.SetParent(externalRoot.transform);

            var originalLabel = new GameObject("OriginalLabel").AddComponent<MID_DemoLabelSlot>();
            originalLabel.transform.SetParent(root.transform);
            panel.SetPreAssigned(originalLabel); // pre-assign before resolving

            var replacementLabel = new GameObject("ReplacementLabel").AddComponent<MID_DemoLabelSlot>();
            replacementLabel.transform.SetParent(root.transform);

            // ── Pass 1: overwriteExisting = false ────────────────────────────────
            var options = new MID_AutoRefOptions
            {
                includeChildren     = true,
                includeExternalRoot = true,
                externalSearchRoot  = externalRoot.transform,
                overwriteExisting   = false
            };
            MID_AutoReferenceResolver.Resolve(root, options);

            Expect("External icon resolved from outside hierarchy", panel.ExternalIcon == icon);
            Expect("Pre-assigned field untouched (overwrite off)",  panel.PreAssigned == originalLabel);
            Expect("[MID_NoAutoRef] field stayed null",             panel.ManualOnly == null);

            // ── Pass 2: overwriteExisting = true ─────────────────────────────────
            options.overwriteExisting = true;
            MID_AutoReferenceResolver.Resolve(root, options);

            Expect("Pre-assigned field re-resolved (overwrite on)", panel.PreAssigned != null);
            Expect("[MID_NoAutoRef] field still stayed null",       panel.ManualOnly == null);

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
