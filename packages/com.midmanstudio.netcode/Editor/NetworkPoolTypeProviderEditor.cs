// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.netcode.md, section "NetworkPoolTypeProviderEditor.cs"
// ============================================================================

// Custom Inspector for NetworkPoolTypeProviderSO. The UI (including the
// "Import Entries from JSON" panel) lives in PoolTypeProviderEditorBase in
// com.midmanstudio.utilities. This class only hooks it up, the same way
// ObjectPoolTypeProviderEditor and ParticlePoolTypeProviderEditor do.

using UnityEditor;
using MidManStudio.Core.Pools.Generator;
using MidManStudio.Netcode.Generator;

namespace MidManStudio.Netcode.Editor
{
    [CustomEditor(typeof(NetworkPoolTypeProviderSO))]
    public sealed class NetworkPoolTypeProviderEditor : PoolTypeProviderEditorBase { }
}
