// ISceneLoader.cs
// Common interface implemented by both MID_SceneLoader (utilities) and
// MID_NetworkSceneLoader (netcode). MID_SceneTransitionController talks
// through this interface so it has no hard dependency on either loader.

using System;

namespace MidManStudio.Core.SceneManagement
{
    public interface ISceneLoader
    {
        /// <summary>True while an async load operation is in progress.</summary>
        bool IsLoadingScene { get; }

        /// <summary>The SceneId currently being loaded, or -1 if idle.</summary>
        int CurrentLoadingSceneId { get; }

        /// <summary>Fires every frame while loading. Value is 0..1.</summary>
        Action<float> OnLoadProgressChanged { get; set; }

        /// <summary>Fires when a scene finishes loading. Payload is the SceneId.</summary>
        Action<int> OnSceneLoadCompleted { get; set; }

        /// <summary>Fires when a load fails. Payload is the error message.</summary>
        Action<string> OnSceneLoadFailed { get; set; }

        /// <summary>Begin loading a scene by its generated SceneId int value.</summary>
        void LoadScene(int sceneId, SceneLoadType loadType = SceneLoadType.Single, short delayMs = 0);

        /// <summary>Unload a scene by its generated SceneId int value.</summary>
        void UnloadScene(int sceneId);

        /// <summary>Returns true if the scene with this id is currently loaded.</summary>
        bool IsSceneLoaded(int sceneId);
    }
}
