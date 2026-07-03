// SHOWCASE 2 — Ambiguous multi-candidate resolution via fuzzy name matching.
// Three MID_DemoLabelSlot children exist, created out of field-declaration order —
// the resolver has to pick each one by name similarity, not by discovery order.

using UnityEngine;
using MidManStudio.Core.AutoReference;

namespace MidManStudio.Core.Tests.AutoReference
{
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
