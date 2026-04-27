// MID_SpawnableAudio.cs
// Pooled audio object. Supports one-shot, looping-follow, and sequential
// (flight → collision) playback modes.
//
// Requires a pool entry of type PoolableObjectType with value matching
// PoolTypeId.SpawnableAudio (generated enum value 0 by default).
//
// SETUP:
//   Register prefab in LocalObjectPool inspector with typeId = PoolTypeId.SpawnableAudio
//   (value 0 from the generated enum, member name "SpawnableAudio").
//
// USAGE:
//   var go  = LocalObjectPool.Instance.GetObject(PoolableObjectType.SpawnableAudio, pos, rot);
//   var sfx = go.GetComponent<MID_SpawnableAudio>();
//   sfx.PlayOneShot(clip, pos);

using System.Collections;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Audio
{
    [RequireComponent(typeof(AudioSource))]
    public class MID_SpawnableAudio : MonoBehaviour
    {
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        private AudioSource _source;
        private Pools.LocalPoolReturn _poolReturn;

        // Follow state
        private Transform _followTarget;
        private Vector3   _followOffset;
        private bool      _isFollowing;

        // Sequential mode
        private bool      _sequentialMode;
        private AudioClip _collisionClip;
        private bool      _awaitingCollision;

        private Coroutine _lifetimeCoroutine;
        private bool      _returned;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            _source      = GetComponent<AudioSource>();
            _poolReturn  = GetComponent<Pools.LocalPoolReturn>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
        }

        private void OnEnable()  => _returned = false;
        private void OnDisable() => StopLifetimeCoroutine();

        private void Update()
        {
            if (!_isFollowing) return;

            if (_followTarget == null || !_followTarget.gameObject.activeInHierarchy)
            {
                _isFollowing = false;
                Return();
                return;
            }

            transform.position = _followTarget.position + _followOffset;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Fire-and-forget. Returns to pool when clip ends.</summary>
        public void PlayOneShot(AudioClip clip, Vector3 position,
                                float volume = 1f, float pitch = 1f)
        {
            if (clip == null) { Return(); return; }
            ResetState();
            transform.position = position;
            Configure(clip, volume, pitch, loop: false);
            _source.Play();
            _lifetimeCoroutine = StartCoroutine(
                ReturnAfter(clip.length / Mathf.Max(pitch, 0.01f)));

            MID_Logger.LogDebug(_logLevel, $"OneShot: {clip.name}",
                nameof(MID_SpawnableAudio), nameof(PlayOneShot));
        }

        /// <summary>
        /// Looping audio that optionally follows a transform (no parenting).
        /// Call Return() or destroy followTarget to stop.
        /// </summary>
        public void PlayLooping(AudioClip clip, Vector3 position,
                                Transform followTarget = null,
                                Vector3 followOffset   = default,
                                float volume = 1f, float pitch = 1f)
        {
            if (clip == null) { Return(); return; }
            ResetState();
            transform.position = position;

            if (followTarget != null)
            {
                _followTarget = followTarget;
                _followOffset = followOffset;
                _isFollowing  = true;
            }

            Configure(clip, volume, pitch, loop: true);
            _source.Play();

            MID_Logger.LogDebug(_logLevel,
                $"Looping: {clip.name} follow:{followTarget?.name ?? "none"}",
                nameof(MID_SpawnableAudio), nameof(PlayLooping));
        }

        /// <summary>
        /// Sequential: plays flyingClip looped while following target.
        /// Call TriggerCollision() on impact to switch to collisionClip.
        /// </summary>
        public void PlaySequential(AudioClip flyingClip, AudioClip collisionClip,
                                   Vector3 position, Transform followTarget,
                                   float volume = 1f, float pitch = 1f)
        {
            if (flyingClip == null) { Return(); return; }
            ResetState();
            transform.position  = position;
            _sequentialMode     = true;
            _collisionClip      = collisionClip;
            _awaitingCollision  = true;

            if (followTarget != null)
            {
                _followTarget = followTarget;
                _isFollowing  = true;
            }

            Configure(flyingClip, volume, pitch, loop: true);
            _source.Play();

            MID_Logger.LogDebug(_logLevel, $"Sequential: {flyingClip.name}",
                nameof(MID_SpawnableAudio), nameof(PlaySequential));
        }

        /// <summary>Switch from flight clip to collision clip.</summary>
        public void TriggerCollision(float volume = 1f)
        {
            if (!_sequentialMode || !_awaitingCollision) return;
            _awaitingCollision = false;
            _isFollowing       = false;
            _followTarget      = null;

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

            // Use LocalPoolReturn if present, otherwise ask pool directly
            if (_poolReturn != null)
            {
                _poolReturn.ReturnToPoolNow();
            }
            else if (Pools.LocalObjectPool.Instance != null &&
                     Pools.LocalObjectPool.Instance.IsRegistered(
                         Pools.PoolableObjectType.SpawnableAudio))
            {
                Pools.LocalObjectPool.Instance.ReturnObject(
                    gameObject, Pools.PoolableObjectType.SpawnableAudio);
            }
            else
            {
                gameObject.SetActive(false);
            }
        }

        // ── Private ───────────────────────────────────────────────────────────

        private void Configure(AudioClip clip, float volume, float pitch, bool loop)
        {
            _source.clip   = clip;
            _source.volume = Mathf.Clamp01(volume);
            _source.pitch  = Mathf.Clamp(pitch, 0.1f, 3f);
            _source.loop   = loop;
        }

        private void ResetState()
        {
            StopLifetimeCoroutine();
            _source.Stop();
            _source.clip = null;
            _source.loop = false;

            _followTarget      = null;
            _followOffset      = Vector3.zero;
            _isFollowing       = false;
            _sequentialMode    = false;
            _collisionClip     = null;
            _awaitingCollision = false;
        }

        private void StopLifetimeCoroutine()
        {
            if (_lifetimeCoroutine == null) return;
            StopCoroutine(_lifetimeCoroutine);
            _lifetimeCoroutine = null;
        }

        private IEnumerator ReturnAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            Return();
        }
    }
}
