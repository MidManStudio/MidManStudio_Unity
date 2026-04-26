// LocalPoolReturn.cs
// Auto-returns a pooled GameObject to LocalObjectPool after a configured delay.
// Automatically added to pooled objects by LocalObjectPool.
// Uses the generated PoolableObjectType enum.

using System.Collections;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Pools
{
    [System.Serializable]
    public class ReturnConfig
    {
        [Header("Auto-Return")]
        public bool  autoReturn = true;
        public float duration   = 5f;

        [Tooltip("Destroy the object if the pool is unavailable instead of leaking it.")]
        public bool useAutoDestruct = true;
    }

    /// <summary>
    /// Attach to any pooled GameObject.
    /// Handles auto-return after a delay and immediate manual return.
    /// </summary>
    public class LocalPoolReturn : MonoBehaviour
    {
        [SerializeField] private MID_LogLevel  _logLevel    = MID_LogLevel.None;
        [SerializeField] private ReturnConfig  config       = new ReturnConfig();
        [SerializeField] private PoolableObjectType originalType;

        private Coroutine _returnCoroutine;

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void OnEnable()
        {
            if (config.autoReturn) StartAutoReturn();
        }

        private void OnDisable()
        {
            StopAutoReturn();
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void SetOriginalType(PoolableObjectType type)
        {
            originalType = type;
            MID_Logger.LogDebug(_logLevel, $"Type set to {type}.",
                nameof(LocalPoolReturn), nameof(SetOriginalType));
        }

        public PoolableObjectType GetOriginalType() => originalType;

        public void SetAutoReturn(bool enabled)
        {
            config.autoReturn = enabled;
            if (!gameObject.activeInHierarchy) return;
            if (enabled) StartAutoReturn();
            else         StopAutoReturn();
        }

        public void SetDuration(float newDuration)
        {
            config.duration = newDuration;
            if (gameObject.activeInHierarchy && config.autoReturn) StartAutoReturn();
        }

        /// <summary>Return immediately, bypassing the auto-return timer.</summary>
        public void ReturnToPoolNow()
        {
            MID_Logger.LogDebug(_logLevel, $"Manual return: {originalType}.",
                nameof(LocalPoolReturn), nameof(ReturnToPoolNow));
            ReturnToPool();
        }

        public bool IsScheduledForReturn() => config.autoReturn && _returnCoroutine != null;
        public float GetDuration()          => config.duration;
        public bool  IsAutoReturnEnabled()  => config.autoReturn;

        // ── Private ───────────────────────────────────────────────────────────

        private void StartAutoReturn()
        {
            StopAutoReturn();
            _returnCoroutine = StartCoroutine(ReturnAfterDelay());
        }

        private void StopAutoReturn()
        {
            if (_returnCoroutine == null) return;
            StopCoroutine(_returnCoroutine);
            _returnCoroutine = null;
        }

        private void ReturnToPool()
        {
            if (gameObject == null) return;
            StopAutoReturn();
            ResetComponents();

            if (LocalObjectPool.Instance != null)
            {
                if (LocalObjectPool.Instance.IsRegistered(originalType))
                {
                    LocalObjectPool.Instance.ReturnObject(gameObject, originalType);
                }
                else
                {
                    MID_Logger.LogError(_logLevel,
                        $"{originalType} not registered in LocalObjectPool.",
                        nameof(LocalPoolReturn), nameof(ReturnToPool));
                    if (config.useAutoDestruct) Destroy(gameObject);
                }
            }
            else
            {
                MID_Logger.LogError(_logLevel, "LocalObjectPool instance missing.",
                    nameof(LocalPoolReturn), nameof(ReturnToPool));
                if (config.useAutoDestruct) Destroy(gameObject);
            }
        }

        private void ResetComponents()
        {
            var audio = GetComponent<AudioSource>();
            if (audio != null) { audio.Stop(); audio.time = 0f; }

            var anim = GetComponent<Animator>();
            if (anim != null) anim.Play(0, 0, 0f);

            foreach (var trail in GetComponentsInChildren<TrailRenderer>())
                trail?.Clear();
        }

        private IEnumerator ReturnAfterDelay()
        {
            yield return new WaitForSeconds(config.duration);
            MID_Logger.LogDebug(_logLevel, $"Auto-return: {originalType}.",
                nameof(LocalPoolReturn), nameof(ReturnAfterDelay));
            ReturnToPool();
        }
    }
}
