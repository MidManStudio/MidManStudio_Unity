// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/scenemanagement.md, section "ISceneLoader.cs"
// ============================================================================
using System;

namespace MidManStudio.Core.SceneManagement
{
    /// <summary>
    /// Common contract of the scene loaders: <c>MID_SceneLoader</c> (utilities) and
    /// <c>MID_NetworkSceneLoader</c> (netcode). <c>MID_SceneTransitionController</c> talks to loaders
    /// through this interface, so it has no hard dependency on either. Scenes are addressed by the
    /// integer value of the generated <c>SceneId</c>, which is the build index.
    /// </summary>
    public interface ISceneLoader
    {
        /// <summary>True while an async load operation is in progress.</summary>
        bool IsLoadingScene { get; }

        /// <summary>The SceneId currently being loaded, or -1 if idle.</summary>
        int CurrentLoadingSceneId { get; }

        /// <summary>Fires repeatedly while loading. Value is 0..1. <c>MID_SceneLoader</c> reports about every 50 ms.</summary>
        Action<float> OnLoadProgressChanged { get; set; }

        /// <summary>Fires when a scene finishes loading. Payload is the SceneId.</summary>
        Action<int> OnSceneLoadCompleted { get; set; }

        /// <summary>Fires when a load fails. Payload is the error message.</summary>
        Action<string> OnSceneLoadFailed { get; set; }

        /// <summary>Begin loading a scene by its generated SceneId int value. <paramref name="delayMs"/> is a wait in milliseconds before the load starts.</summary>
        void LoadScene(int sceneId, SceneLoadType loadType = SceneLoadType.Single, short delayMs = 0);

        /// <summary>Unload a scene by its generated SceneId int value.</summary>
        void UnloadScene(int sceneId);

        /// <summary>Returns true if the scene with this id is currently loaded.</summary>
        bool IsSceneLoaded(int sceneId);
    }
}
