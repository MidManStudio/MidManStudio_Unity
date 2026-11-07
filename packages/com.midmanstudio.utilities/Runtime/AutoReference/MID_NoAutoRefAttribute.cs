// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/autoreference.md, section "MID_NoAutoRefAttribute.cs"
// ============================================================================
using System;

namespace MidManStudio.Core.AutoReference
{
    /// <summary>
    /// Field-level escape hatch. Put it on a field inside a <c>[MID_AutoRefable]</c> script that looks like a
    /// match but must never be touched by the resolver, for example a field assigned by other run-time code or
    /// a pooled reference.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class MID_NoAutoRefAttribute : Attribute
    {
    }
}
