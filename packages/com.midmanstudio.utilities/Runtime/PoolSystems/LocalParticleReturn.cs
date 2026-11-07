// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "LocalParticleReturn.cs"
// ============================================================================
using System.Collections;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Pools
{
    /// <summary>
    /// Returns a pooled particle GameObject to <see cref="LocalParticlePool"/> after <c>maxLifetime</c> seconds, or at once
    /// on request. Uses the generated <see cref="PoolableParticleType"/> enum. <see cref="LocalParticlePool"/> adds it to every
    /// instance it creates and sets the type and the lifetime from the config. A second return of the same use is ignored.
    /// </summary>
    public class LocalParticleReturn : MonoBehaviour
    {
        [SerializeField] private float               maxLifetime        = 10f;
        [SerializeField] private PoolableParticleType originalParticleType;
        [SerializeField] private MID_LogLevel        _logLevel          = MID_LogLevel.None;

        private Coroutine _autoReturnCoroutine;
        private bool      _returned;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void OnEnable()
        {
            _returned = false;
            if (_autoReturnCoroutine != null) StopCoroutine(_autoReturnCoroutine);
            _autoReturnCoroutine = StartCoroutine(AutoReturnAfterTime());
        }

        private void OnDisable()
        {
            if (_autoReturnCoroutine == null) return;
            StopCoroutine(_autoReturnCoroutine);
            _autoReturnCoroutine = null;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Sets the pool type this object returns to.</summary>
        public void SetOriginalType(PoolableParticleType type)  => originalParticleType = type;
        /// <summary>The pool type this object returns to.</summary>
        public PoolableParticleType GetOriginalType()            => originalParticleType;

        /// <summary>Sets the lifetime in seconds. When the object is active the timer starts again from zero.</summary>
        public void SetMaxLifetime(float lifetime)
        {
            maxLifetime = lifetime;
            if (!gameObject.activeInHierarchy) return;
            if (_autoReturnCoroutine != null) StopCoroutine(_autoReturnCoroutine);
            _autoReturnCoroutine = StartCoroutine(AutoReturnAfterTime());
        }

        /// <summary>Returns the object to the pool now. Destroys it when the pool is missing or the type is not registered. Ignored when already returned since the last enable.</summary>
        public void ReturnToPool()
        {
            if (_returned) return;
            _returned = true;

            if (_autoReturnCoroutine != null)
            {
                StopCoroutine(_autoReturnCoroutine);
                _autoReturnCoroutine = null;
            }

            if (LocalParticlePool.Instance != null)
            {
                if (LocalParticlePool.Instance.IsRegistered(originalParticleType))
                {
                    LocalParticlePool.Instance.ReturnObject(gameObject, originalParticleType);
                }
                else
                {
                    MID_Logger.LogError(_logLevel,
                        $"{originalParticleType} not registered — destroying.",
                        nameof(LocalParticleReturn));
                    Destroy(gameObject);
                }
            }
            else
            {
                MID_Logger.LogWarning(_logLevel, "LocalParticlePool unavailable — destroying.",
                    nameof(LocalParticleReturn));
                Destroy(gameObject);
            }
        }

        /// <summary>Same as <see cref="ReturnToPool"/>.</summary>
        public void ForceReturn() => ReturnToPool();

        // ── Private ───────────────────────────────────────────────────────────

        private IEnumerator AutoReturnAfterTime()
        {
            yield return new WaitForSeconds(maxLifetime);
            ReturnToPool();
        }
    }
}
