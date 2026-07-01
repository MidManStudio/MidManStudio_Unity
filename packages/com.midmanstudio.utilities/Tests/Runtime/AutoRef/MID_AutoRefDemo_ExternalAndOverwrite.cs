// SHOWCASE 3 — External search root, overwrite protection, and [MID_NoAutoRef].
// _externalIcon lives entirely outside the panel's hierarchy (the way a detached
// Canvas would for UI); _preAssigned proves a manual assignment survives a re-run
// unless Overwrite Existing is on; _manualOnly proves the opt-out attribute holds.

using UnityEngine;
using MidManStudio.Core.AutoReference;

namespace MidManStudio.Core.Tests.AutoReference
{
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
