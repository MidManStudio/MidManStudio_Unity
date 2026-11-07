// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/scenemanagement.md, section "SceneNetworkDependency.cs"
// ============================================================================
namespace MidManStudio.Core.SceneManagement
{
    /// <summary>
    /// What network state a scene needs. Stored per scene in the generated <c>SceneRegistry</c>.
    /// <c>MID_SceneLoader</c> enforces <see cref="InternetRequired"/>, and <c>MID_SceneTransitionController</c>
    /// picks the network loader for <see cref="NetworkSessionRequired"/>. Nothing reads <see cref="Optional"/>.
    /// </summary>
    public enum SceneNetworkDependency
    {
        /// <summary>Scene works entirely without network.</summary>
        None,

        /// <summary>Scene requires an active internet connection to load.</summary>
        InternetRequired,

        /// <summary>Scene requires NGO to be listening (host or client).</summary>
        NetworkSessionRequired,

        /// <summary>Scene works better with network but falls back gracefully.</summary>
        Optional
    }
}
