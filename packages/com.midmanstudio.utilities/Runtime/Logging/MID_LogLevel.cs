// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/logging.md, section "MID_LogLevel.cs"
// ============================================================================
namespace MidManStudio.Core.Logging
{
    /// <summary>
    /// Verbosity threshold used throughout <see cref="MID_Logger"/>. Each
    /// <c>MID_Logger.LogX</c> call takes the caller's configured level and
    /// only emits if that level is at or above the message's own severity
    /// (see <see cref="MID_Logger.ShouldLog"/>). Higher values are more
    /// verbose, so setting a level shows everything at or below it in this
    /// list.
    /// </summary>
    public enum MID_LogLevel
    {
        /// <summary>Nothing is logged, regardless of message severity.</summary>
        None = 0,
        /// <summary>Only errors and exceptions.</summary>
        Error = 1,
        /// <summary>Errors, plus info and warning messages (there is no separate Warning level; see <see cref="MID_Logger.LogWarning"/>).</summary>
        Info = 2,
        /// <summary>Everything above, plus debug messages.</summary>
        Debug = 3,
        /// <summary>Everything, including the most granular per-frame/per-call messages.</summary>
        Verbose = 4
    }
}
