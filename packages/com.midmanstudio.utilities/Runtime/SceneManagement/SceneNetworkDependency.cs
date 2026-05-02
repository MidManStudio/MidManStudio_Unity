// SceneNetworkDependency.cs
// Describes what network state a scene requires to load.

namespace MidManStudio.Core.SceneManagement
{
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
