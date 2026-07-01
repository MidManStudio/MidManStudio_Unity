// Drop this on any GameObject that has one or more [MID_AutoRefable] scripts.
// Auto-fills their reference fields on self/children/(optional external root),
// disambiguating multi-candidate fields by fuzzy name match. Works in edit mode
// (via "Resolve Now") and at runtime (Awake/Start), including in builds.

using UnityEngine;

namespace MidManStudio.Core.AutoReference
{
    [DisallowMultipleComponent]
    public class MID_AutoRef : MonoBehaviour
    {
        [SerializeField] private MID_AutoRefOptions _options = new MID_AutoRefOptions();

        public MID_AutoRefOptions Options => _options;

        private void Awake()
        {
            if (_options.runMode == MID_AutoRefRunMode.Awake) ResolveNow();
        }

        private void Start()
        {
            if (_options.runMode == MID_AutoRefRunMode.Start) ResolveNow();
        }

        [ContextMenu("Resolve Now")]
        public void ResolveNow()
        {
            MID_AutoReferenceResolver.Resolve(gameObject, _options);
        }
    }
}
