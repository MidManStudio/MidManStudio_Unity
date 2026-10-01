// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/performance.md, section "MID_FrameTimeStats.cs"
// ============================================================================
using System;

namespace MidManStudio.Core.Performance
{
    /// <summary>
    /// Fixed-size rolling window of frame times in milliseconds, with the
    /// summary numbers a frame-rate readout needs. Plain C# with no Unity
    /// dependency. Add() is O(1) and allocation-free; Recalculate() sorts a
    /// preallocated scratch copy, so call it on a timer instead of every frame.
    /// </summary>
    public sealed class MID_FrameTimeStats
    {
        private readonly float[] _samples;
        private readonly float[] _scratch;
        private int _head;
        private int _count;

        public int Capacity => _samples.Length;
        public int Count    => _count;

        /// <summary>Mean frame time over the window, in ms. 0 when empty.</summary>
        public float AverageMs { get; private set; }
        /// <summary>Fastest frame in the window, in ms.</summary>
        public float BestMs    { get; private set; }
        /// <summary>Slowest frame in the window, in ms.</summary>
        public float WorstMs   { get; private set; }
        /// <summary>Mean of the slowest 1% of frames, in ms (at least one frame).</summary>
        public float OnePercentLowMs { get; private set; }

        public float AverageFps       => AverageMs       > 0f ? 1000f / AverageMs       : 0f;
        public float BestFps          => BestMs          > 0f ? 1000f / BestMs          : 0f;
        public float WorstFps         => WorstMs         > 0f ? 1000f / WorstMs         : 0f;
        public float OnePercentLowFps => OnePercentLowMs > 0f ? 1000f / OnePercentLowMs : 0f;

        public MID_FrameTimeStats(int capacity = 300)
        {
            if (capacity < 1) capacity = 1;
            _samples = new float[capacity];
            _scratch = new float[capacity];
        }

        /// <summary>Adds one frame time. Non-finite or non-positive values are ignored.</summary>
        public void Add(float frameMs)
        {
            if (float.IsNaN(frameMs) || float.IsInfinity(frameMs) || frameMs <= 0f) return;
            _samples[_head] = frameMs;
            _head = (_head + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
        }

        public void Clear()
        {
            _head = 0;
            _count = 0;
            AverageMs = BestMs = WorstMs = OnePercentLowMs = 0f;
        }

        /// <summary>Recomputes the summary numbers from the current window.</summary>
        public void Recalculate()
        {
            if (_count == 0)
            {
                AverageMs = BestMs = WorstMs = OnePercentLowMs = 0f;
                return;
            }

            double sum = 0.0;
            for (int i = 0; i < _count; i++)
            {
                float v = _samples[i];
                _scratch[i] = v;
                sum += v;
            }

            Array.Sort(_scratch, 0, _count); // ascending
            AverageMs = (float)(sum / _count);
            BestMs    = _scratch[0];
            WorstMs   = _scratch[_count - 1];

            int worstCount = Math.Max(1, _count / 100);
            double worstSum = 0.0;
            for (int i = _count - worstCount; i < _count; i++) worstSum += _scratch[i];
            OnePercentLowMs = (float)(worstSum / worstCount);
        }
    }
}
