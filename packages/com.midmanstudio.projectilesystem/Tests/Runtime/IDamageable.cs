namespace TestGame
{
    /// <summary>
    /// Implemented by anything that can take projectile/hit damage but isn't
    /// already covered by TestTarget/TestTarget2D's own registration map in
    /// TestSceneBootstrapper (PlayerHealth for PvP, NetworkTurretTarget for
    /// scene turrets). TestSceneBootstrapper.ApplyHit() resolves these
    /// generically via NetworkManager.SpawnManager.SpawnedObjects, so nothing
    /// needs bootstrapper-side registration bookkeeping the way the two
    /// TestTarget types do.
    /// </summary>
    public interface IDamageable
    {
        /// <summary>False while dead/respawning — ApplyHit skips damage entirely
        /// rather than stacking hits on something already down.</summary>
        bool IsAlive { get; }

        /// <summary>
        /// attackerClientId is ulong.MaxValue for an untraceable/environmental
        /// source (e.g. a turret). Implementations that care about PvP
        /// attribution (score, "don't damage yourself") read it; others can
        /// ignore it.
        /// </summary>
        void TakeDamage(float amount, ulong attackerClientId = ulong.MaxValue);
    }
}
