// ServerProjectileData.cs
// Server-side gameplay data for a single projectile.
// Lives in RustSimAdapter._projData, keyed by projectileId_u32.
// Contains everything the damage system needs — nothing Rust needs.
// Position is owned by Rust (NativeProjectile.X/Y) — never duplicated here.
//
// GAME-SPECIFIC ATTRIBUTION:
//   KillTypeRaw, DamageTypeRaw, WeaponTypeRaw are stored as int.
//   Cast them to your game's enums in your damage/kill handler:
//     var killType = (MyGame.KillType)data.KillTypeRaw;
//
// EXTENSION PATTERN:
//   Subclass ServerProjectileData in your game assembly to add strongly-typed
//   game fields without modifying this package file.

using UnityEngine;
using MidManStudio.Projectiles.Config;
using MidManStudio.Projectiles.Core;

namespace MidManStudio.Projectiles.Data
{
    /// <summary>
    /// Server-side gameplay data for a single projectile.
    /// Keyed by projectileId_u32 in RustSimAdapter.
    /// </summary>
    public class ServerProjectileData
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Identity — links this to the Rust NativeProjectile
        // ─────────────────────────────────────────────────────────────────────

        /// Matches NativeProjectile.ProjId. Primary key in RustSimAdapter dictionary.
        public uint   projectileId_u32;

        /// Matches NativeProjectile.ConfigId. Used for config lookups on hit.
        public ushort configId;

        /// True if this projectile uses the 3D sim buffer (NativeProjectile3D).
        public bool   is3D;

        // ─────────────────────────────────────────────────────────────────────
        //  Owner identity
        // ─────────────────────────────────────────────────────────────────────

        /// MID ID of the firing entity (player or bot ID 100-999).
        public ulong ownerClientId;

        /// NetworkObject ID of the weapon/character that fired.
        public ulong firedByNetworkObjectId;

        /// True if the owner is a bot.
        public bool isBotOwner;

        /// Weapon level — affects kill effect probability and damage tier.
        public byte weaponLevel;

        // ─────────────────────────────────────────────────────────────────────
        //  Damage (C# only — Rust never reads these)
        // ─────────────────────────────────────────────────────────────────────

        /// Multiplier from power-ups or abilities.
        public float damageMultiplier;

        /// Pre-rolled crit flag. Set once at spawn by ServerProjectileAuthority.
        public bool isCrit;

        /// Cached crit chance from config (avoids config lookup per spawn).
        public float critChance;

        // ─────────────────────────────────────────────────────────────────────
        //  Collision tracking
        // ─────────────────────────────────────────────────────────────────────

        /// Remaining pierce-through collisions. Decremented by RustSimAdapter.HandlePiercing.
        public byte collisionsRemaining;

        /// Set true by RustSimAdapter when this projectile should die.
        public bool hasHit;

        // ─────────────────────────────────────────────────────────────────────
        //  Spawn position (stored for range falloff; not updated — Rust owns pos)
        // ─────────────────────────────────────────────────────────────────────

        public Vector2 spawnPosition2D;
        public Vector3 spawnPosition3D;

        // ─────────────────────────────────────────────────────────────────────
        //  Game-specific attribution — stored as raw int.
        //  Cast to your game's enums in your damage/kill handler.
        //  Example: var killType = (MyGame.KillType)data.KillTypeRaw;
        // ─────────────────────────────────────────────────────────────────────

        /// Game kill-type ID. Cast to your game's KillType enum.
        public int KillTypeRaw;

        /// Game damage-type ID. Cast to your game's DamageType enum.
        public int DamageTypeRaw;

        /// Game weapon-type ID. Cast to your game's WeaponType enum.
        public int WeaponTypeRaw;

        // ─────────────────────────────────────────────────────────────────────
        //  Construction
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Create ServerProjectileData from a spawn request.
        /// Call once per weapon fire to create a template, then CloneForSpawn()
        /// per projectile in the batch.
        /// </summary>
        /// <param name="ownerMidId">MID ID of the firing entity.</param>
        /// <param name="firedById">NetworkObject ID of the weapon/character.</param>
        /// <param name="isBot">True if owner is a bot.</param>
        /// <param name="level">Weapon level.</param>
        /// <param name="spawnPos2D">World-space spawn origin (2D).</param>
        /// <param name="damageMultiplierIn">Damage multiplier from power-ups.</param>
        /// <param name="config">ProjectileConfigSO for piercing/crit initialisation.</param>
        /// <param name="killTypeRaw">Game kill-type as raw int (default 0).</param>
        /// <param name="damageTypeRaw">Game damage-type as raw int (default 0).</param>
        /// <param name="weaponTypeRaw">Game weapon-type as raw int (default 0).</param>
        public ServerProjectileData(
            ulong              ownerMidId,
            ulong              firedById,
            bool               isBot,
            byte               level,
            Vector2            spawnPos2D,
            float              damageMultiplierIn,
            ProjectileConfigSO config,
            int                killTypeRaw   = 0,
            int                damageTypeRaw = 0,
            int                weaponTypeRaw = 0)
        {
            ownerClientId          = ownerMidId;
            firedByNetworkObjectId = firedById;
            isBotOwner             = isBot;
            weaponLevel            = level;
            spawnPosition2D        = spawnPos2D;
            damageMultiplier       = damageMultiplierIn;
            critChance             = config != null ? config.CritChance : 0f;
            KillTypeRaw            = killTypeRaw;
            DamageTypeRaw          = damageTypeRaw;
            WeaponTypeRaw          = weaponTypeRaw;

            if (config != null)
            {
                switch (config.PiercingType)
                {
                    case ProjectilePiercingType.None:
                        collisionsRemaining = 1;
                        break;
                    case ProjectilePiercingType.Piecer:
                        collisionsRemaining = config.MaxCollisions;
                        break;
                    case ProjectilePiercingType.Random:
                        collisionsRemaining = (byte)UnityEngine.Random.Range(
                            1, config.MaxCollisions + 1);
                        break;
                    default:
                        collisionsRemaining = 1;
                        break;
                }
            }
            else
            {
                collisionsRemaining = 1;
            }

            isCrit           = false;
            projectileId_u32 = 0;
            configId         = 0;
            hasHit           = false;
            is3D             = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Clone for batch spawn
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Create a per-projectile clone from this template.
        /// Called by ServerProjectileAuthority for each projectile in the batch.
        /// isCrit is NOT rolled here — the authority rolls it after CloneForSpawn.
        /// </summary>
        public ServerProjectileData CloneForSpawn(uint projId, ushort cfgId)
        {
            var clone = (ServerProjectileData)MemberwiseClone();
            clone.projectileId_u32 = projId;
            clone.configId         = cfgId;
            clone.hasHit           = false;
            clone.isCrit           = false;
            return clone;
        }

        /// <summary>Overload that also sets is3D and 3D spawn position.</summary>
        public ServerProjectileData CloneForSpawn3D(
            uint projId, ushort cfgId, Vector3 spawnPos3D)
        {
            var clone = CloneForSpawn(projId, cfgId);
            clone.is3D            = true;
            clone.spawnPosition3D = spawnPos3D;
            return clone;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Expiry check
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// True if C# game logic has determined this projectile should die.
        /// Does NOT check Rust state — call after checking NativeProjectile.Alive.
        /// </summary>
        public bool IsDead() => hasHit && collisionsRemaining <= 0;
    }
}
