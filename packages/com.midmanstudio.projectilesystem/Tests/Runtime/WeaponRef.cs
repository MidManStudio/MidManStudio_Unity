// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "WeaponRef.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Core.AutoReference;
using MidManStudio.Core.Logging;

namespace TestGame
{
    /// <summary>
    /// Marks a weapon model's muzzle. WeaponController adds this to every
    /// equipped model that lacks one, then fills <see cref="ShotPoint"/> with
    /// the utilities package's MID_AutoReferenceResolver (name match on the
    /// model's children), and points the player's 3D shot point at it so fire
    /// origin follows the weapon actually held. A ShotPoint assigned on the
    /// prefab is always kept as is. The resolver matches against the field name,
    /// so a child called Muzzle scores 0; a short list of common muzzle names is
    /// tried as a second step when the resolver finds nothing good enough.
    /// </summary>
    [MID_AutoRefable]
    public class WeaponRef : MonoBehaviour
    {
        [Tooltip("Child transform at the barrel tip. Projectiles spawn here. Leave empty to " +
                 "have it found by name (a child called ShotPoint, Muzzle, FirePoint...).")]
        public Transform ShotPoint;

        // Lowercase, with spaces, underscores and dashes removed before comparing.
        private static readonly string[] MuzzleNameHints =
            { "shotpoint", "shootpoint", "muzzle", "firepoint", "barrelend", "barreltip", "bulletspawn" };

        /// <summary>True when a child's name contains one of the common muzzle names.</summary>
        public static bool NameLooksLikeMuzzle(string childName)
        {
            if (string.IsNullOrEmpty(childName)) return false;
            string n = childName.Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
            for (int i = 0; i < MuzzleNameHints.Length; i++)
                if (n.Contains(MuzzleNameHints[i])) return true;
            return false;
        }

        private static Transform FindMuzzleByName(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != root && NameLooksLikeMuzzle(t.name)) return t;
            return null;
        }

        /// <summary>
        /// Returns the WeaponRef on a freshly instantiated model, adding one if
        /// the prefab has none, and resolving ShotPoint when it is still empty.
        /// ShotPoint stays null when no child matches by resolver score (at least
        /// <paramref name="minMatchScore"/>) or by a common muzzle name, or only
        /// the model root matched, so the caller falls back to the player's
        /// default shot point instead of firing from a wrong transform.
        /// </summary>
        public static WeaponRef EnsureOn(GameObject model, float minMatchScore, MID_LogLevel logLevel)
        {
            if (model == null) return null;

            var weaponRef = model.GetComponent<WeaponRef>();
            if (weaponRef == null) weaponRef = model.AddComponent<WeaponRef>();
            if (weaponRef.ShotPoint != null) return weaponRef;

            var options = new MID_AutoRefOptions
            {
                includeChildren         = true,
                includeInactiveChildren = true,
                overwriteExisting       = false,
                logUnresolved           = false,
                logAmbiguousResolved    = false
            };
            var results = MID_AutoReferenceResolver.Resolve(model, options);

            bool accepted = false;
            foreach (var r in results)
            {
                if (r.ScriptTypeName != nameof(WeaponRef) || r.FieldName != nameof(ShotPoint)) continue;
                accepted = r.Outcome != MID_AutoRefOutcome.NoCandidates && r.MatchScore >= minMatchScore;
                break;
            }

            if (!accepted || weaponRef.ShotPoint == model.transform)
            {
                weaponRef.ShotPoint = FindMuzzleByName(model.transform);
                if (weaponRef.ShotPoint == null)
                {
                    MID_Logger.LogWarning(logLevel,
                        $"No child of '{model.name}' looks like a muzzle. Using the player's default " +
                        "shot point. Add a child named ShotPoint (or Muzzle) at the barrel tip, or " +
                        "assign WeaponRef.ShotPoint on the prefab.",
                        nameof(WeaponRef));
                }
            }
            return weaponRef;
        }
    }
}
