// Custom Inspector for NetworkPoolTypeProviderSO. All the actual UI (the
// "Import Entries from JSON" panel) lives in PoolTypeProviderEditorBase, over
// in com.midmanstudio.utilities — this package's Editor asmdef already
// references both MidManStudio.Utilities and MidManStudio.Utilities.Editor
// (needed for IPoolableNetworkObject / MID_NamedList already), so this is
// just the one-line hookup, same as ObjectPoolTypeProviderEditor /
// ParticlePoolTypeProviderEditor.

using UnityEditor;
using MidManStudio.Core.Pools.Generator;
using MidManStudio.Netcode.Generator;

namespace MidManStudio.Netcode.Editor
{
    [CustomEditor(typeof(NetworkPoolTypeProviderSO))]
    public sealed class NetworkPoolTypeProviderEditor : PoolTypeProviderEditorBase { }
}
