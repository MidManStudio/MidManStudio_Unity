// A single named entry in a ProjectileConfigProviderSO.
// Maps an enum member name to a ProjectileConfigSO asset.
// Used by the generator to produce stable ProjectileConfigType enum values
// and the ordered ProjectileConfigMappingSO for runtime registration.

using System;
using UnityEngine;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Projectiles.Config
{
    [Serializable]
    public class ProjectileConfigEntry : IArrayElementTitle
    {
        [Tooltip("Enum member name — PascalCase, no spaces.\n" +
                 "e.g. 'FireBall', 'IceShard', 'HomingMissile'.\n" +
                 "If blank, the configSO asset name is used (sanitised).")]
        public string enumName;

        [Tooltip("The ProjectileConfigSO asset this entry maps to.\n" +
                 "Null entries are skipped by the generator.")]
        public ProjectileConfigSO configSO;

        [Tooltip("Optional inline comment written next to the generated enum member.")]
        public string comment;

        [Tooltip("-1 = auto-assigned by generator.\n" +
                 ">=0 = pinned to this offset within the provider's block.\n" +
                 "Pin entries referenced by serialised inspector fields so their\n" +
                 "integer value never changes even when you add entries above them.")]
        public int explicitOffset = -1;

        public string Name
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(enumName)) return enumName;
                if (configSO != null)                     return configSO.name;
                return "Unnamed Entry";
            }
        }
    }
}
