// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/fx.md, section "FXEntry.cs"
// ============================================================================
using System;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Core.FX
{
    /// <summary>
    /// One entry in the <c>GlobalFXManager</c> effect list. Binds an (<see cref="EffectCategory"/>,
    /// <see cref="EffectType"/>) pair to an in-scene <see cref="ParticleSystem"/> or to flipbook frames.
    /// </summary>
    /// <remarks>
    /// The category is the broad group (Impact, MuzzleFlash, ShellEjection, Explosion). The type is the
    /// variant inside it (MetalSurface, BrassShell, SmallExplosion). Both enums are generated: to extend
    /// them create provider assets and run the Effect Type Generator.
    /// </remarks>
    [Serializable]
    public class FXEntry : IArrayElementTitle
    {
        [Tooltip("Broad category this effect belongs to.\n" +
                 "Determines which TriggerImpact / TriggerMuzzleFlash etc. call can use it.")]
        public EffectCategory category;

        [Tooltip("Specific variant identifier within the category.\n" +
                 "Callers pass this to select e.g. MetalSurface vs ConcreteSurface.")]
        public EffectType effectType;

        [Tooltip("Label shown in the inspector list. Leave blank to use category/effectType.")]
        public string displayName;

        [Tooltip("The ParticleSystem already placed in the scene (or prefab child).\n" +
                 "Must have Simulation Space = World for EmitParams positioning to work.\n" +
                 "Leave null if this entry uses flipbookFrames instead.")]
        public ParticleSystem particleSystem;

        [Tooltip("Number of particles emitted per trigger when caller passes -1.")]
        [Min(1)]
        public int defaultParticleCount = 6;

        [Tooltip("Sprite-sheet flipbook frames — alternative to particleSystem for hand-drawn " +
                 "effects (e.g. a muzzle flash flipbook) that a ParticleSystem can't reproduce. " +
                 "Requires PoolableObjectType.FlipbookEffect registered in LocalObjectPool. " +
                 "If both this and particleSystem are set, flipbookFrames takes priority.")]
        public Sprite[] flipbookFrames;

        [Tooltip("Playback speed for flipbookFrames, in frames per second.")]
        [Min(1f)]
        public float flipbookFps = 24f;

        /// <summary>True if this entry should play as a flipbook rather than emit particles.</summary>
        public bool IsFlipbook => flipbookFrames != null && flipbookFrames.Length > 0;

        // IArrayElementTitle — shown as the list element label
        /// <summary>List element label: the display name, else a label built from the category, type or particle system.</summary>
        public string Name =>
            !string.IsNullOrWhiteSpace(displayName) ? displayName :
            IsFlipbook ? $"{category}/{effectType} (flipbook)" :
            particleSystem != null ? $"{category}/{particleSystem.name}" :
                                     $"{category}/{effectType}";
    }
}
