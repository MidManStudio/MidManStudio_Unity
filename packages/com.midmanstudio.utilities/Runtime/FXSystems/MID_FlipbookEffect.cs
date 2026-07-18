using System.Collections;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.FX
{
    /// <summary>
    /// Pooled sprite-sheet flipbook effect. Fills the gap GlobalFXManager had no
    /// answer for: it's ParticleSystem-only, with a single persistent emitter per
    /// (category, type) — there was no way to play a hand-drawn sprite sequence
    /// (a muzzle flash flipbook, e.g.) at an arbitrary world position, since a
    /// SpriteRenderer can't be "emitted" from multiple positions the way a
    /// ParticleSystem can via EmitParams.
    ///
    /// This plugs into the same slot the Pool Type Generator had already reserved
    /// but never built anything for — PoolableObjectType.FlipbookEffect
    /// (com.midmanstudio.utilities block, explicitOffset = 2).
    ///
    /// Mirrors MID_SpawnableAudio's exact pool-return pattern: fetch from
    /// LocalObjectPool, play, auto-return via LocalPoolReturn (or the pool
    /// directly, or SetActive(false) as a last resort).
    ///
    /// SETUP:
    ///   Register a prefab (SpriteRenderer + this component, ideally + LocalPoolReturn)
    ///   in LocalObjectPool with typeId = PoolableObjectType.FlipbookEffect.
    ///
    /// USAGE (called by GlobalFXManager when an FXEntry has flipbookFrames set):
    ///   var go = LocalObjectPool.Instance.GetObject(PoolableObjectType.FlipbookEffect, pos, rot);
    ///   go.GetComponent<MID_FlipbookEffect>().Play(frames, fps, pos, rot);
    /// </summary>
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
