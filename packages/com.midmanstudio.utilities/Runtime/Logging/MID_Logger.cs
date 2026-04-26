using System;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MidManStudio.Core.Logging
{
    /// <summary>
    /// Singleton logger with static convenience methods.
    /// Class name ends with "Logger" for Unity console double-click support.
    /// Methods start with "Log" as required by Unity.
    /// </summary>
    public class MID_Logger : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private bool _enableStackTrace = true;
        [SerializeField] private StackTraceLogType _logStackTraceType = StackTraceLogType.ScriptOnly;
        [SerializeField] private StackTraceLogType _warningStackTraceType = StackTraceLogType.ScriptOnly;
        [SerializeField] private StackTraceLogType _errorStackTraceType = StackTraceLogType.ScriptOnly;

        #endregion

        #region Private Fields

        private static MID_Logger _instance;

        // Color constants for different log levels
        private const string COLOR_DEBUG = "white";
        private const string COLOR_INFO = "cyan";
        private const string COLOR_WARNING = "yellow";
        private const string COLOR_ERROR = "orange";
        private const string COLOR_EXCEPTION = "red";
        private const string COLOR_VERBOSE = "lightblue";

        #endregion

        #region Properties

        /// <summary>
         /// Gets the singleton instance. Creates one if it doesn't exist.
        /// Works in both Edit Mode and Play Mode.
        /// </summary>
        public static MID_Logger Instance
        {
            get
            {
                if (_instance == null)
                {
                    // Try to find existing instance
                    _instance = FindObjectOfType<MID_Logger>();

                    // Create new instance if none exists
                    if (_instance == null)
                    {
                        GameObject loggerObject = new GameObject("MID_Logger");
                        _instance = loggerObject.AddComponent<MID_Logger>();

                        // Don't destroy on load in play mode
                        if (Application.isPlaying)
                        {
                            DontDestroyOnLoad(loggerObject);
                        }
                    }
                }
                return _instance;
            }
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
                if (Application.isPlaying)
                {
                    DontDestroyOnLoad(gameObject);
                }

                ConfigureStackTrace();
            }
            else if (_instance != this)
            {
                DestroyImmediate(gameObject);
            }
        }

        #endregion

        #region Public Static Methods

        /// <summary>
        /// Log a debug message (only if logLevel is Debug or Verbose)
        /// </summary>
        [HideInCallstack]
        public static void LogDebug(MID_LogLevel logLevel, string message, string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Debug)) return;
            Instance.LogInternal(LogType.Log, message, className, methodName, COLOR_DEBUG);
        }

        /// <summary>
        /// Log an info message (only if logLevel is Info, Debug, or Verbose)
        /// </summary>
        [HideInCallstack]
        public static void LogInfo(MID_LogLevel logLevel, string message, string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Info)) return;
            Instance.LogInternal(LogType.Log, message, className, methodName, COLOR_INFO, "[INFO]");
        }

        /// <summary>
        /// Log a warning message (only if logLevel is Info, Debug, or Verbose)
        /// </summary>
        [HideInCallstack]
        public static void LogWarning(MID_LogLevel logLevel, string message, string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Info)) return;
            Instance.LogInternal(LogType.Warning, message, className, methodName, COLOR_WARNING);
        }

        /// <summary>
        /// Log an error message (only if logLevel is Error or higher)
        /// </summary>
        [HideInCallstack]
        public static void LogError(MID_LogLevel logLevel, string message, string className = "", string methodName = "", Exception e = null)
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Error)) return;
            Instance.LogInternal(LogType.Error, message, className, methodName, COLOR_ERROR, exception: e);
        }

        /// <summary>
        /// Log an exception (only if logLevel is Error or higher)
        /// </summary>
        [HideInCallstack]
        public static void LogException(MID_LogLevel logLevel, Exception e, string message = "", string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Error)) return;

            string fullMessage = string.IsNullOrEmpty(message)
                ? $"Exception occurred: {e.Message}"
                : message;

            Instance.LogInternal(LogType.Exception, fullMessage, className, methodName, COLOR_EXCEPTION, exception: e);
        }

        /// <summary>
        /// Log verbose/detailed information (only if logLevel is Verbose)
        /// </summary>
        [HideInCallstack]
        public static void LogVerbose(MID_LogLevel logLevel, string message, string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Verbose)) return;
            Instance.LogInternal(LogType.Log, message, className, methodName, COLOR_VERBOSE, "[VERBOSE]");
        }

        /// <summary>
        /// Checks if a message at the specified level should be logged
        /// </summary>
        public static bool ShouldLog(MID_LogLevel currentLevel, MID_LogLevel messageLevel)
        {
            if (currentLevel == MID_LogLevel.None) return false;
            return (int)currentLevel >= (int)messageLevel;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Configures Unity's stack trace settings based on serialized fields
        /// </summary>
        private void ConfigureStackTrace()
        {
            if (_enableStackTrace)
            {
                Application.SetStackTraceLogType(LogType.Log, _logStackTraceType);
                Application.SetStackTraceLogType(LogType.Warning, _warningStackTraceType);
                Application.SetStackTraceLogType(LogType.Error, _errorStackTraceType);
                Application.SetStackTraceLogType(LogType.Exception, _errorStackTraceType);

                Debug.Log($"[MID_Logger] Stack trace enabled - Log: {_logStackTraceType}, Warning: {_warningStackTraceType}, Error: {_errorStackTraceType}");
            }
            else
            {
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.None);

                Debug.Log("[MID_Logger] Stack trace disabled");
            }
        }

        /// <summary>
        /// Internal logging implementation with formatting.
        /// MUST start with "Log" for Unity console to work properly.
        /// </summary>
        [HideInCallstack]
        private void LogInternal(LogType logType, string message, string className, string methodName,
            string color, string prefix = "", Exception exception = null)
        {
            string formattedMessage = FormatMessage(message, className, methodName, color, prefix);

            // Use Unity's Debug methods directly for proper console linking
            switch (logType)
            {
                case LogType.Log:
                    Debug.Log(formattedMessage);
                    break;

                case LogType.Warning:
                    Debug.LogWarning(formattedMessage);
                    break;

                case LogType.Error:
                    Debug.LogError(formattedMessage);
                    if (exception != null)
                    {
                        Debug.LogException(exception);
                    }
                    break;

                case LogType.Exception:
                    if (exception != null)
                    {
                        Debug.LogException(exception);
                    }
                    Debug.LogError(formattedMessage);
                    break;
            }
        }

        /// <summary>
        /// Formats the log message with color and context
        /// </summary>
        private string FormatMessage(string message, string className, string methodName, string color, string prefix)
        {
            string formattedMessage = "";

            // Add color tag (only works in Unity Editor)
#if UNITY_EDITOR
            formattedMessage += $"<color={color}>";
#endif

            // Add prefix if provided
            if (!string.IsNullOrEmpty(prefix))
            {
                formattedMessage += $"{prefix} ";
            }

            // Add class name if provided
            if (!string.IsNullOrEmpty(className))
            {
                formattedMessage += $"[{className}]";
            }

            // Add method name if provided
            if (!string.IsNullOrEmpty(methodName))
            {
                formattedMessage += $" [{methodName}]";
            }

            // Add arrow and message
            if (!string.IsNullOrEmpty(className) || !string.IsNullOrEmpty(methodName))
            {
                formattedMessage += " -> ";
            }
            formattedMessage += message;

            // Close color tag
#if UNITY_EDITOR
            formattedMessage += "</color>";
#endif

            return formattedMessage;
        }

        #endregion

        #region Editor Only

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Reconfigure stack trace when settings change in inspector
            if (_instance == this)
            {
                ConfigureStackTrace();
            }
        }
#endif

        #endregion
    }
}