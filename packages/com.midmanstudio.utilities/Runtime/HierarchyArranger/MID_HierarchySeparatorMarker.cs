// Marks a GameObject as an auto-generated separator so a re-run of the arranger
// can find and remove its own previous separators before inserting new ones,
// instead of accumulating more on every run. Kept in the runtime assembly (not
// Editor-only) so a separator accidentally left in a scene never breaks a build.

using UnityEngine;

namespace MidManStudio.Core.HierarchyArranger
{
    [AddComponentMenu("")]
    public class MID_HierarchySeparatorMarker : MonoBehaviour
    {
    }
}
