// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/logging.md, section "ExampleScript.cs"
// ============================================================================
using UnityEngine;
using System;
namespace MidManStudio.Core.Logging
{
    /// <summary>
    /// Example script showing proper region organization and logger usage.
    /// Not part of the package's runtime API; a reference for the coding
    /// pattern (region layout, per-instance <see cref="MID_LogLevel"/>
    /// field, <see cref="MID_Logger"/> call sites for each lifecycle
    /// method) rather than something to attach or subclass.
    /// </summary>
    public class ExampleScript : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Debug;
        [SerializeField] private float _exampleValue = 10f;

        #endregion

        #region Private Fields

        private int _counter;

        #endregion

        #region Properties

        /// <summary>The current example value, set via <see cref="DoSomething"/>.</summary>
        public float ExampleValue
        {
            get => _exampleValue;
            private set => _exampleValue = value;
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            MID_Logger.LogInfo(_logLevel, "ExampleScript awakened", nameof(ExampleScript), nameof(Awake));
        }

        private void Start()
        {
            MID_Logger.LogDebug(_logLevel, "ExampleScript started", nameof(ExampleScript), nameof(Start));
            InitializeScript();
        }

        private void Update()
        {
            MID_Logger.LogVerbose(_logLevel, $"Update tick: {_counter}", nameof(ExampleScript), nameof(Update));
            _counter++;
        }

        private void OnDestroy()
        {
            MID_Logger.LogInfo(_logLevel, "ExampleScript destroyed", nameof(ExampleScript), nameof(OnDestroy));
        }

        #endregion

        #region Public Methods

        /// <summary>Demonstrates a try/catch around <see cref="MID_Logger.LogException"/>, then sets <see cref="ExampleValue"/> via <c>ProcessValue</c>.</summary>
        public void DoSomething(float value)
        {
            MID_Logger.LogInfo(_logLevel, $"Doing something with value: {value}", nameof(ExampleScript), nameof(DoSomething));

            try
            {
                ProcessValue(value);
            }
            catch (Exception e)
            {
                MID_Logger.LogException(_logLevel, e, "Failed to process value", nameof(ExampleScript), nameof(DoSomething));
            }
        }

        #endregion

        #region Private Methods

        private void InitializeScript()
        {
            MID_Logger.LogDebug(_logLevel, "Initializing script", nameof(ExampleScript), nameof(InitializeScript));
        }

        private void ProcessValue(float value)
        {
            if (value < 0)
            {
                MID_Logger.LogWarning(_logLevel, $"Negative value received: {value}", nameof(ExampleScript), nameof(ProcessValue));
            }

            _exampleValue = value;
        }

        #endregion

        #region Event Handlers

        private void OnCollisionEnter2D(Collision2D collision)
        {
            MID_Logger.LogDebug(_logLevel, $"Collision with {collision.gameObject.name}", nameof(ExampleScript), nameof(OnCollisionEnter2D));
        }

        #endregion
    }
}
