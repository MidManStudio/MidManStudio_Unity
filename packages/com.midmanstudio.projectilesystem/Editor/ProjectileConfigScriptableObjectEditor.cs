// Custom Inspector for ProjectileConfigScriptableObject. Draws the normal
// Inspector untouched, then adds an "Apply JSON" panel — paste a JSON object
// whose keys match this asset's serialized field names (base class fields
// included — MaxRange, MovementType, MinSpeed, etc., same as the ones
// declared directly on this class) and it fills in every primitive/enum/
// Vector2/Vector3 field it can, then reports exactly what it couldn't:
// unmatched keys (typos) separately from unsupported ones (asset references,
// AnimationCurve, Gradient — those need manual drag-and-drop same as
// ProjectileConfigEntry.configSO did in the enum-mapping importer).
//
// Example JSON — not exhaustive, just enough to show the shape:
//   {
//     "_movementType": "Straight",
//     "_minSpeed": 25, "_maxSpeed": 30,
//     "_lifetime": 3, "_maxRange": 50,
//     "_piercingType": "None",
//     "_projectileType": "basic", "_projectileClass": "basic",
//     "_capColliderSize": { "x": 0.2, "y": 0.08 },
//     "_minDamage": 8, "_maxDamage": 12,
//     "_minHeadShotDamage": 20, "_maxHeadShotDamage": 28
//   }

