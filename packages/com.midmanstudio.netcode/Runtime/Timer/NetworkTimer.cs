// NetworkTimer.cs
// Fixed-interval network tick timer.
// Tracks elapsed time and exposes ShouldTick() to drive server tick loops.
// Part of com.midmanstudio.netcode.
//
// USAGE:
//   var timer = new NetworkTimer(serverTickRate: 60f);
//   void Update() {
//       timer.Update(Time.deltaTime);
//       while (timer.ShouldTick()) {
//           RunServerTick(timer.CurrentTick);
//       }
//   }

namespace MidManStudio.Core.Netcode
{
    /// <summary>
    /// Lightweight fixed-interval tick timer for server/client network loops.
    /// </summary>
    public class NetworkTimer
    {
        private float _accumulator;

        /// <summary>Seconds between ticks (1 / serverTickRate).</summary>
        public float MinTimeBetweenTicks { get; private set; }

        /// <summary>Total number of ticks fired since creation or last Reset().</summary>
        public int CurrentTick { get; private set; }

        /// <summary>
        /// Fractional progress toward the next tick [0, 1].
        /// Useful for client-side interpolation.
        /// </summary>
        public float LerpFraction =>
            MinTimeBetweenTicks > 0f
                ? _accumulator / MinTimeBetweenTicks
                : 0f;

        /// <param name="serverTickRate">Ticks per second (e.g. 60).</param>
        public NetworkTimer(float serverTickRate)
        {
            MinTimeBetweenTicks = serverTickRate > 0f
                ? 1f / serverTickRate
                : 1f / 60f;
        }

        /// <summary>Advance the timer. Call once per Update or FixedUpdate.</summary>
        public void Update(float deltaTime) => _accumulator += deltaTime;

        /// <summary>
        /// Returns true and advances the tick counter if enough time has elapsed.
        /// Call in a while loop to handle multiple ticks in one frame.
        /// </summary>
        public bool ShouldTick()
        {
            if (_accumulator < MinTimeBetweenTicks) return false;
            _accumulator -= MinTimeBetweenTicks;
            CurrentTick++;
            return true;
        }

        /// <summary>Reset accumulator and tick counter to zero.</summary>
        public void Reset()
        {
            _accumulator = 0f;
            CurrentTick  = 0;
        }

        /// <summary>Change tick rate at runtime (resets accumulator).</summary>
        public void SetTickRate(float tickRate)
        {
            MinTimeBetweenTicks = tickRate > 0f ? 1f / tickRate : 1f / 60f;
            _accumulator        = 0f;
        }
    }
}
