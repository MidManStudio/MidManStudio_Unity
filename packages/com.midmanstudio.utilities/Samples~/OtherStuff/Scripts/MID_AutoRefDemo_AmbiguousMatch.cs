// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/samples.md, section "MID_AutoRefDemo_AmbiguousMatch.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Core.AutoReference;

namespace MidManStudio.Core.Tests.AutoReference
{
    /// <summary>Script under test for demo 2: three fields of the same type, named health, mana and stamina.</summary>
    [MID_AutoRefable]
    public class MID_DemoAmbiguousPanel : MonoBehaviour
    {
        [SerializeField] private MID_DemoLabelSlot _healthLabel;
        [SerializeField] private MID_DemoLabelSlot _manaLabel;
        [SerializeField] private MID_DemoLabelSlot _staminaLabel;

        public MID_DemoLabelSlot HealthLabel  => _healthLabel;
        public MID_DemoLabelSlot ManaLabel    => _manaLabel;
        public MID_DemoLabelSlot StaminaLabel => _staminaLabel;
    }

    /// <summary>
    /// Auto Reference demo 2, several candidates of one type. It creates three <c>MID_DemoLabelSlot</c> children named
    /// StaminaLabel, HealthLabel and ManaLabel, in an order that differs from the field order, calls
    /// <c>MID_AutoReferenceResolver.Resolve</c> with Include Children on, and checks that each field got the child whose
    /// name matches it. Uses the type <c>MID_DemoLabelSlot</c> from the demo 1 file. Runs in Start when Run On Start is
    /// on (default), or from the context menu "Run Test".
    /// </summary>
    public class MID_AutoRefDemo_AmbiguousMatch : MonoBehaviour
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
            Header("AutoRef Demo 2 — Ambiguous Match");

            var root  = new GameObject("[AutoRef Demo] Ambiguous Match");
            var panel = root.AddComponent<MID_DemoAmbiguousPanel>();

            var stamina = new GameObject("StaminaLabel").AddComponent<MID_DemoLabelSlot>();
            stamina.transform.SetParent(root.transform);

            var health = new GameObject("HealthLabel").AddComponent<MID_DemoLabelSlot>();
            health.transform.SetParent(root.transform);

            var mana = new GameObject("ManaLabel").AddComponent<MID_DemoLabelSlot>();
            mana.transform.SetParent(root.transform);

            var options = new MID_AutoRefOptions { includeChildren = true };
            MID_AutoReferenceResolver.Resolve(root, options);

            Expect("_healthLabel -> HealthLabel",   panel.HealthLabel  == health);
            Expect("_manaLabel -> ManaLabel",        panel.ManaLabel    == mana);
            Expect("_staminaLabel -> StaminaLabel",  panel.StaminaLabel == stamina);

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
