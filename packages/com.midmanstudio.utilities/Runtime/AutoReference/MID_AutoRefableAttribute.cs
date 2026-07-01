// Mark a MonoBehaviour class with this to opt it into MID_AutoReferenceResolver scanning.
// Without this attribute, the resolver skips the script entirely — no namespace guessing needed.

using System;

namespace MidManStudio.Core.AutoReference
{
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class MID_AutoRefableAttribute : Attribute
    {
    }
}
