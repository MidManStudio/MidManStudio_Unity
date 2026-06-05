// packages/com.midmanstudio.projectilesystem/Runtime/Config/ProjectileConfigMappingSO.cs
// Runtime-facing SO that maps ProjectileConfigType int values to
// ProjectileConfigSO assets. _configs[i] corresponds to enum value i.
//
// AUTO-GENERATED / UPDATED by ProjectileConfigGenerator — do not edit manually.
// Assign to ProjectileConfigManager._mapping in the scene.

using System;
using UnityEngine;

namespace MidManStudio.Projectiles.Config
{
    [CreateAssetMenu(
        fileName = "ProjectileConfigMapping",
        menuName  = "MidManStudio/Projectile System/Config Type Mapping",
        order     = 22)]
    public class ProjectileConfigMappingSO : ScriptableObject
    {
        [Tooltip("AUTO-GENERATED — array index == ProjectileConfigType int value.\n" +
                 "Null slots represent padding gaps between provider blocks.\n" +
                 "Do not edit manually; use the Config Type Generator window.")]
        [SerializeField]
        private ProjectileConfigSO[] _configs = Array.Empty<ProjectileConfigSO>();

        /// <summary>
        /// Ordered array where <c>Configs[i]</c> maps to ProjectileConfigType int value i.
        /// Null entries are valid padding gaps between provider blocks.
        /// </summary>
        public ProjectileConfigSO[] Configs => _configs;

        /// <summary>Called exclusively by the editor generator to rebuild the array.</summary>
        public void SetConfigs(ProjectileConfigSO[] configs)
            => _configs = configs ?? Array.Empty<ProjectileConfigSO>();
    }
}
