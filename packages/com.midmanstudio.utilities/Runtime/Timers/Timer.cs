// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/timers.md, section "Timer.cs"
// ============================================================================
using System;
using UnityEngine;

namespace MidManStudio.Core.Timers
{
    /// <summary>
    /// Base timer class with extended functionality. Not itself frame-driven:
    /// call <see cref="Tick"/> every frame (or from whatever tick system you
    /// use) to advance it.
    /// </summary>
    public abstract class Timer
    {
        protected float initialTime;
        protected float Time { get; set; }
        /// <summary>True between <see cref="Start"/> and either <see cref="Stop"/> or (for <see cref="CountdownTimer"/>) natural completion.</summary>
        public bool IsRunning { get; protected set; }
        /// <summary>Elapsed-vs-initial ratio. 0 if <c>initialTime</c> is 0 (e.g. a fresh <see cref="StopwatchTimer"/>).</summary>
        public float Progress => initialTime > 0 ? Time / initialTime : 0f;

        /// <summary>Raised by <see cref="Start"/>, but only on the transition from stopped to running.</summary>
        public Action OnTimerStart = delegate { };
        /// <summary>Raised by <see cref="Stop"/>, but only on the transition from running to stopped.</summary>
        public Action OnTimerStop = delegate { };

        protected Timer(float value)
        {
            initialTime = value;
            IsRunning = false;
        }

        /// <summary>Resets the clock to <c>initialTime</c> and, if not already running, starts it and raises <see cref="OnTimerStart"/>. Safe to call again on an already-running timer to restart it without re-raising the event.</summary>
        public void Start()
        {
            Time = initialTime;
            if (!IsRunning)
            {
                IsRunning = true;
                OnTimerStart.Invoke();
            }
        }

        /// <summary>Stops the timer and raises <see cref="OnTimerStop"/>, but only if it was running.</summary>
        public void Stop()
        {
            if (IsRunning)
            {
                IsRunning = false;
                OnTimerStop.Invoke();
            }
        }

        /// <summary>Resumes ticking without resetting the clock or raising <see cref="OnTimerStart"/> (unlike <see cref="Start"/>).</summary>
        public void Resume() => IsRunning = true;
        /// <summary>Pauses ticking without raising <see cref="OnTimerStop"/> (unlike <see cref="Stop"/>).</summary>
        public void Pause() => IsRunning = false;
        /// <summary>Advances the timer by <paramref name="deltaTime"/> seconds. Call once per frame while running.</summary>
        public abstract void Tick(float deltaTime);
    }

    /// <summary>
    /// Countdown timer with completion detection
    /// </summary>
    public class CountdownTimer : Timer
    {
        /// <summary>Raised once, when the countdown reaches zero.</summary>
        public Action OnTimerComplete = delegate { };

        public CountdownTimer(float value) : base(value) { }

        /// <summary>Counts down by <paramref name="deltaTime"/>; stops and raises <see cref="OnTimerComplete"/> once it reaches zero.</summary>
        public override void Tick(float deltaTime)
        {
            if (IsRunning && Time > 0)
            {
                Time -= deltaTime;
            }

            if (IsRunning && Time <= 0)
            {
                Time = 0;
                Stop();
                OnTimerComplete.Invoke();
            }
        }

        /// <summary>True once the countdown has reached zero.</summary>
        public bool IsFinished => Time <= 0;
        /// <summary>Restores the countdown to its original <c>initialTime</c>.</summary>
        public void Reset() => Time = initialTime;
        /// <summary>Sets a new countdown length and restarts from it.</summary>
        public void Reset(float newTime)
        {
            initialTime = newTime;
            Reset();
        }
    }

    /// <summary>
    /// Stopwatch timer that counts up
    /// </summary>
    public class StopwatchTimer : Timer
    {
        public StopwatchTimer() : base(0) { }

