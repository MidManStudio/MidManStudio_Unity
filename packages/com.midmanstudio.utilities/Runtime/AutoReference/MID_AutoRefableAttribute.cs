
using System;

namespace MidManStudio.Core.AutoReference
{   /// <summary>
    /// Mark a MonoBehaviour class with this to opt it into MID_AutoReferenceResolver scanning.
    /// Without this attribute, the resolver skips the script entirely.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class MID_AutoRefableAttribute : Attribute
    {
        /// <summary>
        /// If true, the editor automatically adds a MID_AutoRef component to any
        /// GameObject that receives this script, if one isn't already present.
        /// Duplicate-safe — MID_AutoRef carries [DisallowMultipleComponent].
        /// </summary>
        public bool AutoAddComponent { get; }

        public MID_AutoRefableAttribute(bool autoAddComponent = false)
        {
            AutoAddComponent = autoAddComponent;
        }
    }
}
