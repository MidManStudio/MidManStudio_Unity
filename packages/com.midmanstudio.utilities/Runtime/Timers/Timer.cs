using System;
using UnityEngine;

namespace MidManStudio.Core.Timers
{
    /// <summary>
    /// Base timer class with extended functionality
    /// </summary>
    public abstract class Timer
    {
        protected float initialTime;
        protected float Time { get; set; }
        public bool IsRunning { get; protected set; }
        public float Progress => initialTime > 0 ? Time / initialTime : 0f;

        public Action OnTimerStart = delegate { };
        public Action OnTimerStop = delegate { };

        protected Timer(float value)
        {
            initialTime = value;
            IsRunning = false;
        }

        public void Start()
        {
            Time = initialTime;
            if (!IsRunning)
            {
                IsRunning = true;
                OnTimerStart.Invoke();
            }
        }

        public void Stop()
        {
            if (IsRunning)
            {
                IsRunning = false;
                OnTimerStop.Invoke();
            }
        }

        public void Resume() => IsRunning = true;
        public void Pause() => IsRunning = false;
        public abstract void Tick(float deltaTime);
    }

    /// <summary>
    /// Countdown timer with completion detection
    /// </summary>
    public class CountdownTimer : Timer
    {
        public Action OnTimerComplete = delegate { };

        public CountdownTimer(float value) : base(value) { }

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

        public bool IsFinished => Time <= 0;
        public void Reset() => Time = initialTime;
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

        public override void Tick(float deltaTime)
        {
            if (IsRunning)
            {
                Time += deltaTime;
            }
        }

        public void Reset() => Time = 0;
        public float GetTime() => Time;
    }

    /// <summary>
    /// Interpolation modes for value transitions
    /// </summary>
    public enum InterpolationMode
    {
        Linear,
        EaseIn,
        EaseOut,
        EaseInOut,
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
        public Action<float> OnValueChanged = delegate { };
        public Action OnInterpolationComplete = delegate { };
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
        /// Start with ping-pong mode (goes from start to end, then back to start)
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
        public Action<float> OnValueChanged = delegate { };
        public Action OnComplete = delegate { };
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
    public class NetworkTimer
    {
        private float timer;
        public float minTimeBtwTicks { get; private set; }
        public int currentTick { get; private set; }
        public float lerpFraction => timer / minTimeBtwTicks;

        public NetworkTimer(float serverTickRate)
        {
            minTimeBtwTicks = 1f / serverTickRate;
            timer = 0f;
            currentTick = 0;
        }

        public void Update(float deltaTime)
        {
            timer += deltaTime;
        }

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

        public void Reset()
        {
            timer = 0f;
            currentTick = 0;
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
        /// Create a ping-pong timer (goes back and forth)
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