        /// <summary>Counts up by <paramref name="deltaTime"/> while running. Never completes on its own.</summary>
        public override void Tick(float deltaTime)
        {
            if (IsRunning)
            {
                Time += deltaTime;
            }
        }

        /// <summary>Resets the elapsed time to zero (does not stop the timer).</summary>
        public void Reset() => Time = 0;
        /// <summary>Current elapsed time in seconds.</summary>
        public float GetTime() => Time;
    }

    /// <summary>
    /// Interpolation modes for value transitions
    /// </summary>
    public enum InterpolationMode
    {
        /// <summary>Constant rate of change.</summary>
        Linear,
        /// <summary>Starts slow, speeds up.</summary>
        EaseIn,
        /// <summary>Starts fast, slows down.</summary>
        EaseOut,
        /// <summary>Starts slow, speeds up through the middle, slows down again.</summary>
        EaseInOut,
        /// <summary>Uses the <c>AnimationCurve</c> passed to the constructor or <see cref="ValueInterpolationTimer.SetInterpolationMode"/>.</summary>
        Custom
    }

    /// <summary>
    /// Advanced timer that interpolates a value over time with callback support.
    /// Perfect for dissolve effects, color transitions, movement, etc.
    /// </summary>
    public class ValueInterpolationTimer
    {
        private float _currentValue;
        private float _startValue;
        private float _endValue;
        private float _duration;
        private float _elapsedTime;
        private bool _isRunning;
        private bool _isPingPong;
        private bool _isReversing;

        private InterpolationMode _interpolationMode;
        private AnimationCurve _customCurve;

        // Callbacks
        /// <summary>Raised every time <see cref="Tick"/> updates <see cref="CurrentValue"/>, including the final value on completion.</summary>
        public Action<float> OnValueChanged = delegate { };
        /// <summary>Raised once, when the interpolation finishes (after both legs of a ping-pong, if enabled).</summary>
        public Action OnInterpolationComplete = delegate { };
        /// <summary>Raised once, when <see cref="Start"/> or <see cref="StartPingPong"/> actually starts the timer.</summary>
        public Action OnInterpolationStart = delegate { };

        /// <summary>
        /// Current interpolated value
        /// </summary>
        public float CurrentValue => _currentValue;

        /// <summary>
        /// Is the interpolation currently running
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Progress from 0 to 1
        /// </summary>
        public float Progress => _duration > 0 ? Mathf.Clamp01(_elapsedTime / _duration) : 1f;

        /// <summary>
        /// Create a new value interpolation timer
        /// </summary>
        /// <param name="startValue">Starting value</param>
        /// <param name="endValue">Ending value</param>
        /// <param name="duration">Duration in seconds</param>
        /// <param name="mode">Interpolation mode</param>
        /// <param name="customCurve">Custom animation curve (only used if mode is Custom)</param>
        public ValueInterpolationTimer(
            float startValue,
            float endValue,
            float duration,
            InterpolationMode mode = InterpolationMode.Linear,
            AnimationCurve customCurve = null)
        {
            _startValue = startValue;
            _endValue = endValue;
            _currentValue = startValue;
            _duration = duration;
            _interpolationMode = mode;
            _customCurve = customCurve ?? AnimationCurve.Linear(0, 0, 1, 1);
            _elapsedTime = 0f;
            _isRunning = false;
            _isPingPong = false;
            _isReversing = false;
        }

        /// <summary>
        /// Start the interpolation
        /// </summary>
        public void Start()
        {
            if (!_isRunning)
            {
                _isRunning = true;
                _elapsedTime = 0f;
                _currentValue = _startValue;
                _isReversing = false;
                OnInterpolationStart.Invoke();
                OnValueChanged.Invoke(_currentValue);
            }
        }

        /// <summary>
        /// Start with ping-pong mode (goes from start to end, then back to start,
        /// then stops — this is one round trip, not continuous oscillation).
        /// </summary>
        public void StartPingPong()
        {
            _isPingPong = true;
            Start();
        }

