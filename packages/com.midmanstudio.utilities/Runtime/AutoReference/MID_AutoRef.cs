// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/autoreference.md, section "MID_AutoRef.cs"
// ============================================================================
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Core.AutoReference
{
    /// <summary>
    /// Fills the reference fields of the <c>[MID_AutoRefable]</c> scripts on the same GameObject. For each
    /// Component, GameObject or interface field it looks on this object, its children and optionally an
    /// external root. When several candidates fit, it picks the one whose GameObject name best matches the
    /// field name. Works in Edit Mode (Resolve Now, or automatically with run mode OnValidate) and at run time
    /// (run mode Awake or Start), including in builds.
    /// </summary>
    [DisallowMultipleComponent]
    public class MID_AutoRef : MonoBehaviour
    {
        [SerializeField] private MID_AutoRefOptions _options = new MID_AutoRefOptions();

        /// <summary>The settings this component resolves with.</summary>
        public MID_AutoRefOptions Options => _options;

        private void Awake()
        {
            if (_options.runMode == MID_AutoRefRunMode.Awake) ResolveNow();
        }

        private void Start()
        {
            if (_options.runMode == MID_AutoRefRunMode.Start) ResolveNow();
        }

        /// <summary>Resolves all <c>[MID_AutoRefable]</c> scripts on this GameObject now, with this component's options.</summary>
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
