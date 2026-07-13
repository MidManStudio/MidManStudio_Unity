using System;
using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Projectiles.Core;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Data;

namespace MidManStudio.Projectiles.Adapters
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Damage payload
    // ─────────────────────────────────────────────────────────────────────────

    public struct ProjectileHitPayload
    {
        public uint   ProjId;
        public ushort ConfigId;
        public bool   Is3D;
        public uint   TargetId;
        public float  Damage;
        public bool   IsHeadshot;
        public bool   IsCrit;
        public Vector3 HitPosition;
        public ulong  OwnerMidId;
        public ulong  FiredByNetworkObjectId;
        public bool   IsBotOwner;
        public byte   WeaponLevel;
        public ServerProjectileData GameData;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Adapter
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class RustSimAdapter
    {
        private readonly Dictionary<uint, ServerProjectileData> _projData
            = new Dictionary<uint, ServerProjectileData>(512);

        public event Action<ProjectileHitPayload> OnProjectileHit;
        public event Action<uint>                 OnProjectileDied;

        // ── Registration ──────────────────────────────────────────────────────

        public void Register(ServerProjectileData data)
            => _projData[data.projectileId_u32] = data;

        public void Unregister(uint projId)
            => _projData.Remove(projId);

        public bool IsRegistered(uint projId)
            => _projData.ContainsKey(projId);

        /// <summary>Look up a projectile's server-side data (owner, config, etc.) by id.</summary>
        public bool TryGetData(uint projId, out ServerProjectileData data)
            => _projData.TryGetValue(projId, out data);

        // ── Hit processing (2D) ───────────────────────────────────────────────

        public void ProcessHit(in HitResult hit, bool isHeadshot)
        {
            if (!_projData.TryGetValue(hit.ProjId, out var data)) return;

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
                new Vector3(hit.HitX, hit.HitY, 0f), hit.TargetId, false);

            HandlePiercing(hit.ProjId, data, config);
        }

        // ── Hit processing (3D) ───────────────────────────────────────────────

        public void ProcessHit3D(in HitResult3D hit, bool isHeadshot)
        {
            if (!_projData.TryGetValue(hit.ProjId, out var data)) return;

            var config = ProjectileRegistry.Instance.Get(data.configId);
            if (config == null) return;

            float damage = ComputeDamage(data, config, hit.TravelDist, isHeadshot);
            FireHitEvent(data, damage, isHeadshot,
                new Vector3(hit.HitX, hit.HitY, hit.HitZ), hit.TargetId, true);

            HandlePiercing(hit.ProjId, data, config);
        }

        // ── Compact dead notification (lifetime expiry path) ──────────────────

        /// <summary>
        /// Called by ServerProjectileAuthority during CompactDeadSlots for each
        /// projectile whose Alive flag was cleared by Rust (lifetime expired) or
        /// by Collision2D after HandlePiercing unregistered it.
        /// Guards against double-fire: HandlePiercing already called Unregister
        /// and fired OnProjectileDied for hit-killed projectiles.
        /// </summary>
        public void NotifyDead(uint projId)
        {
            // Only act if still registered — if HandlePiercing already cleaned up,
            // _projData no longer contains this key and we do nothing here.
            if (!_projData.ContainsKey(projId)) return;

            Unregister(projId);
            OnProjectileDied?.Invoke(projId);
        }

        // ── Guided / wave / circular: C# writes accel fields ─────────────────

        public void SetHomingDirection2D(ref NativeProjectile proj, Vector2 worldDir)
        {
            Vector2 n = worldDir.normalized;
            proj.Ax = n.x;
            proj.Ay = n.y;
        }

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
                ? Mathf.Clamp01(travelDist / config.MaxRange) : 0f;

            float damage = config.EvaluateDamage(normDist);
            if (isHeadshot)  damage *= config.HeadshotMultiplier;
            if (data.isCrit) damage *= config.CritMultiplier;
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

        /// <summary>
        /// Determines whether the projectile should die and handles cleanup.
        ///
        ///  For PiercingType.None (most common case), we immediately call
        /// Unregister() so that ServerProjectileAuthority.Collision2D's post-hit
        /// check (!Adapter.IsRegistered) returns true and sets Alive=0 in the
        /// Rust buffer. Without this, Alive was never cleared and projectiles
        /// lived forever regardless of piercing setting or lifetime.
        ///
        /// For piercing types we decrement collisionsRemaining and only unregister
        /// (and kill) when the pierce count reaches zero.
        /// </summary>
        private void HandlePiercing(
            uint                 projId,
            ServerProjectileData data,
            ProjectileConfigSO   config)
        {
            switch (config.PiercingType)
            {
                case ProjectilePiercingType.None:
                    // Non-piercing: die on first hit.
                    // Unregister now so Collision2D sets Alive=0 immediately.
                    data.hasHit              = true;
                    data.collisionsRemaining = 0;
                    Unregister(projId);
                    OnProjectileDied?.Invoke(projId);
                    break;

                case ProjectilePiercingType.Piecer:
                case ProjectilePiercingType.Random:
                    data.collisionsRemaining--;
                    if (data.collisionsRemaining <= 0)
                    {
                        data.hasHit = true;
                        Unregister(projId);
                        OnProjectileDied?.Invoke(projId);
                    }
                    else
                    {
                        // Still alive — update remaining count
                        _projData[projId] = data;
                    }
                    break;

                default:
                    // Unknown type — treat as non-piercing
                    data.hasHit = true;
                    Unregister(projId);
                    OnProjectileDied?.Invoke(projId);
                    break;
            }
        }
    }
}
