// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "ProjectileVisual_2D.cs"
// ============================================================================
using UnityEngine;
using MidManStudio.Projectiles.Config;
using MidManStudio.Core.EditorUtils;

namespace MidManStudio.Projectiles.Visuals
{
    public class ProjectileVisual_2D : ProjectileVisualBase
    {
        #region Inspector

        [Header("2D Renderers")]
        [SerializeField] public SpriteRenderer projectileSpriteRend;
        [SerializeField] public TrailRenderer  projectileTrailRend;

        [Header("Draw Order")]
        [Tooltip("Sorting layer shared by the sprite, shape mesh, and trail on this " +
                 "prefab. Previously there was no way to set this at all — only the " +
                 "per-part sortingOrder fields below existed, so the sorting LAYER was " +
                 "stuck at whatever the Renderer components happened to be authored " +
                 "with in the prefab, invisibly, with no inspector control.")]
        [SerializeField, MID_SortingLayer] private string _sortingLayerName = "Default";
        [SerializeField] private int _spriteSortingOrder = 1;
        [SerializeField] private int _trailSortingOrder  = 0;

        [Header("Rendering Mode Override")]
        [Tooltip("Force this instance to ALWAYS render through the SpriteRenderer, " +
                 "ignoring the config's CustomShape entirely — for a projectile that " +
                 "never needs a mesh shape, just a sprite. No shape mesh child is ever " +
                 "created or used while this is on.\n\n" +
                 "Also the more reliable choice re: atlas timing: SpriteRenderer resolves " +
                 "a packed sprite's texture through Unity's own built-in rendering path, " +
                 "which re-resolves it continuously every frame at the engine level. The " +
                 "shape-mesh path below instead manually snapshots sprite.texture + a " +
                 "computed UV rect into a MaterialPropertyBlock once per OnInitialise call " +
                 "— correct once the atlas has finished (re)binding, but until then it can " +
                 "visibly lag a few shots behind. Turn this on for any prefab variant that " +
                 "should just be a plain sprite.")]
        [SerializeField] private bool _forceSpriteRendererOnly = false;

        [Header("Shape Mesh (CustomShape configs only)")]
        [Tooltip("Isolated on its own child GameObject, auto-created at runtime when a " +
                 "CustomShape config first needs it — never on this GameObject directly. " +
                 "SpriteRenderer and MeshRenderer sharing one GameObject was the actual " +
                 "issue here: not a hard Unity restriction, but two renderer components " +
                 "on one object is exactly the kind of setup where GetComponent<Renderer>() " +
                 "calls elsewhere in the pool/prefab pipeline become ambiguous about which " +
                 "one they're getting, and where a prefab that had them pre-added manually " +
                 "(instead of lazily, only-when-needed, on their own object) could easily end " +
                 "up with stale/misconfigured component state after a pool cycle. Child object " +
                 "removes the ambiguity entirely — the sprite GameObject has exactly one " +
                 "renderer, always.")]
        [SerializeField] private Transform     _shapeMeshChild;
        [SerializeField] private MeshFilter    _shapeMeshFilter;
        [SerializeField] private MeshRenderer  _shapeMeshRenderer;
        [Tooltip("Sorting order for the shape MeshRenderer.")]
        [SerializeField] private int _shapeSortingOrder = 1;
        [Tooltip("Material for shape mesh rendering.\n" +
                 "Assign InstancedProjectile.shader material for correct atlas UV support.\n" +
                 "If null, falls back to Sprites/Default (no atlas UV remapping).")]
        [SerializeField] private Material _fallbackShapeMaterial;

        private const string ShapeMeshChildName = "ShapeMesh (auto)";

        #endregion

        #region State

        private Sprite _cachedSprite;
        private bool   _trailConfigured;
        private ushort _cachedConfigId;
        private bool   _configInitialised;
        private bool   _usingShapeMesh;

        private MaterialPropertyBlock _shapeMpb;

     
        private static Sprite    _fallbackSprite;
        private static Texture2D _fallbackTexture;

        #endregion

        #region Fallback Sprite

