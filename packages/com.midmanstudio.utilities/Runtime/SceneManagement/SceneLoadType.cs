// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/scenemanagement.md, section "SceneLoadType.cs"
// ============================================================================
namespace MidManStudio.Core.SceneManagement
{
    /// <summary>
    /// How a scene is loaded. <c>MID_SceneLoader</c> handles <see cref="Single"/> and <see cref="Additive"/>
    /// and treats every value other than <see cref="Single"/> as additive. <see cref="NetworkAdditive"/>
    /// is meant for the netcode loader.
    /// </summary>
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
