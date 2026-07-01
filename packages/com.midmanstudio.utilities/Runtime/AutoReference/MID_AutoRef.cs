// Drop this on any GameObject that has one or more [MID_AutoRefable] scripts.
// Auto-fills their reference fields on self/children/(optional external root),
// disambiguating multi-candidate fields by fuzzy name match. Works in edit mode
// (via "Resolve Now" / OnValidate) and at runtime (Awake/Start), including in builds.

using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

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

#if UNITY_EDITOR
        // Edit-time "auto find" — fires whenever this component is added or its
        // inspector values change. Deferred via delayCall: Unity disallows some
        // operations (AddComponent, certain serialized writes) synchronously
        // inside OnValidate, so the real work is scheduled for right after.
        private void OnValidate()
        {
            if (_options.runMode != MID_AutoRefRunMode.OnValidate) return;
            if (Application.isPlaying) return; // Awake/Start already cover play mode

            EditorApplication.delayCall += DeferredResolve;
        }

        private void DeferredResolve()
        {
            if (this == null) return; // component may have been destroyed before delayCall fires
            ResolveNow();
        }
#endif
    }
}
