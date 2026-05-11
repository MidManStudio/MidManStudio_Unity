// ProjectileManager.cs
// Marker MonoBehaviour.
// ProjectileRenderer2D, ProjectileRenderer3D, and TrailObjectPool use
// [RequireComponent(typeof(ProjectileManager))] to enforce they live on a
// GameObject that participates in the projectile sim pipeline.
// No logic lives here — attach alongside the renderer/pool components.

using UnityEngine;

namespace MidManStudio.Projectiles.Core
{
    /// <summary>
    /// Marker component that signals a GameObject participates in the projectile
    /// sim pipeline. Required by ProjectileRenderer2D, ProjectileRenderer3D,
    /// and TrailObjectPool.
    /// </summary>
    [AddComponentMenu("MidMan/Projectile System/Projectile Manager")]
    public sealed class ProjectileManager : MonoBehaviour
    {
        // Intentionally empty — this is a marker/tag component only.
        // LocalProjectileManager and ServerProjectileAuthority hold
        // serialized references to the renderer and pool components directly.
    }
}
