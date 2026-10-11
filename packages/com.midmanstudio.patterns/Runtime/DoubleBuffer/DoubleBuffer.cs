// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/double-buffer.md, section "DoubleBuffer.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.DoubleBuffer
{
    /// <summary>
    /// Two instances of the same thing: readers use <see cref="Front"/> while the writer fills
    /// <see cref="Back"/>, and <see cref="Swap"/> exchanges the two. Because writes never touch what is being
    /// read, every reader sees one consistent snapshot per step, whatever order the updates run in.
    /// </summary>
    public sealed class DoubleBuffer<T>
    {
        private T _front;
        private T _back;

        /// <exception cref="ArgumentException">Both arguments are the same instance of a reference type.</exception>
        public DoubleBuffer(T front, T back)
        {
            if (!typeof(T).IsValueType && ReferenceEquals(front, back))
                throw new ArgumentException("The two buffers must be separate instances.");
            _front = front;
            _back = back;
        }

        /// <summary>Builds both buffers with <paramref name="create"/>.</summary>
        public static DoubleBuffer<T> Create(Func<T> create)
        {
            if (create == null) throw new ArgumentNullException(nameof(create));
            return new DoubleBuffer<T>(create(), create());
        }

        /// <summary>The buffer readers use.</summary>
        public T Front => _front;

        /// <summary>The buffer the writer fills.</summary>
        public T Back => _back;

        /// <summary>How many times <see cref="Swap()"/> has run.</summary>
        public int SwapCount { get; private set; }

        public void Swap()
        {
            T previousFront = _front;
            _front = _back;
            _back = previousFront;
            SwapCount++;
        }

        /// <summary>Swaps, then calls <paramref name="resetBack"/> on the new back buffer (the old front) so the next step starts clean.</summary>
        public void Swap(Action<T> resetBack)
        {
            if (resetBack == null) throw new ArgumentNullException(nameof(resetBack));
            Swap();
            resetBack(_back);
        }
    }
}
