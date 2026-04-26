// LocalParticleReturn.cs
// Auto-returns a pooled particle GameObject to LocalParticlePool.
// Automatically added by LocalParticlePool. Uses the generated PoolableParticleType enum.

using System.Collections;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Pools
{
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

        public void SetOriginalType(PoolableParticleType type)  => originalParticleType = type;
        public PoolableParticleType GetOriginalType()            => originalParticleType;

        public void SetMaxLifetime(float lifetime)
        {
            maxLifetime = lifetime;
            if (!gameObject.activeInHierarchy) return;
            if (_autoReturnCoroutine != null) StopCoroutine(_autoReturnCoroutine);
            _autoReturnCoroutine = StartCoroutine(AutoReturnAfterTime());
        }

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

        public void ForceReturn() => ReturnToPool();

        // ── Private ───────────────────────────────────────────────────────────

        private IEnumerator AutoReturnAfterTime()
        {
            yield return new WaitForSeconds(maxLifetime);
            ReturnToPool();
        }
    }
}
