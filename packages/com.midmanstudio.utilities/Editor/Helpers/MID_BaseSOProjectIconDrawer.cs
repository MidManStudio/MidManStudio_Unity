// THE FIX for "the selected icon doesn't reflect on the individual SO asset
// in the editor": MID_BaseSOEditor used to call
// EditorGUIUtility.SetIconForObject(target, icon) to push a per-instance icon
// onto each asset's Project window thumbnail. That API's own documentation
// says it's scoped to GameObject and MonoScript — "Sets a custom icon to
// associate with a GameObject or MonoScript" — it was never actually
// supported for arbitrary ScriptableObject asset instances, which is exactly
// why it worked unreliably (or not at all) per-asset.
//
// The correct, well-established approach for per-instance ScriptableObject
// icons is EditorApplication.projectWindowItemOnGUI: draw the icon directly
// over each item's row/tile yourself, every time the Project window repaints
// that item. This is how third-party "SO custom icon" tools do it (see e.g.
// the AssetIcons package, or the ScriptableObjectIcon attribute pattern
// several Unity devs have independently converged on).
//
// Cached by GUID since this callback fires on every repaint for every
// visible item — without caching, that's a LoadAssetAtPath + ResolveIcon per
// item per frame, which gets slow fast in a project with many assets.
//
// ── "draws over, doesn't replace" fix ──────────────────────────────────────
// projectWindowItemOnGUI fires AFTER Unity has already painted its own
// default icon into selectionRect — there's no hook to suppress that first
// pass, only to draw on top of it. GUI.DrawTexture alpha-blends, it doesn't
// clear what's underneath, so two things were leaking through the old icon:
//   1. ScaleMode.ScaleToFit letterboxes/pillarboxes any non-square texture,
//      leaving the untouched rect edges showing the default icon behind it.
//   2. Any transparent pixels in the custom icon (rounded corners, padding,
//      etc.) let the default icon show through those pixels too.
// Fix: paint an opaque rect matching the Project window's row/tile
// background FIRST — this erases the default icon — then draw the custom
// icon on top. Whatever's left uncovered now shows plain background instead
// of a ghost of the old icon: actual replacement, not an overlay.
//
// The background colors below are the standard dark/light Editor skin
// defaults (EditorGUIUtility.isProSkin), not a pixel-sampled read of the
// real row — Unity doesn't expose a way to read that back. They match
// unselected rows closely. Selected rows get a rough blue-tint approximation
// so there's no jarring solid-gray seam over a highlighted item, but it
// won't be a pixel-perfect match to Unity's internal highlight tone (which
// also shifts slightly when the Project window has focus vs. not, and that
// state isn't exposed either). Purely cosmetic — nudge the constants below
// if it ever looks visibly off in your Editor version/theme.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Core.EditorUtils
{
    [InitializeOnLoad]
    public static class MID_BaseSOProjectIconDrawer
    {
        private static readonly Dictionary<string, Texture2D> _iconCache = new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> _confirmedNotMidBaseSO = new HashSet<string>();

        // Approximate Editor default row/tile backgrounds (dark/light skin)
        // and a rough selection tint — see file header for why these are
        // approximations rather than sampled values.
        private static readonly Color _bgDark         = new Color(0.219f, 0.219f, 0.219f, 1f);
        private static readonly Color _bgLight         = new Color(0.760f, 0.760f, 0.760f, 1f);
        private static readonly Color _bgDarkSelected  = new Color(0.243f, 0.372f, 0.588f, 1f);
        private static readonly Color _bgLightSelected = new Color(0.243f, 0.490f, 0.827f, 1f);

        static MID_BaseSOProjectIconDrawer()
        {
            EditorApplication.projectWindowItemOnGUI += DrawCustomIcon;
        }

        private static void DrawCustomIcon(string guid, Rect selectionRect)
        {
            if (_confirmedNotMidBaseSO.Contains(guid)) return;

            if (!_iconCache.TryGetValue(guid, out var icon))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var so = AssetDatabase.LoadAssetAtPath<MID_BaseSO>(path);
                if (so == null)
                {
                    _confirmedNotMidBaseSO.Add(guid);
                    return;
                }

                icon = so.ResolveIcon();
                _iconCache[guid] = icon; // cache even null — "no custom icon" is a valid, stable result
            }

            if (icon == null) return; // falls through to Unity's own default script icon

            // List view rows are short and wide; icon-view tiles are roughly
            // square. Use height as the size cue for list view so the icon
            // doesn't overflow into the label text next to it.
            bool isListView = selectionRect.height <= 20f;
            float size = isListView ? selectionRect.height
                                    : Mathf.Min(selectionRect.width, selectionRect.height);

            var iconRect = new Rect(selectionRect.x, selectionRect.y, size, size);

            // Erase the default icon Unity already painted into this rect
            // before drawing ours — see file header. Without this, letterboxed
            // edges and any transparent pixels in `icon` show the old icon.
            var selectedGuids = Selection.assetGUIDs;
            bool isSelected = selectedGuids != null && System.Array.IndexOf(selectedGuids, guid) >= 0;

            Color bg = EditorGUIUtility.isProSkin
                ? (isSelected ? _bgDarkSelected  : _bgDark)
                : (isSelected ? _bgLightSelected : _bgLight);
            EditorGUI.DrawRect(iconRect, bg);

            GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);
        }

        /// <summary>
        /// Called by MID_BaseSOEditor when an asset's custom icon field changes,
        /// so the Project window picks up the new icon immediately instead of
        /// showing a stale cached one until the next domain reload.
        /// </summary>
        public static void InvalidateCache(string guid)
        {
            _iconCache.Remove(guid);
        }

        [InitializeOnLoadMethod]
        private static void ClearCacheOnScriptReload()
        {
            _iconCache.Clear();
            _confirmedNotMidBaseSO.Clear();
        }
    }
}
