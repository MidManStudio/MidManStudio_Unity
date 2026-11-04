// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "ProjectileManager.cs"
// ============================================================================
// Marker MonoBehaviour.
// ProjectileRenderer2D and TrailObjectPool use
// [RequireComponent(typeof(ProjectileManager))], so Unity adds this component
// next to them. ProjectileRenderer3D does not require it.
// No logic lives here.

using UnityEngine;

namespace MidManStudio.Projectiles.Core
{
    /// <summary>
    /// Marker component that signals a GameObject participates in the projectile
    /// sim pipeline. Required by ProjectileRenderer2D and TrailObjectPool.
    /// </summary>
    [AddComponentMenu("MidMan/Projectile System/Projectile Manager")]
    public sealed class ProjectileManager : MonoBehaviour
    {
        // Intentionally empty — this is a marker/tag component only.
        // LocalProjectileManager and ServerProjectileAuthority hold
        // serialized references to the renderer and pool components directly.
    }
}
