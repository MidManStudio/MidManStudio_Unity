// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/fx.md, section "MID_FlipbookEffect.cs"
// ============================================================================
using System.Collections;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.FX
{
    /// <summary>
    /// Pooled sprite-sheet flipbook effect. <c>GlobalFXManager</c> uses it for entries that have
    /// <c>flipbookFrames</c>, because a persistent <see cref="ParticleSystem"/> cannot play a hand-drawn
    /// sprite sequence at an arbitrary world position.
    /// </summary>
    /// <remarks>
    /// Setup: register a prefab (SpriteRenderer, this component, ideally <c>LocalPoolReturn</c>) in
    /// <c>LocalObjectPool</c> with the type <c>PoolableObjectType.FlipbookEffect</c>.
    /// Usage, as done by <c>GlobalFXManager</c>: fetch an object with
    /// <c>LocalObjectPool.Instance.GetObject(PoolableObjectType.FlipbookEffect, pos, rot)</c>, then call
    /// <see cref="Play"/>. After the last frame the effect returns itself: through <c>LocalPoolReturn</c>
    /// if the prefab has one, else through the pool directly, else by deactivating the object.
    /// </remarks>
    [RequireComponent(typeof(SpriteRenderer))]
    public class MID_FlipbookEffect : MonoBehaviour
    {
        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;

        private SpriteRenderer _renderer;
        private Pools.LocalPoolReturn _poolReturn;

        private Coroutine _playCoroutine;
        private bool _returned;

        private void Awake()
        {
            _renderer = GetComponent<SpriteRenderer>();
            _poolReturn = GetComponent<Pools.LocalPoolReturn>();
        }

        private void OnEnable() => _returned = false;
        private void OnDisable() => StopPlayCoroutine();

        /// <summary>
        /// Plays through frames at the given fps, then returns to pool.
        /// rotation is applied to the transform directly (2D: pass a Z-only euler rotation).
        /// </summary>
        public void Play(Sprite[] frames, float fps, Vector3 position, Quaternion rotation)
        {
            if (frames == null || frames.Length == 0) { Return(); return; }

            ResetState();
            transform.SetPositionAndRotation(position, rotation);
            _renderer.enabled = true;

            float frameDuration = 1f / Mathf.Max(fps, 1f);
            _playCoroutine = StartCoroutine(PlayFrames(frames, frameDuration));

            MID_Logger.LogDebug(_logLevel, $"Flipbook: {frames.Length} frames @ {fps}fps",
                nameof(MID_FlipbookEffect), nameof(Play));
        }

        /// <summary>Return to pool immediately, mid-playback if needed.</summary>
        public void Return()
        {
            if (_returned) return;
            _returned = true;

            StopPlayCoroutine();
            ResetState();

            if (_poolReturn != null)
            {
                _poolReturn.ReturnToPoolNow();
            }
            else if (Pools.LocalObjectPool.Instance != null &&
                     Pools.LocalObjectPool.Instance.IsRegistered(
                         Pools.PoolableObjectType.FlipbookEffect))
            {
                Pools.LocalObjectPool.Instance.ReturnObject(
                    gameObject, Pools.PoolableObjectType.FlipbookEffect);
            }
            else
            {
                gameObject.SetActive(false);
            }
        }

        private IEnumerator PlayFrames(Sprite[] frames, float frameDuration)
        {
            for (int i = 0; i < frames.Length; i++)
            {
                _renderer.sprite = frames[i];
                yield return new WaitForSeconds(frameDuration);
            }
            Return();
        }

        private void ResetState()
        {
            StopPlayCoroutine();
            if (_renderer != null) _renderer.sprite = null;
        }

        private void StopPlayCoroutine()
        {
            if (_playCoroutine == null) return;
            StopCoroutine(_playCoroutine);
            _playCoroutine = null;
        }
    }
}
