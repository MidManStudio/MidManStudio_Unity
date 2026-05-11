// RustSimAdapter.cs
// Translates raw HitResult / HitResult3D from Rust into game damage events.
//
// Owns:
//   Dictionary<uint, ServerProjectileData> — maps ProjId to gameplay data.
//
// On hit (called by ServerProjectileAuthority every FixedUpdate):
//   1. Look up ServerProjectileData by ProjId.
//   2. Compute final damage: curve(normDist) × headshot/crit multipliers.
//   3. Check piercing — kill or decrement.
//   4. Fire OnProjectileHit event.
//
// NOT responsible for:
//   - Network RPCs (MID_ProjectileNetworkBridge owns those)
//   - Visual effects (ProjectileImpactHandler owns those)
//   - Applying damage to health components (game layer owns that)

using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Data;

namespace MidManStudio.Projectiles.Adapters
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Damage payload — everything the game damage system needs
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when a projectile hits a registered target.
    /// Subscribe from the game's damage system and from MID_ProjectileNetworkBridge.
    /// </summary>
    public struct ProjectileHitPayload
    {
        // ── Projectile identity ───────────────────────────────────────────────
        public uint   ProjId;
        public ushort ConfigId;
        public bool   Is3D;

        // ── Target identity ───────────────────────────────────────────────────
        public uint TargetId;

        // ── Damage ────────────────────────────────────────────────────────────
        public float Damage;
        public bool  IsHeadshot;
        public bool  IsCrit;

        // ── Position ──────────────────────────────────────────────────────────
        public Vector3 HitPosition;

        // ── Owner ─────────────────────────────────────────────────────────────
        public ulong OwnerMidId;
        public ulong FiredByNetworkObjectId;
        public bool  IsBotOwner;
        public byte  WeaponLevel;

        // ── Passthrough — game-specific data from ServerProjectileData ─────────
        /// Reference to the full ServerProjectileData.
        /// Game code reads KillTypeRaw, DamageTypeRaw, WeaponTypeRaw etc. from here.
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

        private readonly Dictionary<uint, ServerProjectileData> _projData
            = new Dictionary<uint, ServerProjectileData>(512);

        // ── Events ────────────────────────────────────────────────────────────

        /// Fired for each hit. Subscribe from MID_ProjectileNetworkBridge and
        /// the game's damage system.
        public event Action<ProjectileHitPayload> OnProjectileHit;

        /// Fired when a projectile dies (lifetime, piercing exhausted, etc.).
        public event Action<uint> OnProjectileDied;

        // ── Registration ──────────────────────────────────────────────────────

        public void Register(ServerProjectileData data)
            => _projData[data.projectileId_u32] = data;

        public void Unregister(uint projId)
            => _projData.Remove(projId);

        public bool IsRegistered(uint projId)
            => _projData.ContainsKey(projId);

        // ── Hit processing (2D) ───────────────────────────────────────────────

        /// <summary>
        /// Process a 2D hit result from check_hits_grid.
        /// Called by ServerProjectileAuthority for each hit in the hits array.
        /// isHeadshot is determined by the game layer (capsule zone check) before calling.
        /// </summary>
        public void ProcessHit(in HitResult hit, bool isHeadshot)
        {
            if (!_projData.TryGetValue(hit.ProjId, out var data))
                return;

            var config = ProjectileRegistry.Instance.Get(data.configId);
            if (config == null)
            {
                Debug.LogError(
                    $"[RustSimAdapter] No config for projId={hit.ProjId} " +
                    $"configId={data.configId}");
                return;
            }

            float damage = ComputeDamage(data, config, hit.TravelDist, isHeadshot);

            FireHitEvent(data, damage, isHeadshot,
                new Vector3(hit.HitX, hit.HitY, 0f),
                hit.TargetId, false);

            HandlePiercing(hit.ProjId, data, config);
        }

        // ── Hit processing (3D) ───────────────────────────────────────────────

        /// <summary>Process a 3D hit result from check_hits_grid_3d.</summary>
        public void ProcessHit3D(in HitResult3D hit, bool isHeadshot)
        {
            if (!_projData.TryGetValue(hit.ProjId, out var data))
                return;

            var config = ProjectileRegistry.Instance.Get(data.configId);
            if (config == null) return;

            float damage = ComputeDamage(data, config, hit.TravelDist, isHeadshot);

            FireHitEvent(data, damage, isHeadshot,
                new Vector3(hit.HitX, hit.HitY, hit.HitZ),
                hit.TargetId, true);

            HandlePiercing(hit.ProjId, data, config);
        }

        // ── Compact dead notification ─────────────────────────────────────────

        /// <summary>
        /// Called by ServerProjectileAuthority during CompactDeadSlots for each
        /// projectile whose Alive flag was cleared by Rust or by HandlePiercing.
        /// </summary>
        public void NotifyDead(uint projId)
        {
            if (_projData.ContainsKey(projId))
            {
                Unregister(projId);
                OnProjectileDied?.Invoke(projId);
            }
        }

        // ── Guided / wave / circular: C# writes accel fields ─────────────────

        /// <summary>
        /// Update the homing direction for a guided 2D projectile.
        /// Called from a TickDispatcher subscriber. Writes Ax/Ay in the buffer.
        /// </summary>
        public void SetHomingDirection2D(ref NativeProjectile proj, Vector2 worldDir)
        {
            Vector2 n = worldDir.normalized;
            proj.Ax = n.x;
            proj.Ay = n.y;
        }

        /// <summary>
        /// Update the homing direction for a guided 3D projectile.
        /// </summary>
        public void SetHomingDirection3D(ref NativeProjectile3D proj, Vector3 worldDir)
        {
            Vector3 n = worldDir.normalized;
            proj.Ax = n.x;
            proj.Ay = n.y;
            proj.Az = n.z;
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private static float ComputeDamage(
            ServerProjectileData data,
            ProjectileConfigSO   config,
            float                travelDist,
            bool                 isHeadshot)
        {
            float normDist = config.MaxRange > 0f
                ? Mathf.Clamp01(travelDist / config.MaxRange)
                : 0f;

            float damage = config.EvaluateDamage(normDist);

            if (isHeadshot)   damage *= config.HeadshotMultiplier;
            if (data.isCrit)  damage *= config.CritMultiplier;

            damage *= data.damageMultiplier;
            return damage;
        }

        private void FireHitEvent(
            ServerProjectileData data,
            float                damage,
            bool                 isHeadshot,
            Vector3              hitPos,
            uint                 targetId,
            bool                 is3D)
        {
            var payload = new ProjectileHitPayload
            {
                ProjId                 = data.projectileId_u32,
                ConfigId               = data.configId,
                Is3D                   = is3D,
                TargetId               = targetId,
                Damage                 = damage,
                IsHeadshot             = isHeadshot,
                IsCrit                 = data.isCrit,
                HitPosition            = hitPos,
                OwnerMidId             = data.ownerClientId,
                FiredByNetworkObjectId = data.firedByNetworkObjectId,
                IsBotOwner             = data.isBotOwner,
                WeaponLevel            = data.weaponLevel,
                GameData               = data
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
                data.hasHit = true;
                _projData[projId] = data;
                return;
            }

            data.collisionsRemaining--;
            if (data.collisionsRemaining <= 0)
                data.hasHit = true;

            _projData[projId] = data;
        }
    }
}
