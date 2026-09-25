// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/logging.md, section "MID_LoggerSettings.cs"
// ============================================================================
using UnityEngine;

namespace MidManStudio.Core.Logging
{
    /// <summary>
    /// ScriptableObject holding the project's default <see cref="MID_LogLevel"/>.
    /// Create one via <c>MidManStudio/Utilities/Logger Settings</c>, place it
    /// in a <c>Resources</c> folder, and manage it from the
    /// <c>MidManStudio &gt; Utilities &gt; Logger Manager</c> editor window
    /// (see <c>MID_LoggerEditorWindow</c>).
    /// </summary>
[CreateAssetMenu(fileName="MID_LoggerSettings",
    menuName="MidManStudio/Utilities/Logger Settings", order=140)]
    public class MID_LoggerSettings : MID_BaseSO
    {
        [SerializeField] private MID_LogLevel _defaultLogLevel = MID_LogLevel.Debug;

        /// <summary>The project's default log level.</summary>
        public MID_LogLevel DefaultLogLevel
        {
            get => _defaultLogLevel;
            set => _defaultLogLevel = value;
        }
    }
}
