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
            // DEFENSIVE FIX ("first fire shows the wrong visual, self-corrects
            // on the second fire — reproduces whether or not CustomShape is
            // assigned"): I traced the branch logic below exhaustively and it
            // should always be deterministic from cfg alone, with no
            // dependency on prior state — but you've now confirmed the bug
            // reproduces regardless of CustomShape, which rules out the branch
            // decision itself as the cause. I could not pin down the exact
            // remaining mechanism through static reading (my leading
            // suspicion: ProjectileVisual_2D.prefab's shape-mesh child saves
            // with MeshRenderer.enabled=1 in the sandbox prefab, contradicting
            // this file's own comment that it's "never on this GameObject
            // directly" — Awake() should already disable it, but something
            // about first-activation timing on a pooled instance may be
            // slipping past that; I don't have a way to single-step Unity
            // here to confirm).
            //
            // This is a defensive, not root-cause, fix: force BOTH renderers
            // to a known-off state before any decision is made, so whichever
            // one the branch below turns on is the only one active,
            // regardless of what state either was already in coming in. If
            // this DOESN'T resolve it, the bug isn't in this method's
            // rendering logic at all — next place to look is whether
            // OnInitialise is even being reached on the object's true first
            // activation (add a one-off MID_Logger.LogDebug at the very top
            // logging ConfigId + cfg?.name + Time.frameCount, compare across
            // a first vs second fire of the same pooled instance).
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

            // FIX: Ensure components exist at runtime (client pool prefabs don't pre-add them)
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

                // FIX: previously passed cfg?.ProjectileSprite unconditionally —
                // a config with UseSprite = false (ProjectileRenderer2D/3D and
                // ProjectileRegistry.GetUVRect both already treat this as "use
                // the plain fallback, ignore whatever's in ProjectileSprite")
                // would still show its sprite field here if one happened to be
                // assigned. Matches the Rust-sim renderers' own gate.
                bool useSprite = cfg != null && cfg.UseSprite;
                ApplySpriteOptimised(cfg, useSprite ? cfg.ProjectileSprite : null);
            }

            // GROWTH FIX ("gets spawned full scale rather than scaling up as
            // intended") — no-ops immediately if cfg is null or
            // UseScaleGrowth is false, leaving the one-shot scale set just
            // above untouched. See ProjectileVisualBase's own section
            // comment for the full explanation.
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
        /// Scale matches ProjectileRenderer2D: (FullSizeX, FullSizeY, 1) — set on
        /// THIS transform (not the child), same as before the child-object split.
        /// _shapeMeshChild keeps identity local scale, so it inherits this size
        /// through normal Unity parent/child transform propagation — no behavior
        /// change from isolating the mesh onto its own object.
        ///
        /// BUG FIX: this used to set _MainTex only, and only inside an
        /// `if (texture != null)` guard — meaning two separate bugs at once:
        ///
        ///  1. _UVRect was never set at all. InstancedProjectile(_URP).shader —
        ///     the shader this component's own header comment says to assign as
        ///     _fallbackShapeMaterial "for correct atlas UV support" — declares
        ///     BOTH _MainTex and _UVRect as material properties, and per that
        ///     shader file's own comment, the shipped material's baked-in
        ///     default _UVRect is (0.24, 0, 4.08, 1.2) — a corrupted, very much
        ///     not-full-texture sub-rect, left over from when it was authored
        ///     for the shared-atlas batch renderer (ProjectileRenderer2D, which
        ///     DOES set _UVRect every frame via ProjectileRegistry.GetUVRect).
        ///     Nothing here ever overrode it, so every shape-mesh projectile
        ///     sampled that same broken corner of whatever texture got bound —
        ///     which reads as "the material/sprite is never (correctly) set".
        ///     Fix: always compute and set _UVRect too, via the exact same
        ///     ProjectileRegistry.GetUVRect used by the Rust-sim renderers —
        ///     one source of truth for "which part of this texture to sample"
        ///     instead of reimplementing it here.
        ///
        ///  2. The property block was only touched when a texture existed —
        ///     skip the call entirely (no sprite this config, or config not
        ///     found) and this pooled instance's renderer keeps showing
        ///     whatever _MainTex/_UVRect its PREVIOUS pool cycle left bound.
        ///     Fix: always set the property block, falling back to
        ///     Texture2D.whiteTexture + the full (0,0,1,1) rect — exactly what
        ///     ProjectileRenderer2D/3D fall back to for a no-sprite config —
        ///     so every call leaves this instance in a fully-defined state.
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
        /// SCALING FIX ("physics-based projectiles do not support scaling"):
        /// ApplyShapeMeshOptimised (below) has always set transform.localScale
        /// from cfg.FullSizeX/FullSizeY — this plain-SpriteRenderer path never
        /// did, so any config using a plain sprite (UseSprite = true, no
        /// CustomShape — the common case, and the ONLY path physics
        /// projectiles ever hit unless a CustomShape is assigned) rendered at
        /// whatever scale the pooled prefab/parent already happened to have,
        /// completely ignoring FullSizeX/Y. Mirrors the shape-mesh path's own
        /// scale line exactly. Falls back to Vector3.one when cfg is null
        /// (config not resolved yet) — same fallback OnReturnToPool already
        /// uses, and LocalObjectPool.ReturnObject's own ResetObject() also
        /// resets localScale to Vector3.one on every return regardless, so
        /// there's no stale-scale risk between pool cycles either way.
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
