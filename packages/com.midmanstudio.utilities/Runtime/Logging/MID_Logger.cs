// MID_Logger.cs
// Level-gated singleton logger.
// In the Unity Editor, the level prefix (e.g. [INFO]) is rendered in colour using
// rich-text tags. The rest of the message is always plain text so the console
// detail pane and log files never show raw tag characters.

using System;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MidManStudio.Core.Logging
{
    public class MID_Logger : MonoBehaviour
    {
        #region Serialized Fields

        [SerializeField] private bool _enableStackTrace = true;
        [SerializeField] private StackTraceLogType _logStackTraceType     = StackTraceLogType.ScriptOnly;
        [SerializeField] private StackTraceLogType _warningStackTraceType = StackTraceLogType.ScriptOnly;
        [SerializeField] private StackTraceLogType _errorStackTraceType   = StackTraceLogType.ScriptOnly;

        #endregion

        #region Private Fields

        private static MID_Logger _instance;

        // Colours for the prefix token only — message body is always plain text.
        private const string COLOR_DEBUG     = "white";
        private const string COLOR_INFO      = "cyan";
        private const string COLOR_WARNING   = "yellow";
        private const string COLOR_ERROR     = "orange";
        private const string COLOR_EXCEPTION = "red";
        private const string COLOR_VERBOSE   = "lightblue";

        #endregion

        #region Singleton

        public static MID_Logger Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<MID_Logger>();
                    if (_instance == null)
                    {
                        var go = new GameObject("MID_Logger");
                        _instance = go.AddComponent<MID_Logger>();
                        if (Application.isPlaying)
                            DontDestroyOnLoad(go);
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
                if (Application.isPlaying) DontDestroyOnLoad(gameObject);
                ConfigureStackTrace();
            }
            else if (_instance != this)
            {
                DestroyImmediate(gameObject);
            }
        }

        #endregion

        #region Public Static — Standard Levels

        [HideInCallstack]
        public static void LogDebug(MID_LogLevel logLevel, string message,
            string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Debug)) return;
            Instance.LogInternal(LogType.Log, message, className, methodName,
                COLOR_DEBUG, "[DEBUG]");
        }

        [HideInCallstack]
        public static void LogInfo(MID_LogLevel logLevel, string message,
            string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Info)) return;
            Instance.LogInternal(LogType.Log, message, className, methodName,
                COLOR_INFO, "[INFO]");
        }

        [HideInCallstack]
        public static void LogWarning(MID_LogLevel logLevel, string message,
            string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Info)) return;
            Instance.LogInternal(LogType.Warning, message, className, methodName,
                COLOR_WARNING, "");
        }

        [HideInCallstack]
        public static void LogError(MID_LogLevel logLevel, string message,
            string className = "", string methodName = "", Exception e = null)
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Error)) return;
            Instance.LogInternal(LogType.Error, message, className, methodName,
                COLOR_ERROR, "", e);
        }

        [HideInCallstack]
        public static void LogException(MID_LogLevel logLevel, Exception e,
            string message = "", string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Error)) return;
            string full = string.IsNullOrEmpty(message)
                ? $"Exception occurred: {e?.Message}"
                : message;
            Instance.LogInternal(LogType.Exception, full, className, methodName,
                COLOR_EXCEPTION, "", e);
        }

        [HideInCallstack]
        public static void LogVerbose(MID_LogLevel logLevel, string message,
            string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Verbose)) return;
            Instance.LogInternal(LogType.Log, message, className, methodName,
                COLOR_VERBOSE, "[VERBOSE]");
        }

        #endregion

        #region Public Static — Colour Override

        /// <summary>
        /// Log a Debug-level message where the prefix token is rendered in a custom colour.
        /// The message body remains plain text; only the prefix is coloured.
        /// </summary>
        [HideInCallstack]
        public static void LogWithColor(MID_LogLevel logLevel, string message, string color,
            string className = "", string methodName = "", string prefix = "[LOG]")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Debug)) return;
            Instance.LogInternal(LogType.Log, message, className, methodName, color, prefix);
        }

        [HideInCallstack]
        public static void LogWarningWithColor(MID_LogLevel logLevel, string message, string color,
            string className = "", string methodName = "")
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Info)) return;
            Instance.LogInternal(LogType.Warning, message, className, methodName, color, "");
        }

        [HideInCallstack]
        public static void LogErrorWithColor(MID_LogLevel logLevel, string message, string color,
            string className = "", string methodName = "", Exception e = null)
        {
            if (!ShouldLog(logLevel, MID_LogLevel.Error)) return;
            Instance.LogInternal(LogType.Error, message, className, methodName, color, "", e);
        }

        #endregion

        #region Level Check

        public static bool ShouldLog(MID_LogLevel currentLevel, MID_LogLevel messageLevel)
        {
            if (currentLevel == MID_LogLevel.None) return false;
            return (int)currentLevel >= (int)messageLevel;
        }

        #endregion

        #region Internal

        [HideInCallstack]
        internal void LogInternal(LogType logType, string message, string className,
            string methodName, string color, string prefix, Exception exception = null)
        {
            string formatted = FormatMessage(message, className, methodName, color, prefix);

            switch (logType)
            {
                case LogType.Log:
                    Debug.Log(formatted);
                    break;
                case LogType.Warning:
                    Debug.LogWarning(formatted);
                    break;
                case LogType.Error:
                    Debug.LogError(formatted);
                    if (exception != null) Debug.LogException(exception);
                    break;
                case LogType.Exception:
                    if (exception != null) Debug.LogException(exception);
                    else                   Debug.LogError(formatted);
                    break;
            }
        }

        /// <summary>
        /// Builds the final log string.
        /// Only the prefix token (e.g. [INFO]) is wrapped in a colour tag in the editor —
        /// the rest is always plain text so the console detail pane and log files are readable.
        /// </summary>
        private string FormatMessage(string message, string className,
            string methodName, string color, string prefix)
        {
            var sb = new System.Text.StringBuilder();

            // Colour only the prefix token, never the message body.
            if (!string.IsNullOrEmpty(prefix))
            {
#if UNITY_EDITOR
                sb.Append($"<color={color}>{prefix}</color> ");
#else
                sb.Append($"{prefix} ");
#endif
            }

            if (!string.IsNullOrEmpty(className))  sb.Append($"[{className}]");
            if (!string.IsNullOrEmpty(methodName)) sb.Append($" [{methodName}]");
            if (!string.IsNullOrEmpty(className) || !string.IsNullOrEmpty(methodName))
                sb.Append(" -> ");

            sb.Append(message);
            return sb.ToString();
        }

        private void ConfigureStackTrace()
        {
            if (_enableStackTrace)
            {
                Application.SetStackTraceLogType(LogType.Log,       _logStackTraceType);
                Application.SetStackTraceLogType(LogType.Warning,   _warningStackTraceType);
                Application.SetStackTraceLogType(LogType.Error,     _errorStackTraceType);
                Application.SetStackTraceLogType(LogType.Exception, _errorStackTraceType);
            }
            else
            {
                Application.SetStackTraceLogType(LogType.Log,       StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Warning,   StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Error,     StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.None);
            }
        }

        #endregion

        #region Editor

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_instance == this) ConfigureStackTrace();
        }
#endif

        #endregion
    }
}
