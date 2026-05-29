// packages/com.midmanstudio.projectilesystem/Runtime/Managers/PhysicsProjectile.cs
//
// DEPRECATED — replaced by the following split architecture:
//   PhysicsProjectileBase.cs  — abstract shared base
//   PhysicsProjectile2D.cs    — concrete 2D (Rigidbody2D + CircleCollider2D)
//   PhysicsProjectile3D.cs    — concrete 3D (Rigidbody  + SphereCollider)
//
// MIGRATION:
//   1. On your 2D physics projectile prefab:
//      - Remove this component
//      - Add PhysicsProjectile2D
//      - Ensure Rigidbody2D and CircleCollider2D are present
//   2. On your 3D physics projectile prefab:
//      - Remove this component
//      - Add PhysicsProjectile3D
//      - Ensure Rigidbody and SphereCollider are present
//   3. In NetworkedDimensionPlayer inspector:
//      - _physicsPoolType2D → BaseProjectileBlueprint_2D pool entry
//      - _physicsPoolType3D → BaseProjectileBlueprint_3D pool entry
//   4. Delete this file once migration is complete.
//
// This file is intentionally empty of logic to produce a clear compile error
// if any code still references PhysicsProjectile directly, forcing migration.

namespace MidManStudio.Projectiles.Managers
{
#pragma warning disable CS0618
    [System.Obsolete(
        "PhysicsProjectile is replaced by PhysicsProjectile2D and PhysicsProjectile3D. " +
        "See migration notes in this file. Delete this file after migrating prefabs.",
        error: false)]
    public sealed class PhysicsProjectile : PhysicsProjectileBase
    {
        protected override bool Is2D => _use2DFallback;

        [UnityEngine.SerializeField]
        [UnityEngine.Tooltip("DEPRECATED — use PhysicsProjectile2D or PhysicsProjectile3D instead.")]
        private bool _use2DFallback = false;

        protected override void OnPhysicsSetup() { }

        protected override UnityEngine.Vector3 OnLaunch(float bulletVelocity)
            => transform.forward;

        protected override void StopPhysics() { }
    }
#pragma warning restore CS0618
}
