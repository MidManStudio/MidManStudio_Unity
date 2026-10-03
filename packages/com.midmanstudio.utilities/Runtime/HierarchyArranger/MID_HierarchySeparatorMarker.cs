// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/hierarchyarranger.md, section "MID_HierarchySeparatorMarker.cs"
// ============================================================================
using UnityEngine;

namespace MidManStudio.Core.HierarchyArranger
{
    /// <summary>
    /// Marks a GameObject as an auto-generated separator so a re-run of
    /// <c>MID_HierarchyArranger</c> can find and remove its own previous
    /// separators before inserting new ones, instead of accumulating more
    /// on every run. Kept in the runtime assembly (not Editor-only) so a
    /// separator accidentally left in a scene never breaks a build. Hidden
    /// from the Add Component menu; nothing about it is meant to be added
    /// by hand.
    /// </summary>
    [AddComponentMenu("")]
    public class MID_HierarchySeparatorMarker : MonoBehaviour
    {
    }
}