        /// <summary>
        /// Stop the interpolation
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
        }

        /// <summary>
        /// Reset to start value
        /// </summary>
        public void Reset()
        {
            _elapsedTime = 0f;
            _currentValue = _startValue;
            _isReversing = false;
            OnValueChanged.Invoke(_currentValue);
        }

        /// <summary>
        /// Update the timer (call this every frame or from tick system)
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!_isRunning)
                return;

            _elapsedTime += deltaTime;

            // Calculate progress
            float progress = Progress;

            // Apply interpolation curve
            float curveValue = ApplyInterpolationCurve(progress);

            // Calculate current value
            if (_isReversing)
            {
                _currentValue = Mathf.Lerp(_endValue, _startValue, curveValue);
            }
            else
            {
                _currentValue = Mathf.Lerp(_startValue, _endValue, curveValue);
            }

            // Invoke callback
            OnValueChanged.Invoke(_currentValue);

            // Check if complete
            if (_elapsedTime >= _duration)
            {
                // Ensure final value is exact
                _currentValue = _isReversing ? _startValue : _endValue;
                OnValueChanged.Invoke(_currentValue);

                if (_isPingPong && !_isReversing)
                {
                    // Switch to reverse direction
                    _isReversing = true;
                    _elapsedTime = 0f;
                }
                else
                {
                    // Complete
                    _isRunning = false;
                    OnInterpolationComplete.Invoke();
                }
            }
        }

        /// <summary>
        /// Reconfigure the interpolation without stopping
        /// </summary>
        public void Reconfigure(float newStartValue, float newEndValue, float newDuration)
        {
            _startValue = newStartValue;
            _endValue = newEndValue;
            _duration = newDuration;
            _elapsedTime = 0f;
            _currentValue = _startValue;
            OnValueChanged.Invoke(_currentValue);
        }

        /// <summary>
        /// Set interpolation mode
        /// </summary>
        public void SetInterpolationMode(InterpolationMode mode, AnimationCurve customCurve = null)
        {
            _interpolationMode = mode;
            if (customCurve != null)
            {
                _customCurve = customCurve;
            }
        }

        private float ApplyInterpolationCurve(float t)
        {
            switch (_interpolationMode)
            {
                case InterpolationMode.Linear:
                    return t;

                case InterpolationMode.EaseIn:
                    return t * t;

                case InterpolationMode.EaseOut:
                    return t * (2f - t);

                case InterpolationMode.EaseInOut:
                    return t < 0.5f ? 2f * t * t : -1f + (4f - 2f * t) * t;

                case InterpolationMode.Custom:
                    return _customCurve.Evaluate(t);

                default:
                    return t;
            }
        }
    }

    /// <summary>
    /// Timer that automatically handles value changes over time with step increments.
    /// Perfect for dissolve effects that need to change in discrete steps.
    /// </summary>
    public class SteppedValueTimer
    {
        private float _currentValue;
        private float _startValue;
        private float _endValue;
        private float _stepSize;
        private float _stepInterval; // Time between steps in seconds
        private float _timeSinceLastStep;
        private bool _isRunning;
        private bool _isIncreasing;

        // Callbacks
        /// <summary>Raised every time <see cref="CurrentValue"/> changes (each step, plus on Start/Reset/Reconfigure).</summary>
        public Action<float> OnValueChanged = delegate { };
        /// <summary>Raised once, when <see cref="CurrentValue"/> reaches <c>endValue</c> and the timer stops itself.</summary>
        public Action OnComplete = delegate { };
        /// <summary>Raised after every step, including the final one (alongside <see cref="OnComplete"/>).</summary>
        public Action OnStepComplete = delegate { };

        /// <summary>
        /// Current value
        /// </summary>
        public float CurrentValue => _currentValue;

        /// <summary>
        /// Is the timer running
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Progress from 0 to 1
        /// </summary>
        public float Progress
        {
            get
            {
                float range = Mathf.Abs(_endValue - _startValue);
                if (range <= 0f) return 1f;
                float current = Mathf.Abs(_currentValue - _startValue);
                return Mathf.Clamp01(current / range);
            }
        }

        /// <summary>
        /// Create a stepped value timer
        /// </summary>
        /// <param name="startValue">Starting value</param>
        /// <param name="endValue">Ending value</param>
        /// <param name="stepSize">Size of each step</param>
        /// <param name="stepInterval">Time between steps in seconds</param>
        public SteppedValueTimer(float startValue, float endValue, float stepSize, float stepInterval)
        {
            _startValue = startValue;
            _endValue = endValue;
            _currentValue = startValue;
            _stepSize = Mathf.Abs(stepSize);
            _stepInterval = stepInterval;
            _timeSinceLastStep = 0f;
            _isRunning = false;
            _isIncreasing = endValue > startValue;
        }

        /// <summary>
        /// Start the stepped timer
        /// </summary>
        public void Start()
        {
            if (!_isRunning)
            {
                _isRunning = true;
                _timeSinceLastStep = 0f;
                _currentValue = _startValue;
                OnValueChanged.Invoke(_currentValue);
            }
        }

        /// <summary>
        /// Stop the timer
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
        }

        /// <summary>
        /// Reset to start value
        /// </summary>
        public void Reset()
        {
            _currentValue = _startValue;
            _timeSinceLastStep = 0f;
            OnValueChanged.Invoke(_currentValue);
        }

        /// <summary>
        /// Update the timer
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!_isRunning)
                return;

            _timeSinceLastStep += deltaTime;

            // Check if it's time for next step
            if (_timeSinceLastStep >= _stepInterval)
            {
                _timeSinceLastStep = 0f;

                // Take step
                if (_isIncreasing)
                {
                    _currentValue += _stepSize;

                    // Check if we've reached or passed end value
                    if (_currentValue >= _endValue)
                    {
                        _currentValue = _endValue;
                        OnValueChanged.Invoke(_currentValue);
                        OnStepComplete.Invoke();
                        _isRunning = false;
                        OnComplete.Invoke();
                        return;
                    }
                }
                else
                {
                    _currentValue -= _stepSize;

                    // Check if we've reached or passed end value
                    if (_currentValue <= _endValue)
                    {
                        _currentValue = _endValue;
                        OnValueChanged.Invoke(_currentValue);
                        OnStepComplete.Invoke();
                        _isRunning = false;
                        OnComplete.Invoke();
                        return;
                    }
                }

                OnValueChanged.Invoke(_currentValue);
                OnStepComplete.Invoke();
            }
        }

        /// <summary>
        /// Reconfigure the timer
        /// </summary>
        public void Reconfigure(float newStartValue, float newEndValue, float newStepSize, float newStepInterval)
        {
            _startValue = newStartValue;
            _endValue = newEndValue;
            _stepSize = Mathf.Abs(newStepSize);
            _stepInterval = newStepInterval;
            _currentValue = newStartValue;
            _isIncreasing = newEndValue > newStartValue;
            _timeSinceLastStep = 0f;
            OnValueChanged.Invoke(_currentValue);
        }
    }

    /// <summary>
    /// Lightweight fixed-interval tick timer for use in utility systems.
    /// NOTE: com.midmanstudio.netcode has its own NetworkTimer with PascalCase
    /// properties (MinTimeBetweenTicks, CurrentTick, LerpFraction) — prefer that
    /// one for all networked code. This version lives here for utility-only contexts
    /// that cannot take a netcode dependency.
    /// </summary>
    public class NetworkTimer
    {
        private float timer;

        /// <summary>Seconds between ticks (1 / serverTickRate). Always > 0.</summary>
        public float minTimeBtwTicks { get; private set; }

        /// <summary>Total ticks fired since creation or last Reset().</summary>
        public int currentTick { get; private set; }

        /// <summary>
        /// Fractional progress toward the next tick [0, 1].
        /// Useful for client-side interpolation.
        /// </summary>
        public float lerpFraction => minTimeBtwTicks > 0f ? timer / minTimeBtwTicks : 0f;

        /// <param name="serverTickRate">
        /// Ticks per second (e.g. 60).
        /// Zero or negative values fall back to 60 fps (1/60 s interval).
        /// </param>
        public NetworkTimer(float serverTickRate)
        {
            // Guard against divide-by-zero — fall back to 60 Hz
            minTimeBtwTicks = serverTickRate > 0f ? 1f / serverTickRate : 1f / 60f;
            timer = 0f;
            currentTick = 0;
        }

        /// <summary>Advance the timer. Call once per Update or FixedUpdate.</summary>
        public void Update(float deltaTime) => timer += deltaTime;

        /// <summary>
        /// Returns true and advances the tick counter if enough time has elapsed.
        /// Call in a while loop to handle multiple ticks in one frame.
        /// </summary>
        public bool ShouldTick()
        {
            if (timer >= minTimeBtwTicks)
            {
                timer -= minTimeBtwTicks;
                currentTick++;
                return true;
            }
            return false;
        }

        /// <summary>Reset accumulator and tick counter to zero.</summary>
        public void Reset()
        {
            timer = 0f;
            currentTick = 0;
        }

        /// <summary>Change tick rate at runtime (resets accumulator).</summary>
        public void SetTickRate(float tickRate)
        {
            minTimeBtwTicks = tickRate > 0f ? 1f / tickRate : 1f / 60f;
            timer = 0f;
        }
    }

    /// <summary>
    /// Helper class to create common timer configurations
    /// </summary>
    public static class TimerFactory
    {
        /// <summary>
        /// Create a dissolve effect timer (0 to 1 over duration)
        /// </summary>
        public static ValueInterpolationTimer CreateDissolveTimer(
            float duration,
            InterpolationMode mode = InterpolationMode.Linear)
        {
            return new ValueInterpolationTimer(0f, 1f, duration, mode);
        }

        /// <summary>
        /// Create an un-dissolve effect timer (1 to 0 over duration)
        /// </summary>
        public static ValueInterpolationTimer CreateUnDissolveTimer(
            float duration,
            InterpolationMode mode = InterpolationMode.Linear)
        {
            return new ValueInterpolationTimer(1f, 0f, duration, mode);
        }

        /// <summary>
        /// Create a stepped dissolve timer matching PlayerInitializer pattern
        /// </summary>
        public static SteppedValueTimer CreateSteppedDissolveTimer(
            float minValue,
            float maxValue,
            float stepSize,
            float stepInterval)
        {
            return new SteppedValueTimer(minValue, maxValue, stepSize, stepInterval);
        }

        /// <summary>
        /// Create a color fade timer
        /// </summary>
        public static ValueInterpolationTimer CreateAlphaFadeTimer(
            float startAlpha,
            float endAlpha,
            float duration,
            InterpolationMode mode = InterpolationMode.EaseInOut)
        {
            return new ValueInterpolationTimer(startAlpha, endAlpha, duration, mode);
        }

        /// <summary>
        /// Create a timer for a single ping-pong round trip (start to end, then
        /// back to start). Despite the name, this returns a plain, not-yet-started
        /// timer configured like any other — call <see cref="ValueInterpolationTimer.StartPingPong"/>
        /// on the result, not <see cref="ValueInterpolationTimer.Start"/>, or it
        /// will run one-way only. See this file's Fixes and Problems entry.
        /// </summary>
        public static ValueInterpolationTimer CreatePingPongTimer(
            float minValue,
            float maxValue,
            float duration,
            InterpolationMode mode = InterpolationMode.EaseInOut)
        {
            var timer = new ValueInterpolationTimer(minValue, maxValue, duration, mode);
            return timer;
        }
    }
}
