// Auto-adds a MID_AutoRef component when a [MID_AutoRefable(autoAddComponent: true)]
// script is added to a GameObject — no manual drag-and-drop needed. Hooks
// ObjectFactory.componentWasAdded, which fires exactly on add — no scene scanning.
// Duplicate-safe: checked here AND MID_AutoRef carries [DisallowMultipleComponent].

#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.AutoReference;

namespace MidManStudio.Core.EditorUtils.AutoReference
{
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