        private static Sprite GetFallbackSprite()
        {
            if (_fallbackSprite != null) return _fallbackSprite;

            _fallbackTexture = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode   = TextureWrapMode.Clamp,
                name       = "FallbackProjectileTexture"
            };
            Color32[] pixels = new Color32[16];
            for (int i = 0; i < 16; i++) pixels[i] = new Color32(255, 255, 255, 255);
            _fallbackTexture.SetPixels32(pixels);
            _fallbackTexture.Apply(false, true);

            _fallbackSprite = Sprite.Create(
                _fallbackTexture,
                new Rect(0, 0, 4, 4),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit: 4f);
            _fallbackSprite.name = "FallbackProjectileSprite";
            return _fallbackSprite;
        }

#if UNITY_EDITOR
        // Registered once per domain load. Fires right before the NEXT domain
        // reload, so whatever was cached during this session gets explicitly
        // destroyed instead of orphaned. The static fields themselves are reset
        // to null for free when the new domain loads — only the native side
        // needs the explicit call.
        static ProjectileVisual_2D()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += ReleaseStaticNativeCaches;
        }

        private static void ReleaseStaticNativeCaches()
        {
            if (_fallbackSprite != null)
            {
                UnityEngine.Object.DestroyImmediate(_fallbackSprite);
                _fallbackSprite = null;
            }
            if (_fallbackTexture != null)
            {
                UnityEngine.Object.DestroyImmediate(_fallbackTexture);
                _fallbackTexture = null;
            }
        }
