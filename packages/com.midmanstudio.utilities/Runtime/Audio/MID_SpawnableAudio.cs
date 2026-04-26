// MID_SpawnableAudio.cs
// Pooled audio object. Handles one-shot, looping-follow, and sequential
// (e.g. flight sound → collision sound) playback modes.
// Requires a PoolableObjectType entry for your game's pool enum.
//
// SETUP: Add to a prefab with AudioSource. Register prefab in LocalObjectPool.
// USAGE:
//   var go  = LocalObjectPool.Instance.GetObject(PoolableObjectType.SpawnableAudio, pos, rot);
//   var sfx = go.GetComponent<MID_SpawnableAudio>();
//   sfx.PlayOneShot(clip, pos);
//   sfx.PlayLooping(clip, pos, followTarget);
using System.Collections;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;

namespace MidManStudio.Core.Audio
{
    [RequireComponent(typeof(AudioSource))]
    public class MID_SpawnableAudio : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        [Header("Pool")]
        [Tooltip("The PoolableObjectType this prefab is registered under.")]
        [SerializeField] private PoolableObjectType _poolType = PoolableObjectType.SpawnableAudio;

        #endregion

        #region Private Fields

        private AudioSource _source;
        private LocalPoolReturn _poolReturn;

        // Transform following (no parenting)
        private Transform _followTarget;
        private Vector3 _followOffset;
        private bool _isFollowing;

        // Sequential mode
        private bool _sequentialMode;
        private AudioClip _collisionClip;
        private bool _awaitingCollision;

        private Coroutine _lifetimeCoroutine;
        private bool _returned;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _source = GetComponent<AudioSource>();
            _poolReturn = GetComponent<LocalPoolReturn>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
        }

        private void OnEnable() => _returned = false;
        private void OnDisable() => StopLifetimeCoroutine();

        private void Update()
        {
            if (!_isFollowing) return;

            if (_followTarget == null || !_followTarget.gameObject.activeInHierarchy)
            {
                // Target gone — stop following and return
                _isFollowing = false;
                Return();
                return;
            }

            transform.position = _followTarget.position + _followOffset;
        }

        #endregion

        #region Public API

        /// <summary>Fire-and-forget. Returns to pool when clip ends.</summary>
        public void PlayOneShot(AudioClip clip, Vector3 position,
            float volume = 1f, float pitch = 1f)
        {
            if (clip == null) { Return(); return; }
            ResetState();
            transform.position = position;
            Configure(clip, volume, pitch, loop: false);
            _source.Play();
            _lifetimeCoroutine = StartCoroutine(ReturnAfter(clip.length / Mathf.Max(pitch, 0.01f)));

            MID_Logger.LogDebug(_logLevel, $"OneShot: {clip.name}",
                nameof(MID_SpawnableAudio), nameof(PlayOneShot));
        }

        /// <summary>
        /// Looping audio that follows a transform (no parenting).
        /// Call Return() or let followTarget be destroyed to stop.
        /// </summary>
        public void PlayLooping(AudioClip clip, Vector3 position,
            Transform followTarget = null, Vector3 followOffset = default,
            float volume = 1f, float pitch = 1f)
        {
            if (clip == null) { Return(); return; }
            ResetState();
            transform.position = position;

            if (followTarget != null)
            {
                _followTarget = followTarget;
                _followOffset = followOffset;
                _isFollowing = true;
            }

            Configure(clip, volume, pitch, loop: true);
            _source.Play();

            MID_Logger.LogDebug(_logLevel, $"Looping: {clip.name} follow:{followTarget?.name ?? "none"}",
                nameof(MID_SpawnableAudio), nameof(PlayLooping));
        }

        /// <summary>
        /// Sequential mode: plays flyingClip looped while following target.
        /// Call TriggerCollision() when the projectile hits to transition to collisionClip.
        /// </summary>
        public void PlaySequential(AudioClip flyingClip, AudioClip collisionClip,
            Vector3 position, Transform followTarget,
            float volume = 1f, float pitch = 1f)
        {
            if (flyingClip == null) { Return(); return; }
            ResetState();
            transform.position = position;
            _sequentialMode = true;
            _collisionClip = collisionClip;
            _awaitingCollision = true;

            if (followTarget != null)
            {
                _followTarget = followTarget;
                _isFollowing = true;
            }

            Configure(flyingClip, volume, pitch, loop: true);
            _source.Play();

            MID_Logger.LogDebug(_logLevel, $"Sequential: {flyingClip.name}",
                nameof(MID_SpawnableAudio), nameof(PlaySequential));
        }

        /// <summary>Call when projectile hits. Switches to collision clip then returns.</summary>
        public void TriggerCollision(float volume = 1f)
        {
            if (!_sequentialMode || !_awaitingCollision) return;
            _awaitingCollision = false;
            _isFollowing = false;
            _followTarget = null;

            _source.Stop();

            if (_collisionClip == null) { Return(); return; }

            Configure(_collisionClip, volume, _source.pitch, loop: false);
            _source.Play();
            _lifetimeCoroutine = StartCoroutine(
                ReturnAfter(_collisionClip.length / Mathf.Max(_source.pitch, 0.01f)));
        }

        /// <summary>Return to pool immediately.</summary>
        public void Return()
        {
            if (_returned) return;
            _returned = true;

            StopLifetimeCoroutine();
            _source.Stop();
            ResetState();

            if (_poolReturn != null)
                _poolReturn.ReturnToPoolNow();
            else if (LocalObjectPool.Instance != null &&
                     LocalObjectPool.Instance.IsRegistered(_poolType))
                LocalObjectPool.Instance.ReturnObject(gameObject, _poolType);
            else
                gameObject.SetActive(false);
        }

        #endregion

        #region Private

        private void Configure(AudioClip clip, float volume, float pitch, bool loop)
        {
            _source.clip = clip;
            _source.volume = Mathf.Clamp01(volume);
            _source.pitch = Mathf.Clamp(pitch, 0.1f, 3f);
            _source.loop = loop;
        }

        private void ResetState()
        {
            StopLifetimeCoroutine();
            _source.Stop();
            _source.clip = null;
            _source.loop = false;

            _followTarget = null;
            _followOffset = Vector3.zero;
            _isFollowing = false;
            _sequentialMode = false;
            _collisionClip = null;
            _awaitingCollision = false;
        }

        private void StopLifetimeCoroutine()
        {
            if (_lifetimeCoroutine != null)
            {
                StopCoroutine(_lifetimeCoroutine);
                _lifetimeCoroutine = null;
            }
        }

        private IEnumerator ReturnAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            Return();
        }

        #endregion
    }
}