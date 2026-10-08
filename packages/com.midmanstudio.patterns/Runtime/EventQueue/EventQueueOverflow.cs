// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/event-queue.md, section "EventQueueOverflow.cs"
// ============================================================================
namespace MidManStudio.Patterns.EventQueue
{
    /// <summary>What a bounded <see cref="EventQueue{TEvent}"/> does when an event is posted while it is full.</summary>
    public enum EventQueueOverflow
    {
        /// <summary>Discard the oldest queued event to make room, so the queue keeps the most recent events.</summary>
        DropOldest,

        /// <summary>Discard the event being posted, so the queue keeps the events it already has.</summary>
        DropNewest,

        /// <summary>Throw <see cref="System.InvalidOperationException"/> from Post.</summary>
        Throw
    }
}
