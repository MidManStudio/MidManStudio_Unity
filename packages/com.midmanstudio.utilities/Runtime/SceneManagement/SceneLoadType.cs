// SceneLoadType.cs
// How a scene should be loaded.

namespace MidManStudio.Core.SceneManagement
{
    public enum SceneLoadType
    {
        /// <summary>Standard Unity single-mode load. Previous scene is destroyed.</summary>
        Single,

        /// <summary>Additive load. Previous scenes remain.</summary>
        Additive,

        /// <summary>NGO-managed additive load. Synced across all clients.</summary>
        NetworkAdditive
    }
}
