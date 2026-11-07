// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/pool-systems.md, section "LocalPoolReturn.cs"
// ============================================================================
using System.Collections;
using UnityEngine;
using MidManStudio.Core.Logging;

namespace MidManStudio.Core.Pools
{
    /// <summary>Auto-return settings of <see cref="LocalPoolReturn"/>: on by default, after 5 seconds, and what to do when the pool is missing.</summary>
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
    /// Returns a pooled GameObject to <see cref="LocalObjectPool"/> after a delay, or at once on request. Uses the generated
    /// <see cref="PoolableObjectType"/> enum. <see cref="LocalObjectPool"/> adds it to every instance it creates and sets
    /// the type. With the default config the object returns itself 5 seconds after it is enabled, so call
    /// <see cref="SetAutoReturn"/> with false (or change the config on the prefab) for objects that live longer.
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

        /// <summary>Sets the pool type this object returns to.</summary>
        public void SetOriginalType(PoolableObjectType type)
        {
            originalType = type;
            MID_Logger.LogDebug(_logLevel, $"Type set to {type}.",
                nameof(LocalPoolReturn), nameof(SetOriginalType));
        }

        /// <summary>The pool type this object returns to.</summary>
        public PoolableObjectType GetOriginalType() => originalType;

        /// <summary>Turns the auto-return timer on or off. When the object is active the timer starts again or stops at once.</summary>
        public void SetAutoReturn(bool enabled)
        {
            config.autoReturn = enabled;
            if (!gameObject.activeInHierarchy) return;
            if (enabled) StartAutoReturn();
            else         StopAutoReturn();
        }

        /// <summary>Sets the auto-return time in seconds. When the object is active and auto-return is on, the timer starts again from zero.</summary>
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

        /// <summary>True while an auto-return timer is running.</summary>
        public bool IsScheduledForReturn() => config.autoReturn && _returnCoroutine != null;
        /// <summary>The auto-return time in seconds.</summary>
        public float GetDuration()          => config.duration;
        /// <summary>True when auto-return is on.</summary>
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
