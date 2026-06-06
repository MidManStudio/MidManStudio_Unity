// packages/com.midmanstudio.projectilesystem/Runtime/Config/Generator/ProjectileConfigProviderSO.cs
// One of these lives in every package/game-assembly that registers ProjectileConfigSO
// assets as named enum members in the generated ProjectileConfigType enum.
//
// SETUP:
//   Create via: right-click > MidManStudio > Projectile System > Config Type Provider
//   Set packageId (unique), priority, then add entries pairing enum names to config SOs.
//   Run: MidManStudio > Projectile System > Config Type Generator

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Projectiles.Config
{
    [CreateAssetMenu(
        fileName = "ProjectileConfigProvider",
        menuName  = "MidManStudio/Projectile System/Config Type Provider",
        order     = 20)]
    public class ProjectileConfigProviderSO : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Reverse-domain package ID. Must be unique across all providers.\n" +
                 "e.g. com.midmanstudio.mygame")]
        public string packageId = "com.mygame";

        [Tooltip("Human-readable name shown in the generator window.")]
        public string displayName = "My Game";

        [Header("Block Priority")]
        [Tooltip("Lower value = earlier block in the generated enum.\n" +
                 "0   = system/utilities (reserved)\n" +
                 "100 = recommended starting priority for user game code.")]
        public int priority = 100;

        [Header("Entries")]
        [MID_NamedList]
        public List<ProjectileConfigEntry> entries = new();

        public int EntryCount => entries?.Count ?? 0;
    }
}
