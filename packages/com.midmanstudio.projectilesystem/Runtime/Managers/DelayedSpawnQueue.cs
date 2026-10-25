// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "DelayedSpawnQueue.cs"
// ============================================================================
using System;
using System.Collections.Generic;
using MidManStudio.Projectiles.Adapters;

namespace MidManStudio.Projectiles.Managers
{
    /// <summary>Which spawn path a queued projectile belongs to.</summary>
    public enum DelayedSpawnKind : byte
    {
        Offline,       // LocalProjectileManager.Spawn2D/3D
        FiringClient,  // predicted shot of the firing client, temp ids
        Network,       // shot fired by another client, real server ids
        Server         // ServerProjectileAuthority
    }

    /// <summary>
    /// Holds projectiles that start later than the shot that fired them. The sim
    /// buffer has no start delay, so a delayed projectile is only written to it
    /// when its time comes. The owner advances the queue from FixedUpdate, before
    /// its sim tick, so the clock is the same fixed step the sim runs on.
    /// </summary>
    public sealed class DelayedSpawnQueue
    {
        public struct Entry
        {
            public double          DueTime;   // queue clock value at which to spawn
            public SpawnPoint      Point;
            public uint            ProjId;
            public ushort          ConfigId;
            public float           Speed;     // base speed for the spawn params, <= 0 uses the config default
            public float           Elapsed;   // seconds the projectile is already into its flight when spawned
            public DelayedSpawnKind Kind;
            public object          Context;   // shared per shot, owner specific
        }

        private readonly List<Entry> _entries = new List<Entry>(32);

        /// <summary>Seconds of fixed time advanced so far.</summary>
        public double Now { get; private set; }

        public int Count => _entries.Count;

        public void Enqueue(in Entry entry) => _entries.Add(entry);

        /// <summary>
        /// Moves the clock forward and hands every due entry to
        /// <paramref name="spawn"/> in the order they were queued.
        /// </summary>
        public void Advance(float dt, Action<Entry> spawn)
        {
            Now += dt;

            int count = _entries.Count;
            if (count == 0) return;

            int keep = 0;
            for (int read = 0; read < count; read++)
            {
                Entry e = _entries[read];
                if (e.DueTime <= Now)
                {
                    spawn(e);
                    continue;
                }
                if (keep != read) _entries[keep] = e;
                keep++;
            }
            _entries.RemoveRange(keep, count - keep);
        }

        /// <summary>Swaps a temp projectile id for the real server id.</summary>
        public void Relink(uint oldId, uint newId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].ProjId != oldId) continue;
                Entry e = _entries[i];
                e.ProjId = newId;
                _entries[i] = e;
                return;
            }
        }

        public void Remove(uint projId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].ProjId != projId) continue;
                _entries.RemoveAt(i);
                return;
            }
        }

        public void Clear() => _entries.Clear();

        /// <summary>True when any of the first <paramref name="count"/> points starts late.</summary>
        public static bool AnyDelayed(SpawnPoint[] points, int count)
        {
            if (points == null) return false;
            int n = Math.Min(count, points.Length);
            for (int i = 0; i < n; i++)
                if (points[i].StartDelay > 0f) return true;
            return false;
        }
    }
}
