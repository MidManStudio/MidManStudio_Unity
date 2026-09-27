// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "WeaponRef.cs"
// ============================================================================
using UnityEngine;

namespace TestGame
{
    /// <summary>
    /// Marks a weapon model prefab's muzzle. WeaponController looks this up on
    /// the instantiated model (not the prefab asset) after equipping a weapon
    /// and points the player's 3D shot point at <see cref="ShotPoint"/>, so
    /// fire origin follows whichever weapon is actually held instead of a
    /// fixed offset from the head. Optional — a weapon model with no WeaponRef
    /// falls back to the player's own default shot point.
    /// </summary>
    public class WeaponRef : MonoBehaviour
    {
        [Tooltip("Child transform at the barrel tip. Projectiles spawn here.")]
        public Transform ShotPoint;
    }
}
