using System.Collections;
using UnityEngine;
using MidManStudio.Core.Logging;
using MidManStudio.Core.Pools;


namespace MidManStudio.Core.Pools
{

    [System.Serializable]
    public class ReturnConfig
    {
        [Header("Auto-Return Configuration")]
        public bool autoReturn = true;
        public float duration = 5f;
        public bool useAutoDestruct = true;
    }

    /// <summary>
    /// LocalPoolReturn — Auto-returns a pooled GameObject to LocalObjectPool after a configured delay.
    /// Automatically added to pooled objects by LocalObjectPool. Performs cleanup on return:
    /// stops AudioSource, resets Animator, clears TrailRenderers.
    ///
    /// To return immediately from game code: call ReturnToPoolNow().
    /// To disable auto-return for a specific object: call SetAutoReturn(false).
    /// </summary>
    public class LocalPoolReturn : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.None;
        [SerializeField] private ReturnConfig config = new ReturnConfig();
        [SerializeField] private PoolableObjectType originalType;

        #endregion

        #region Private Fields

        private Coroutine _returnCoroutine;

        #endregion

        #region Unity Lifecycle

        private void OnEnable()
        {
            if (config.autoReturn)
                StartAutoReturn();
        }

        private void OnDisable()
        {
            StopAutoReturn();
        }

        #endregion

        #region Public Methods

        public void SetOriginalType(PoolableObjectType objectType)
        {
            originalType = objectType;
            MID_Logger.LogDebug(_logLevel, $"Type set to {objectType}.", nameof(LocalPoolReturn), nameof(SetOriginalType));
        }

        public PoolableObjectType GetOriginalType() => originalType;

        public void SetAutoReturn(bool enabled)
        {
            config.autoReturn = enabled;

            if (gameObject.activeInHierarchy)
            {
                if (config.autoReturn) StartAutoReturn();
                else StopAutoReturn();
            }

            MID_Logger.LogDebug(_logLevel, $"Auto-return {(enabled ? "enabled" : "disabled")} for {originalType}.", nameof(LocalPoolReturn), nameof(SetAutoReturn));
        }

        public void SetDuration(float newDuration)
        {
            config.duration = newDuration;
            if (gameObject.activeInHierarchy && config.autoReturn)
                StartAutoReturn();
        }

        /// <summary>Returns the object to the pool immediately, bypassing the auto-return timer.</summary>
        public void ReturnToPoolNow()
        {
            MID_Logger.LogDebug(_logLevel, $"Manual return for {originalType}.", nameof(LocalPoolReturn), nameof(ReturnToPoolNow));
            ReturnToPool();
        }

        public bool IsScheduledForReturn() => config.autoReturn && _returnCoroutine != null;
        public float GetDuration() => config.duration;
        public bool IsAutoReturnEnabled() => config.autoReturn;

        #endregion

        #region Private Methods

        private void StartAutoReturn()
        {
            StopAutoReturn();
            _returnCoroutine = StartCoroutine(ReturnAfterDelay());
        }

        private void StopAutoReturn()
        {
            if (_returnCoroutine != null)
            {
                StopCoroutine(_returnCoroutine);
                _returnCoroutine = null;
            }
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
                    MID_Logger.LogError(_logLevel, $"Type {originalType} not registered in LocalObjectPool.", nameof(LocalPoolReturn), nameof(ReturnToPool));
                    if (config.useAutoDestruct) Destroy(gameObject);
                }
            }
            else
            {
                MID_Logger.LogError(_logLevel, "LocalObjectPool instance missing.", nameof(LocalPoolReturn), nameof(ReturnToPool));
                if (config.useAutoDestruct) Destroy(gameObject);
            }
        }

        private void ResetComponents()
        {
            AudioSource audioSource = GetComponent<AudioSource>();
            if (audioSource != null) { audioSource.Stop(); audioSource.time = 0f; }

            Animator animator = GetComponent<Animator>();
            if (animator != null) animator.Play(0, 0, 0f);

            TrailRenderer[] trails = GetComponentsInChildren<TrailRenderer>();
            foreach (var trail in trails)
                if (trail != null) trail.Clear();
        }

        #endregion

        #region Coroutines

        private IEnumerator ReturnAfterDelay()
        {
            yield return new WaitForSeconds(config.duration);
            MID_Logger.LogDebug(_logLevel, $"Auto-return triggered for {originalType}.", nameof(LocalPoolReturn), nameof(ReturnAfterDelay));
            ReturnToPool();
        }

        #endregion
    }
}