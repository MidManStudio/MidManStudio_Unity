using UnityEngine;
using System.Collections;
using MidManStudio.Core.Pools;

namespace MidManStudio.Core.Pools
{
    /// <summary>
    /// LocalParticleReturn — Auto-returns a pooled particle GameObject to LocalParticlePool
    /// after maxLifetime seconds. Automatically added to particles by LocalParticlePool.
    ///
    /// Call ReturnToPool() or ForceReturn() to return early from game code.
    /// Call SetMaxLifetime() to override the default lifetime per-spawn if needed.
    /// </summary>
    public class LocalParticleReturn : MonoBehaviour
    {
        #region Serialized Fields

        [Header("Auto-Return Configuration")]
        [SerializeField] private float maxLifetime = 10f;

        [Header("Debug")]
        [SerializeField] private PoolableParticleType originalParticleType;

        #endregion

        #region Private Fields

        private Coroutine _autoReturnCoroutine;
        private bool _hasBeenReturned = false;

        #endregion

        #region Unity Lifecycle

        private void OnEnable()
        {
            _hasBeenReturned = false;

            if (_autoReturnCoroutine != null)
                StopCoroutine(_autoReturnCoroutine);

            _autoReturnCoroutine = StartCoroutine(AutoReturnAfterTime());
        }

        private void OnDisable()
        {
            if (_autoReturnCoroutine != null)
            {
                StopCoroutine(_autoReturnCoroutine);
                _autoReturnCoroutine = null;
            }
        }

        #endregion

        #region Public Methods

        public void SetOriginalType(PoolableParticleType particleType) => originalParticleType = particleType;
        public PoolableParticleType GetOriginalType() => originalParticleType;

        public void SetMaxLifetime(float lifetime)
        {
            maxLifetime = lifetime;

            if (gameObject.activeInHierarchy && _autoReturnCoroutine != null)
            {
                StopCoroutine(_autoReturnCoroutine);
                _autoReturnCoroutine = StartCoroutine(AutoReturnAfterTime());
            }
        }

        /// <summary>Returns the particle to the pool immediately.</summary>
        public void ReturnToPool()
        {
            if (_hasBeenReturned) return;
            _hasBeenReturned = true;

            if (_autoReturnCoroutine != null)
            {
                StopCoroutine(_autoReturnCoroutine);
                _autoReturnCoroutine = null;
            }

            if (LocalParticlePool.Instance != null)
            {
                if (LocalParticlePool.Instance.IsRegistered(originalParticleType))
                    LocalParticlePool.Instance.ReturnObject(gameObject, originalParticleType);
                else
                {
                    Debug.LogError($"[LocalParticleReturn] Type {originalParticleType} not registered — destroying.");
                    Destroy(gameObject);
                }
            }
            else
            {
                Debug.LogWarning("[LocalParticleReturn] Pool unavailable — destroying.");
                Destroy(gameObject);
            }
        }

        public void ForceReturn() => ReturnToPool();

        #endregion

        #region Coroutines

        private IEnumerator AutoReturnAfterTime()
        {
            yield return new WaitForSeconds(maxLifetime);
            ReturnToPool();
        }

        #endregion
    }
}