using MidManStudio.Core.EditorUtils;
using MidManStudio.Projectiles.Config;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Projectiles.EditorUtils
{
    /// <summary>
    /// EDITOR INHERITANCE FIX ("my game's ProjectileConfigSO subclass doesn't
    /// extend the whole JSON-import custom editor stuff from it — what's the
    /// proper way to do it, do I need my own custom editor too?"):
    /// [CustomEditor(typeof(ProjectileConfigSO))] previously had NO
    /// editorForChildClasses — meaning it ONLY applied to exact ProjectileConfigSO
    /// instances, never to any subclass, so a game-specific
    /// MyGameProjectileConfigSO : ProjectileConfigSO fell all the way back to
    /// Unity's plain default Inspector, losing the JSON-import panel entirely.
    /// (This is the mirror image of the earlier NetworkTransformEditor issue —
    /// there, editorForChildClasses:true was applying TOO broadly with nothing
    /// more specific to override it; here, the lack of it meant this editor
    /// wasn't applying broadly ENOUGH.)
    ///
    /// Adding editorForChildClasses:true here is the whole fix — it makes this
    /// editor (JSON panel, icon-cache fix, everything below) apply
    /// automatically to ProjectileConfigSO AND any subclass, including a
    /// game-specific one, with ZERO code needed in the game project. Any extra
    /// fields declared on a subclass already show up too, since
    /// DrawDefaultInspector() below draws every serialized field it finds,
    /// base class and subclass alike — no per-subclass editor required just to
    /// see new fields.
    ///
    /// A subclass only needs its OWN [CustomEditor] if it wants genuinely
    /// different UI beyond what DrawDefaultInspector + the JSON panel already
    /// gives it (e.g. a custom widget for a subclass-only field) — in that
    /// case Unity picks whichever editor is more derived, same rule as
    /// everywhere else. This class is sealed, so a subclass editor can't
    /// literally extend it in C# — but it doesn't need to: the JSON panel
    /// itself now lives in ProjectileConfigJsonPanel (a plain reusable class,
    /// not tied to this editor) — see that file's own doc comment for a
    /// usage example. This class is just its reference usage.
    /// </summary>
    [CustomEditor(typeof(ProjectileConfigSO), true)]
    [CanEditMultipleObjects]
    public sealed partial class ProjectileConfigScriptableObjectEditor : UnityEditor.Editor
    {
        // Extracted to ProjectileConfigJsonPanel.cs so a game-specific
        // subclass's own custom editor can embed the exact same panel — see
        // that file's own doc comment for the full explanation and a usage
        // example. This class is just ITS reference usage now.
        private readonly ProjectileConfigJsonPanel _jsonPanel = new();

        // POLISHED PATH EDITOR: same reusable-panel pattern as _jsonPanel
        // above — see ProjectileCustomPathPanel's own doc comment. Replaces
        // the plain default List<Vector2>/string fields the CustomCurve path
        // used to fall back to with formula validation, an example dropdown,
        // a draggable point list, and a live preview.
        private readonly ProjectileCustomPathPanel _pathPanel = new();

        // Path fields excluded from the default draw below — DrawPropertiesExcluding
        // skips exactly these, then _pathPanel.Draw renders its own rich UI for
        // them afterward. Every other field (base class and subclass alike)
        // still draws normally, same as DrawDefaultInspector() did before.
        private static readonly string[] PathFieldsHandledByPanel =
        {
            "_customPathShape", "_customPathSplineType", "_customPathPoints",
            "_customPathFormulaX", "_customPathFormulaY",
        };

        public override void OnInspectorGUI()
        {
            // ── Icon cache fix ───────────────────────────────────────────────
            // ProjectileConfigSO extends MID_BaseSO, whose per-instance
            // "custom icon" (_customIcon, a plain Texture2D field — this is
            // what shows up as the Project window thumbnail/"custom sprite"
            // for the asset) normally relies on MID_BaseSOEditor
            // ([CustomEditor(typeof(MID_BaseSO), editorForChildClasses: true)])
            // to invalidate MID_BaseSOProjectIconDrawer's per-GUID icon cache
            // the moment that field changes, so the thumbnail updates
            // immediately. Unity always resolves the MOST DERIVED
            // [CustomEditor] match for a given type — since THIS class targets
            // ProjectileConfigSO directly (an exact-type match beats
            // MID_BaseSOEditor's editorForChildClasses match), MID_BaseSOEditor
            // never runs for ProjectileConfigSO assets at all, and the plain
            // DrawDefaultInspector() call below has no idea _customIcon needs
            // special handling — it just writes the new value like any other
            // field.
            //
            // Net effect (the reported bug): assigning/changing a custom
            // icon/texture on a ProjectileConfigSO asset silently updates the
            // underlying data, but MID_BaseSOProjectIconDrawer's cache is
            // never invalidated, so the Project window keeps painting the
            // stale/old icon. MID_BaseSOProjectIconDrawer.ClearCacheOnScriptReload()
            // is [InitializeOnLoadMethod] — it fires after any domain reload,
            // and entering Play Mode triggers one by default, which is why the
            // correct icon only ever shows up once Play is hit. Configs that
            // never set a custom icon (falling back to the MID_BaseSO
            // default/GroupIconPath behaviour — "the MID_BaseSO thing") never
            // populate the cache with a stale value in the first place, so
            // they were never affected — matching the reported "regular
            // configs update instantly" half of this.
            //
            // Fix: reproduce MID_BaseSOEditor's own invalidate+repaint step
            // here too. EditorGUI.BeginChangeCheck/EndChangeCheck around
            // the properties draw picks up ANY field edit (not just the
            // icon) — cheap and harmless to invalidate a couple of extra
            // times on an unrelated field change, and far simpler/more
            // robust than trying to diff _customIcon specifically before vs.
            // after the call. Loops over `targets` (not just `target`) since
            // this editor supports multi-select ([CanEditMultipleObjects]).
            EditorGUI.BeginChangeCheck();
            DrawPropertiesExcluding(serializedObject, PathFieldsHandledByPanel);
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var t in targets)
                {
                    if (t == null) continue;
                    string path = AssetDatabase.GetAssetPath(t);
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (!string.IsNullOrEmpty(guid))
                        MID_BaseSOProjectIconDrawer.InvalidateCache(guid);
                }
                EditorApplication.RepaintProjectWindow();
            }

            // Single-object only — a draggable-point-list-and-preview UI
            // doesn't have a sensible meaning across a multi-selection the
            // way plain PropertyField edits do (those apply uniformly; "drag
            // this point" doesn't).
            if (targets.Length == 1 && target is ProjectileConfigSO cfg)
                _pathPanel.Draw(serializedObject, cfg);
            else if (targets.Length > 1)
                EditorGUILayout.HelpBox(
                    "Custom Movement Path editing is single-object only — select just one config to edit its path.",
                    MessageType.None);

            _jsonPanel.Draw(serializedObject);
        }
    }
}
