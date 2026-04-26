using UnityEngine;
using System;
using MidManStudio.Core.Logging;

/// <summary>
/// Example script showing proper region organization and logger usage
/// </summary>
public class ExampleScript : MonoBehaviour
{
    #region Serialized Fields

    [SerializeField] private MID_LogLevel _logLevel = MID_LogLevel.Debug;
    [SerializeField] private float _exampleValue = 10f;

    #endregion

    #region Private Fields

    private int _counter;
    private bool _isActive;

    #endregion

    #region Properties

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
        // Verbose logging - only use when debugging Update specifically
        MID_Logger.LogVerbose(_logLevel, $"Update tick: {_counter}", nameof(ExampleScript), nameof(Update));
        _counter++;
    }

    private void OnDestroy()
    {
        MID_Logger.LogInfo(_logLevel, "ExampleScript destroyed", nameof(ExampleScript), nameof(OnDestroy));
    }

    #endregion

    #region Public Methods

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
        _isActive = true;
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