#endif

        #endregion

        #region Unity Lifecycle

        protected override void Awake()
        {
            base.Awake();

            // Pool-recycled instance may already have the child from a previous
            // cycle — find it by name rather than GetComponent<MeshFilter>() on
            // this GameObject, since that should never have one directly.
            if (_shapeMeshChild == null)
            {
                var existing = transform.Find(ShapeMeshChildName);
                if (existing != null)
                {
                    _shapeMeshChild    = existing;
                    _shapeMeshFilter   = existing.GetComponent<MeshFilter>();
                    _shapeMeshRenderer = existing.GetComponent<MeshRenderer>();
                }
            }

            // Disable if found — sprite is the default visual
            if (_shapeMeshRenderer != null) _shapeMeshRenderer.enabled = false;
        }

        #endregion

        #region ProjectileVisualBase

        protected override void ApplyRotation(Vector3 dir)
        {
            if (dir.sqrMagnitude < 0.001f) return;
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, 0f, angle);
        }

        protected override void OnInitialise(ProjectileConfigSO cfg)
        {
            // Force both renderers off before any decision is made, so whichever
            // one the branch below turns on is the only one active, whatever
            // state either was in on entry (a pooled instance can come back with
            // the shape renderer on).
            if (_shapeMeshRenderer   != null) _shapeMeshRenderer.enabled   = false;
            if (projectileSpriteRend != null) projectileSpriteRend.enabled = false;

            bool configChanged = !_configInitialised || _cachedConfigId != ConfigId;
            if (configChanged)
            {
                _cachedConfigId    = ConfigId;
                _trailConfigured   = false;
                _configInitialised = true;
            }

            // Resolve shape first — _forceSpriteRendererOnly (see Inspector) skips
            // the shape mesh entirely regardless of what the config specifies.
            bool hasCustomShape = !_forceSpriteRendererOnly && cfg != null && cfg.CustomShape != null;
            Mesh shapeMesh      = hasCustomShape ? cfg.CustomShape.GetMesh() : null;
            bool needsShapeMesh = hasCustomShape && shapeMesh != null && shapeMesh.vertexCount > 0;

            // Make sure the shape-mesh components exist (client pool prefabs do
            // not pre-add them).
            if (needsShapeMesh) EnsureShapeMeshComponents();

            bool canUseShape = needsShapeMesh
                            && _shapeMeshFilter   != null
                            && _shapeMeshRenderer != null;

            if (canUseShape)
            {
                ApplyShapeMeshOptimised(cfg, shapeMesh);
                if (projectileSpriteRend != null) projectileSpriteRend.enabled = false;
                _usingShapeMesh = true;
            }
            else
            {
                // Disable shape renderer — it may have been created on a previous pool cycle
                if (_shapeMeshRenderer != null) _shapeMeshRenderer.enabled = false;
                _usingShapeMesh = false;

                // A config with UseSprite = false uses the plain fallback and
                // ignores ProjectileSprite, the same gate ProjectileRenderer2D/3D
                // and ProjectileRegistry.GetUVRect apply.
                bool useSprite = cfg != null && cfg.UseSprite;
                ApplySpriteOptimised(cfg, useSprite ? cfg.ProjectileSprite : null);
            }

            // Starts scale growth when cfg.UseScaleGrowth is set. Otherwise it
            // returns at once and the one-shot scale set above stands. See
            // ProjectileVisualBase.
            RefreshScaleGrowth(cfg);

            ApplyTrailOptimised(cfg);
        }

        protected override void OnReturnToPool()
        {
            _configInitialised = false;
            _trailConfigured   = false;
            _cachedSprite      = null;

            if (projectileSpriteRend != null)
            {
                projectileSpriteRend.enabled = true;
                projectileSpriteRend.sprite  = null;
                projectileSpriteRend.color   = Color.white;
            }

            if (_shapeMeshRenderer != null) _shapeMeshRenderer.enabled  = false;
            if (_shapeMeshFilter   != null) _shapeMeshFilter.sharedMesh = null;

            if (_usingShapeMesh)
            {
                transform.localScale = Vector3.one;
                _usingShapeMesh = false;
            }

            if (projectileTrailRend != null)
            {
                projectileTrailRend.emitting = false;
                projectileTrailRend.enabled  = false;
                projectileTrailRend.Clear();
            }
        }

        public override void HideProjectile()
        {
            if (projectileSpriteRend != null) projectileSpriteRend.enabled  = false;
            if (_shapeMeshRenderer   != null) _shapeMeshRenderer.enabled    = false;
            if (projectileTrailRend  != null) projectileTrailRend.emitting  = false;
        }

        #endregion

        #region Shape Mesh

        /// <summary>
        /// Creates the shape-mesh child GameObject (and its MeshFilter/MeshRenderer)
        /// on first use, if it doesn't already exist. Lives entirely off to the side
        /// of this GameObject — the sprite object itself never carries these
        /// components. Pool prefabs never need to pre-add anything for this.
        /// </summary>
        private void EnsureShapeMeshComponents()
        {
            if (_shapeMeshChild == null)
            {
                var childGO = new GameObject(ShapeMeshChildName);
                childGO.transform.SetParent(transform, worldPositionStays: false);
                childGO.transform.localPosition = Vector3.zero;
                childGO.transform.localRotation = Quaternion.identity;
                childGO.transform.localScale    = Vector3.one;

                _shapeMeshChild    = childGO.transform;
                _shapeMeshFilter   = childGO.AddComponent<MeshFilter>();
                _shapeMeshRenderer = childGO.AddComponent<MeshRenderer>();

                // Assign material — prefer inspector-assigned, then Sprites/Default
                if (_fallbackShapeMaterial != null)
                {
                    _shapeMeshRenderer.sharedMaterial = _fallbackShapeMaterial;
                }
                else
                {
                    // Sprites/Default is always available (Built-in and URP)
                    var shader = Shader.Find("Sprites/Default");
                    if (shader == null) shader = Shader.Find("Unlit/Transparent");
                    if (shader != null)
                        _shapeMeshRenderer.sharedMaterial = new Material(shader)
                            { name = "DynamicShapeFallback" };
                }

                _shapeMeshRenderer.enabled = false;
            }
        }

        /// <summary>
        /// Applies the CustomShape mesh to the child's MeshFilter/MeshRenderer.
        /// The scale (FullSizeX, FullSizeY, 1), as in ProjectileRenderer2D, is set
        /// on THIS transform, not the child. _shapeMeshChild keeps identity local
        /// scale and inherits the size through the normal parent/child transform.
        ///
        /// The property block always sets both _MainTex and _UVRect, and always
        /// runs, so every call leaves the instance in a fully defined state:
        ///  - InstancedProjectile(_URP).shader declares both properties, and the
        ///    shipped material's baked default _UVRect (0.24, 0, 4.08, 1.2) is not
        ///    a full texture rect. _UVRect comes from ProjectileRegistry.GetUVRect,
        ///    the same source the Rust-sim renderers use.
        ///  - With no sprite (or no config) it falls back to
        ///    Texture2D.whiteTexture and the full (0, 0, 1, 1) rect, as
        ///    ProjectileRenderer2D/3D do, so a pooled instance never keeps the
        ///    texture its previous use left bound.
        /// </summary>
        private void ApplyShapeMeshOptimised(ProjectileConfigSO cfg, Mesh mesh)
        {
            _shapeMeshFilter.sharedMesh = mesh;

            transform.localScale = new Vector3(cfg.FullSizeX, cfg.FullSizeY, 1f);

            bool hasSprite = cfg.UseSprite && cfg.ProjectileSprite?.texture != null;

            Texture2D tex = hasSprite ? cfg.ProjectileSprite.texture : Texture2D.whiteTexture;
            Vector4   uv  = hasSprite && ProjectileRegistry.HasInstance
                ? ProjectileRegistry.Instance.GetUVRect(ConfigId)
                : new Vector4(0f, 0f, 1f, 1f);

            if (_shapeMpb == null) _shapeMpb = new MaterialPropertyBlock();
            _shapeMpb.SetTexture("_MainTex", tex);
            _shapeMpb.SetVector("_UVRect", uv);
            _shapeMeshRenderer.SetPropertyBlock(_shapeMpb);

            _shapeMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _shapeMeshRenderer.receiveShadows     = false;
            _shapeMeshRenderer.sortingLayerName   = _sortingLayerName;
            _shapeMeshRenderer.sortingOrder       = _shapeSortingOrder;
            _shapeMeshRenderer.enabled            = true;
        }

        #endregion

        #region Sprite

        /// <summary>
        /// Sets transform.localScale from cfg.FullSizeX/FullSizeY (Vector3.one when
        /// cfg is null) before applying the sprite, the same scale line the
        /// shape-mesh path uses. LocalObjectPool.ReturnObject also resets
        /// localScale on every return.
        /// </summary>
        private void ApplySpriteOptimised(ProjectileConfigSO cfg, Sprite sprite)
        {
            transform.localScale = cfg != null
                ? new Vector3(cfg.FullSizeX, cfg.FullSizeY, 1f)
                : Vector3.one;

            if (projectileSpriteRend == null) return;

            projectileSpriteRend.enabled       = true;
            projectileSpriteRend.sortingLayerName = _sortingLayerName;
            projectileSpriteRend.sortingOrder  = _spriteSortingOrder;

            Sprite toUse = sprite != null ? sprite : GetFallbackSprite();
            if (_cachedSprite != toUse)
            {
                projectileSpriteRend.sprite = toUse;
                _cachedSprite = toUse;
            }
        }

        #endregion

        #region Trail

        private void ApplyTrailOptimised(ProjectileConfigSO cfg)
        {
            if (projectileTrailRend == null) return;

            if (cfg == null || !cfg.HasTrail)
            {
                projectileTrailRend.enabled  = false;
                projectileTrailRend.emitting = false;
                _trailConfigured = false;
                return;
            }

            if (!_trailConfigured)
            {
                if (cfg.TrailMaterial != null)
                {
                    if (cfg.UseSharedTrailMaterial)
                        projectileTrailRend.sharedMaterial = cfg.TrailMaterial;
                    else
                        projectileTrailRend.material = cfg.TrailMaterial;
                }

                if (cfg.UseGradientOverride && cfg.TrailGradient != null)
                    projectileTrailRend.colorGradient = cfg.TrailGradient;

                projectileTrailRend.time              = cfg.TrailTime;
                projectileTrailRend.startWidth        = cfg.TrailStartWidth;
                projectileTrailRend.endWidth          = cfg.TrailEndWidth;
                projectileTrailRend.numCapVertices    = cfg.TrailCapVertices;
                projectileTrailRend.minVertexDistance = cfg.TrailMinVertexDistance;

                projectileTrailRend.shadowCastingMode         = UnityEngine.Rendering.ShadowCastingMode.Off;
                projectileTrailRend.receiveShadows             = false;
                projectileTrailRend.generateLightingData       = false;
                projectileTrailRend.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                projectileTrailRend.alignment                  = LineAlignment.View;
                projectileTrailRend.sortingLayerName           = _sortingLayerName;
                projectileTrailRend.sortingOrder               = _trailSortingOrder;

                _trailConfigured = true;
            }

            projectileTrailRend.Clear();
            projectileTrailRend.enabled  = true;
            projectileTrailRend.emitting = true;
        }

        #endregion
    }
}
