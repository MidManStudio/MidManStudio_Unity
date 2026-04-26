// RustSimAdapter.cs
// Translates raw HitResult / HitResult3D from Rust into game damage events.
//
// Owns:
//   Dictionary<uint, ServerProjectileData> — maps ProjId to gameplay data
//   (damage, owner, kill type, weapon type, crit state — all C# only, never in Rust)
//
// On hit (called by ServerProjectileAuthority every FixedUpdate):
//   1. Look up ServerProjectileData by ProjId
//   2. Compute final damage: curve(normalisedDistance) × headshot/crit multipliers
//   3. Check remaining collisions for piercers — kill or decrement
//   4. Fire OnProjectileHit event (MID_ProjectileNetworkBridge subscribes)
//   5. If piercer still alive: reduce damage by config falloff for subsequent hits
//
// NOT responsible for:
//   - Network RPCs (MID_ProjectileNetworkBridge owns those)
//   - Visual effects (ProjectileImpactHandler owns those)
//   - Applying damage to health components (game's damage system owns that)
//     — adapter fires an event with a DamagePayload; game code subscribes

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.InGame.ProjectileConfigs;
using MidManStudio.InGame.Managers;

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Damage payload — everything the game damage system needs
    //  No MidManStudio.InGame enums here — those are game-specific.
    //  The game subscribes to OnProjectileHit and casts/routes as needed.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when a projectile hits a registered target.
    /// Contains all data the game's damage system needs — no game-specific enums
    /// in this struct so the package stays game-agnostic.
    /// </summary>
    public struct ProjectileHitPayload
    {
        // ── Projectile identity ───────────────────────────────────────────────
        public uint   ProjId;
        public ushort ConfigId;
        public bool   Is3D;

        // ── Target identity ───────────────────────────────────────────────────
        /// Maps to NetworkObject ID on the hit target.
        public uint TargetId;

        // ── Damage ────────────────────────────────────────────────────────────
        /// Final computed damage after curve + crit + headshot.
        public float Damage;

        /// True if this hit was evaluated as a headshot.
        /// Headshot detection logic lives in the game layer — adapter receives
        /// it as a flag passed in from ServerProjectileAuthority.
        public bool  IsHeadshot;

        /// True if crit was rolled for this projectile at spawn.
        public bool  IsCrit;

        // ── Position ──────────────────────────────────────────────────────────
        public Vector3 HitPosition;

        // ── Owner (game damage system needs these for kill attribution) ────────
        public ulong OwnerMidId;
        public ulong FiredByNetworkObjectId;
        public bool  IsBotOwner;
        public byte  WeaponLevel;

        // ── Passthrough — game-specific data stored in ServerProjectileData ────
        /// Reference to the full ServerProjectileData.
        /// Game code can read kill type, weapon type, damage type etc. from here.
        public ServerProjectileData GameData;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Adapter
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Server-only. Translates Rust HitResults into game damage events.
    /// Create one instance — ServerProjectileAuthority owns it.
    /// </summary>
    public sealed class RustSimAdapter
    {
        // ── Active projectile data ────────────────────────────────────────────

        /// ProjId → gameplay data. Written at spawn, read on hit, removed on death.
        private readonly Dictionary<uint, ServerProjectileData> _projData
            = new Dictionary<uint, ServerProjectileData>(512);

        // ── Events ────────────────────────────────────────────────────────────

        /// Fired for each hit. Subscribe from MID_ProjectileNetworkBridge and the game's
        /// damage system. Both receive the same payload in the same frame.
        public event Action<ProjectileHitPayload> OnProjectileHit;

        /// Fired when a projectile dies (lifetime, max range, piercing exhausted).
        /// ServerProjectileAuthority uses this to signal TrailObjectPool.NotifyDead.
        public event Action<uint> OnProjectileDied;

        // ── Registration ──────────────────────────────────────────────────────

        /// <summary>
        /// Register a spawned projectile. Call immediately after BatchSpawnHelper
        /// writes the NativeProjectile into the sim buffer.
        /// </summary>
        public void Register(ServerProjectileData data)
        {
            _projData[data.projectileId_u32] = data;
        }

        /// <summary>
        /// Remove a projectile from the adapter. Called during CompactDeadSlots.
        /// </summary>
        public void Unregister(uint projId)
        {
            _projData.Remove(projId);
        }

        /// <summary>True if the adapter has gameplay data for this projId.</summary>
        public bool IsRegistered(uint projId) => _projData.ContainsKey(projId);

        // ── Hit processing (2D) ───────────────────────────────────────────────

        /// <summary>
        /// Process a 2D hit result from Rust check_hits_grid.
        /// Called by ServerProjectileAuthority for each hit in the hits array.
        ///
        /// isHeadshot — determined by the game layer (capsule collider zone check)
        ///              before calling this. Pass false for non-player targets.
        /// </summary>
        public void ProcessHit(in HitResult hit, bool isHeadshot)
        {
            if (!_projData.TryGetValue(hit.ProjId, out var data))
            {
                // Projectile not registered — already dead or not a sim projectile
                return;
            }

            var config = ProjectileConfigManager.GetProjectileConfig(
                data.projectileName);

            if (config == null)
            {
                Debug.LogError($"[RustSimAdapter] No config for projId {hit.ProjId} " +
                               $"(name: {data.projectileName})");
                return;
            }

            float damage = ComputeDamage(data, config, hit.TravelDist, isHeadshot);

            FireHitEvent(data, config, damage, isHeadshot,
                new Vector3(hit.HitX, hit.HitY, 0f),
                hit.TargetId, false);

            HandlePiercing(hit.ProjId, data, config);
        }

        // ── Hit processing (3D) ───────────────────────────────────────────────

        /// <summary>
        /// Process a 3D hit result from Rust check_hits_grid_3d.
        /// </summary>
        public void ProcessHit3D(in HitResult3D hit, bool isHeadshot)
        {
            if (!_projData.TryGetValue(hit.ProjId, out var data))
                return;

            var config = ProjectileConfigManager.GetProjectileConfig(data.projectileName);
            if (config == null) return;

            float damage = ComputeDamage(data, config, hit.TravelDist, isHeadshot);

            FireHitEvent(data, config, damage, isHeadshot,
                new Vector3(hit.HitX, hit.HitY, hit.HitZ),
                hit.TargetId, true);

            HandlePiercing(hit.ProjId, data, config);
        }

        // ── Compact dead notification ─────────────────────────────────────────

        /// <summary>
        /// Called by ServerProjectileAuthority during CompactDeadSlots for each
        /// projectile whose alive flag was cleared by Rust (lifetime expiry) or
        /// by ProcessHit (piercing exhausted).
        /// </summary>
        public void NotifyDead(uint projId)
        {
            if (_projData.TryGetValue(projId, out _))
            {
                Unregister(projId);
                OnProjectileDied?.Invoke(projId);
            }
        }

        // ── Guided / wave / circular: C# writes accel fields ─────────────────

        /// <summary>
        /// Update the homing direction for a guided projectile.
        /// Called from a TickDispatcher subscriber (Tick_0_1 is sufficient).
        /// Writes ax/ay in the NativeProjectile at the given buffer index.
        /// ServerProjectileAuthority exposes SetAccel2D() which calls this.
        /// </summary>
        public void SetHomingDirection2D(
            ref NativeProjectile proj, Vector2 worldDir)
        {
            Vector2 n = worldDir.normalized;
            proj.ax = n.x;
            proj.ay = n.y;
        }

        /// <summary>
        /// Update the homing direction for a 3D guided projectile.
        /// </summary>
        public void SetHomingDirection3D(
            ref NativeProjectile3D proj, Vector3 worldDir)
        {
            Vector3 n = worldDir.normalized;
            proj.ax = n.x;
            proj.ay = n.y;
            proj.az = n.z;
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private static float ComputeDamage(
            ServerProjectileData data,
            ProjectileConfigSO   config,
            float                travelDist,
            bool                 isHeadshot)
        {
            // Normalise distance (0 = point-blank, 1 = max range)
            float normDist = config.MaxRange > 0f
                ? Mathf.Clamp01(travelDist / config.MaxRange)
                : 0f;

            // Evaluate damage curve (skips evaluation internally when curve is flat)
            float damage = config.EvaluateDamage(normDist);

            // Apply headshot multiplier
            if (isHeadshot)
                damage *= config.HeadshotMultiplier;

            // Apply pre-rolled crit (rolled at spawn in ServerProjectileData)
            if (data.isCrit)
                damage *= config.CritMultiplier;

            // Apply damage multiplier from power-ups (stored in ServerProjectileData)
            damage *= data.damageMultiplier;

            return damage;
        }

        private void FireHitEvent(
            ServerProjectileData data,
            ProjectileConfigSO   config,
            float                damage,
            bool                 isHeadshot,
            Vector3              hitPos,
            uint                 targetId,
            bool                 is3D)
        {
            var payload = new ProjectileHitPayload
            {
                ProjId                  = data.projectileId_u32,
                ConfigId                = data.configId,
                Is3D                    = is3D,
                TargetId                = targetId,
                Damage                  = damage,
                IsHeadshot              = isHeadshot,
                IsCrit                  = data.isCrit,
                HitPosition             = hitPos,
                OwnerMidId              = data.ownerClientId,
                FiredByNetworkObjectId  = data.firedByNetworkObjectId,
                IsBotOwner              = data.isBotOwner,
                WeaponLevel             = data.weaponLevel,
                GameData                = data
            };

            OnProjectileHit?.Invoke(payload);
        }

        private void HandlePiercing(
            uint                 projId,
            ServerProjectileData data,
            ProjectileConfigSO   config)
        {
            if (config.PiercingType == ProjectilePiercingType.None)
            {
                // Non-piercer: mark for death — ServerProjectileAuthority sets alive=0
                // in the NativeProjectile during its hit-processing pass
                data.hasHit = true;
                _projData[projId] = data;
                return;
            }

            // Piercer: decrement remaining collisions
            data.collisionsRemaining--;

            if (data.collisionsRemaining <= 0)
            {
                data.hasHit = true;
            }

            _projData[projId] = data;
        }
    }
}
