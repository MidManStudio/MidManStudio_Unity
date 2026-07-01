// Field-level escape hatch. Put on any field inside a [MID_AutoRefable] script that
// looks like a match but should never be touched by the resolver (e.g. assigned by
// other runtime code, or a pooled reference).

using System;

namespace MidManStudio.Core.AutoReference
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class MID_NoAutoRefAttribute : Attribute
    {
    }
}
