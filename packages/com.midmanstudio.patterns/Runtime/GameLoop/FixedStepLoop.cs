// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.patterns/game-loop.md, section "FixedStepLoop.cs"
// ============================================================================
using System;

namespace MidManStudio.Patterns.GameLoop
{
    /// <summary>
    /// Runs simulation in fixed-size steps while the frame rate varies. Each frame the elapsed time is added
    /// to an accumulator and one step runs for every full step it contains, so the simulation advances by the
    /// same amount per second at any frame rate. <see cref="Alpha"/> is the fraction of the next step already
    /// elapsed, for interpolating what is drawn. Engine-free: the caller supplies the frame time.
    /// </summary>
    public sealed class FixedStepLoop
    {
        private readonly Action<float> _onStep;
        private readonly double _step;
        private double _accumulator;
        private float _timeScale = 1f;

        /// <param name="stepSeconds">Length of one simulation step. Above zero.</param>
        /// <param name="onStep">Called once per step with <paramref name="stepSeconds"/>.</param>
        /// <param name="maxStepsPerAdvance">Cap on steps in one <see cref="Advance"/> call. At least 1.</param>
        public FixedStepLoop(float stepSeconds, Action<float> onStep, int maxStepsPerAdvance = 5)
        {
            if (float.IsNaN(stepSeconds) || float.IsInfinity(stepSeconds) || stepSeconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(stepSeconds));
            if (onStep == null) throw new ArgumentNullException(nameof(onStep));
            if (maxStepsPerAdvance < 1) throw new ArgumentOutOfRangeException(nameof(maxStepsPerAdvance));
            StepSeconds = stepSeconds;
            _step = stepSeconds;
            _onStep = onStep;
            MaxStepsPerAdvance = maxStepsPerAdvance;
        }

        public float StepSeconds { get; private set; }

        public int MaxStepsPerAdvance { get; private set; }

        /// <summary>Time carried over that is not yet a full step.</summary>
        public float Accumulator => (float)_accumulator;

        /// <summary>0 to just under 1: how far the next step has progressed. Use it to blend the previous and current state when drawing.</summary>
        public float Alpha => (float)(_accumulator / _step);

        /// <summary>Total steps run since creation or the last <see cref="Reset"/>.</summary>
        public long StepCount { get; private set; }

        /// <summary>Steps that were due but skipped because <see cref="MaxStepsPerAdvance"/> was reached.</summary>
        public long DroppedSteps { get; private set; }

        /// <summary>Multiplies the frame time. 0 freezes the simulation without stopping the clock; above 1 speeds it up.</summary>
        public float TimeScale
        {
            get { return _timeScale; }
            set
            {
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) throw new ArgumentOutOfRangeException(nameof(value));
                _timeScale = value;
            }
        }

        /// <summary>While true, <see cref="Advance"/> does nothing and the accumulator is kept.</summary>
        public bool Paused { get; set; }

        /// <summary>
        /// Adds the frame time and runs the steps that are due. Returns how many ran. When more than
        /// <see cref="MaxStepsPerAdvance"/> are due (after a long hitch), the extra whole steps are discarded
        /// and counted in <see cref="DroppedSteps"/> instead of making the next frame even slower.
        /// An exception from <paramref name="onStep"/> propagates; steps run so far stay counted.
        /// </summary>
        public int Advance(float frameSeconds)
        {
            if (float.IsNaN(frameSeconds) || float.IsInfinity(frameSeconds) || frameSeconds < 0f)
                throw new ArgumentOutOfRangeException(nameof(frameSeconds));
            if (Paused) return 0;

            _accumulator += (double)frameSeconds * _timeScale;

            int steps = 0;
            while (_accumulator >= _step && steps < MaxStepsPerAdvance)
            {
                _accumulator -= _step;
                steps++;
                StepCount++;
                _onStep(StepSeconds);
            }

            if (_accumulator >= _step)
            {
                long dropped = (long)Math.Floor(_accumulator / _step);
                _accumulator -= dropped * _step;
                DroppedSteps += dropped;
            }
            return steps;
        }

        /// <summary>Clears the accumulator and the counters. <see cref="TimeScale"/> and <see cref="Paused"/> are kept.</summary>
        public void Reset()
        {
            _accumulator = 0d;
            StepCount = 0;
            DroppedSteps = 0;
        }
    }
}
