// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/autoreference.md, section "MID_AutoRefComponentWatcher.cs"
// ============================================================================
#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.AutoReference;

namespace MidManStudio.Core.EditorUtils.AutoReference
{
    /// <summary>
    /// Adds a <see cref="MID_AutoRef"/> component when a <c>[MID_AutoRefable(autoAddComponent: true)]</c> script
    /// is added to a GameObject, and starts a resolve when the existing or new component has run mode OnValidate.
    /// Hooks <c>ObjectFactory.componentWasAdded</c>, so there is no scene scanning. Safe against duplicates:
    /// it checks for an existing component and <see cref="MID_AutoRef"/> is <c>[DisallowMultipleComponent]</c>.
    /// </summary>
    [InitializeOnLoad]
    internal static class MID_AutoRefComponentWatcher
    {
        static MID_AutoRefComponentWatcher()
        {
            ObjectFactory.componentWasAdded += OnComponentAdded;
        }

        private static void OnComponentAdded(Component component)
        {
            if (component == null || component is MID_AutoRef) return;

            var attr = component.GetType().GetCustomAttribute<MID_AutoRefableAttribute>(inherit: true);
            if (attr == null) return;

            var go = component.gameObject;
            var autoRef = go.GetComponent<MID_AutoRef>();

            if (autoRef == null)
            {
                if (!attr.AutoAddComponent) return;
                autoRef = Undo.AddComponent<MID_AutoRef>(go); // safe: checked above + [DisallowMultipleComponent] backstop
            }

            if (autoRef.Options.runMode == MID_AutoRefRunMode.OnValidate)
            {
                var target = autoRef;
                EditorApplication.delayCall += () => { if (target != null) target.ResolveNow(); };
            }
        }
    }
}
#endif
