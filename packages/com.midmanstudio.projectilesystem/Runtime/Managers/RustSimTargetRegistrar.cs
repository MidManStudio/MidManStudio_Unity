// Auto-registers this GameObject as a RustSim collision target — drop this
// on any target that should participate in RustSim (native-simulated)
// projectile hit detection (players, enemies, breakables, ...).
//
// WHY THIS EXISTS: RustSim's target system (MID_MasterProjectileSystem.
// RegisterTarget2D/3D → ServerProjectileAuthority/LocalProjectileManager)
// needs three things done correctly for every target, none of which were
// previously handled automatically:
//
//   1. A STABLE TargetId. Handled here — NetworkObjectId if this object has
//      a NetworkObject, otherwise a locally-generated id.
//
//   2. The target's POSITION KEPT CURRENT as it moves. RegisterTarget2D/3D
//      is an UPSERT (confirmed by reading ServerProjectileAuthority's
//      implementation directly — it linear-searches for an existing entry
//      with the same TargetId and overwrites it in place, appending only if
//      not found) — there is no separate "move" or "sync position" method;
//      calling Register again with the same id and a new position IS how you
//      move a registered target. This component does that on a configurable
//      cadence (default: every FixedUpdate) instead of registering once and
//      leaving a stale position behind.
//
//   3. The target's REAL Unity layer. RegisterTarget2D/3D's plain
//      (target, int) overload defaults unityLayer to 0 — silently wrong for
//      anything not actually on layer 0, and the root cause of an earlier
//      "HitLayers doesn't work" bug that took a while to track down. This
//      component always uses the (target, GameObject) overload, which reads
//      gameObject.layer directly — that mistake isn't possible here.
//
// PERFORMANCE NOTE: because RegisterTarget2D/3D does a linear scan over
// every currently-registered target to find (or not find) this one's id,
// registering N targets every single tick costs O(N²) per frame. That's
// negligible for tens of targets — this is a native-buffer system built for
// bulk operations — but if you have hundreds of simultaneously active
// targets, raise _updateEveryNTicks well above 1 rather than updating every
// target every tick.
//
// SETUP: drop on the target, done. Works with or without Netcode — the
// underlying RegisterTarget2D/3D already no-ops correctly on clients in a
// networked game (only the server's call actually registers anything) and
// registers directly in local/offline mode, so this component doesn't need
// to know or care which mode it's in.
//
// SHAPE AUTO-DETECTION ("it's not only circle and spear soldiers"): beyond
// CircleCollider2D/SphereCollider, this also detects BoxCollider2D,
// CapsuleCollider2D, EdgeCollider2D, PolygonCollider2D (2D) and
// CapsuleCollider (3D) and registers a ShapeCollider2D/3D instead of a plain
// circle/sphere target — see ShapeCollider2D's doc comment in ProjectileLib.cs
// for how those work. Detection priority, checked in this order, first match
// wins: Circle/Sphere → Box → Capsule → Edge → Polygon. Before this, anything
// without a Circle/Sphere collider silently fell back to a hardcoded 0.5
// radius circle regardless of its real shape — often very wrong for anything
// long, thin, or non-round.
//
// 3D BOX LIMITATION: a 3D box has 12 edges; ShapeCollider3D's single
// closed-point-loop format (same one that represents a 2D box's 4 corners
// exactly) can't trace a cuboid's edges without ambiguity. BoxCollider (3D)
// is therefore approximated as a CAPSULE along its longest local axis, not
// registered exactly — reasonable for most gameplay hitboxes, but a real
// approximation. If you need an exact 3D box (or anything else exact),
// author it point-by-point instead with RustSimCustomShapeAuthoring, whose
// scene-view editor isn't limited to Unity's built-in collider shapes at all.
// 2D Box, Capsule, Edge, and Polygon detection below are all exact, no
// approximation involved.

using UnityEngine;
using MidManStudio.Projectiles.Core;

namespace MidManStudio.Projectiles.Managers
{
    [AddComponentMenu("MidMan Studio/Projectile System/RustSim Target Registrar")]
    public sealed class RustSimTargetRegistrar : MonoBehaviour
    {
        #region Inspector

        [Header("Dimensionality")]
        [Tooltip("Register as a 2D target (RegisterTarget2D) or a 3D one (RegisterTarget3D).")]
        [SerializeField] private bool _is3D = false;

        [Header("Collision Shape")]
        [Tooltip("Skip shape auto-detection entirely and always register a plain " +
                 "circle/sphere, even if a Box/Capsule/Edge/Polygon collider is also " +
                 "present on this object. For the rare case where you have one of " +
                 "those for gameplay/visual purposes but specifically want the cheap " +
                 "circle/sphere approximation for RustSim instead.")]
        [SerializeField] private bool _forceCircleOrSphere = false;

        [Tooltip("Hit radius reported to RustSim's collision check when this " +
                 "registers as a plain circle/sphere (CircleCollider2D/SphereCollider " +
                 "found, or nothing matched at all). Leave at 0 to auto-detect from " +
                 "that collider at Start — set explicitly to override, or if no such " +
                 "collider exists and you still want a circle/sphere rather than a " +
                 "shape. Ignored entirely once a Box/Capsule/Edge/Polygon collider is " +
                 "detected — see the file header's shape auto-detection note.")]
        [SerializeField] private float _radius = 0f;

        [Header("Target Id")]
        [Tooltip("Leave at 0 to auto-derive: this object's NetworkObjectId if it " +
                 "has a NetworkObject, otherwise a locally-generated id from " +
                 "GetInstanceID(). Only set this explicitly if you need a " +
                 "specific, predictable id (e.g. to reference from elsewhere).")]
        [SerializeField] private uint _explicitTargetId = 0;

        [Header("Networking")]
        [Tooltip(
            "NETWORK OBJECT ID TIMING FIX: if this object also has a NetworkObject, " +
            "its NetworkObjectId is not valid until Netcode has actually spawned it — " +
            "that does NOT happen by Awake(), and isn't guaranteed to have happened by " +
            "Start() either (a scene-placed NetworkObject can spawn several frames " +
            "later; one spawned at runtime spawns whenever the spawning code calls " +
            "Spawn()). Registering before that bakes in an invalid TargetId — every " +
            "such object would resolve to the same not-yet-assigned id and collide " +
            "with every other one still waiting to spawn. Enable this on anything " +
            "that's also a NetworkObject: both target-id resolution AND registration " +
            "are held off until NetworkObject.IsSpawned is actually true, retried " +
            "automatically every tick in the meantime (see Update/FixedUpdate below) — " +
            "same retry mechanism the static-target race-condition fix uses.")]
        [SerializeField] private bool _isNetworkedObject = false;

        [Header("Movement")]
        [Tooltip(
            "STATIC TARGET FIX: not every target moves — walls, breakables, level " +
            "geometry, and plenty of enemies sit still most/all of the time, but this " +
            "component previously always re-registered on the Update Rate cadence below " +
            "regardless, silently paying for a linear-scan RegisterTarget2D/3D upsert " +
            "every tick (see the file header's O(N²) performance note) even when the " +
            "position hadn't changed at all. Enable this for anything that never moves: " +
            "Update/FixedUpdate below become a single bool check instead of a full " +
            "re-register — but only ONCE REGISTRATION HAS ACTUALLY SUCCEEDED, not just " +
            "once Start() has run — see the race-condition note on RegisterNow(). " +
            "Leave off for anything that moves, including anything driven by a moving " +
            "parent transform.")]
        [SerializeField] private bool _isStatic = false;

        [Header("Update Rate")]
        [Tooltip("Ignored when Is Static is on — a static target never re-registers " +
                 "after its initial Start() call, so there's no cadence to configure. " +
                 "FixedUpdate matches physics tick rate — the usual choice for " +
                 "anything moving via Rigidbody. Switch to Update if this " +
                 "target's position changes outside FixedUpdate.")]
        [SerializeField] private bool _useFixedUpdate = true;

        [Tooltip("Re-register (refresh position) every N ticks instead of every " +
                 "single one — see the file header's performance note. 1 = every tick. " +
                 "Ignored when Is Static is on.")]
        [SerializeField, Min(1)] private int _updateEveryNTicks = 1;

        #endregion

        #region State

        private uint _targetId;
        private bool _targetIdResolved;
        private int  _tickCounter;
        private bool _hasRegisteredOnce;
        private bool _subscribedToReadyEvent;

        // SHAPE STATE: populated once at Start() by DetectShape(). Points are
        // stored relative to THIS transform (not the source collider's own
        // transform, which may be a child) — RegisterNow() reconstructs world
        // points every call via transform.TransformPoint, so a shape moves/
        // rotates/scales correctly with this GameObject regardless of which
        // child the detected collider actually lives on.
        private bool               _isShape;
        private byte               _shapeType;
        private int                _shapePointCount;
        private bool               _shapeClosed;
        private float              _shapeThickness;
        private readonly Vector3[] _shapeLocalPoints = new Vector3[ShapeCollider2D.MaxPoints];

        #endregion

        #region Lifecycle

        private void Start()
        {
            DetectShape();
            TryRegisterOrWaitForReady(); // handles both "already ready" (dynamic spawn) and "not yet" (subscribes) — see that method's doc comment
        }

        private void OnEnable()
        {
            // Re-registers on re-enable (e.g. object pooled and reactivated).
            // If it was disabled before ever successfully registering (a
            // pooled object flipped inactive again quickly, mid-wait), the
            // OnDisable below already cancelled any pending OnSystemReady
            // subscription — TryRegisterOrWaitForReady picks that back up
            // correctly rather than leaving it stalled with nothing driving
            // it until the next poll tick.
            if (_hasRegisteredOnce) RegisterNow();
            else                    TryRegisterOrWaitForReady();
        }

        private void OnDisable()
        {
            Deactivate();
            UnsubscribeFromReadyEvent();
        }

        private void OnDestroy()
        {
            Deactivate();
            UnsubscribeFromReadyEvent();
        }

        private void Update()
        {
            if (_useFixedUpdate) return;
            // STATIC TARGET RACE-CONDITION FIX ("set to static, collisions never
            // work"): only stop ticking once registration has ACTUALLY
            // succeeded at least once — not just because _isStatic is set. The
            // old code skipped Update/FixedUpdate entirely the moment _isStatic
            // was true, with only ONE registration attempt ever (in Start()).
            // If MID_MasterProjectileSystem hadn't finished initializing yet at
            // that exact frame — a genuine, common script-execution-order race,
            // not a rare edge case — RegisterNow() silently no-op'd and NOTHING
            // ever retried, so the target just stayed permanently unregistered.
            // A moving (non-static) target self-healed from the same race
            // within a few frames purely by continuing to tick normally; a
            // static one had no such safety net. Now both behave the same way
            // until the first successful registration, and only then does a
            // static target actually stop ticking. This tick-based retry runs
            // ALONGSIDE the OnSystemReady event subscription below (Start()/
            // TryRegisterOrWaitForReady) as an independent second safety net —
            // neither mechanism depends on the other for correctness.
            if (_isStatic && _hasRegisteredOnce) return;
            Tick();
        }

        private void FixedUpdate()
        {
            if (!_useFixedUpdate) return;
            if (_isStatic && _hasRegisteredOnce) return; // see Update()'s comment
            Tick();
        }

        #endregion

        #region Implementation

        private void Tick()
        {
            if (++_tickCounter < _updateEveryNTicks) return;
            _tickCounter = 0;
            RegisterNow();
        }

        /// <summary>
        /// DYNAMICALLY SPAWNED OBJECTS FIX, BOTH NET AND NON-NET: checks
        /// MID_MasterProjectileSystem.IsReady FIRST — if the system is
        /// already initialised (the common case for anything Instantiate()'d
        /// during normal gameplay, well after scene load), registers
        /// immediately with zero extra latency, exactly as if this were a
        /// direct RegisterNow() call. Only subscribes to OnSystemReady when
        /// it genuinely isn't ready yet (the case a scene-present object can
        /// hit at startup, racing this system's own Awake()). Subscribing
        /// unconditionally instead — without this readiness check — would be
        /// the actual bug: an object spawned AFTER OnSystemReady already
        /// fired would subscribe to an event that's never firing again and
        /// hang forever waiting for it, with only the tick-based retry below
        /// left to save it.
        /// </summary>
        private void TryRegisterOrWaitForReady()
        {
            if (MID_MasterProjectileSystem.IsReady)
            {
                RegisterNow();
                return;
            }

            if (_subscribedToReadyEvent) return;
            MID_MasterProjectileSystem.OnSystemReady += HandleSystemReady;
            _subscribedToReadyEvent = true;
        }

        private void HandleSystemReady()
        {
            UnsubscribeFromReadyEvent();
            RegisterNow();
        }

        private void UnsubscribeFromReadyEvent()
        {
            if (!_subscribedToReadyEvent) return;
            MID_MasterProjectileSystem.OnSystemReady -= HandleSystemReady;
            _subscribedToReadyEvent = false;
        }

        private void RegisterNow()
        {
            var system = MID_MasterProjectileSystem.HasInstance
                ? MID_MasterProjectileSystem.Instance : null;
            if (system == null) return; // retried next tick — see Update()'s comment

            // NETWORK OBJECT ID TIMING FIX ("network objects do not exist in
            // Awake"): a NetworkObject's NetworkObjectId isn't valid/stable
            // until Netcode has actually spawned it — not guaranteed by
            // Start(), let alone Awake(). Resolving (and caching) _targetId
            // any earlier than this check would bake in whatever placeholder
            // value NetworkObjectId happens to hold pre-spawn — likely the
            // same value for every such object, i.e. TargetId collisions
            // between every one of them still waiting to spawn. Held off (and
            // retried next tick) until IsSpawned is actually true.
            if (_isNetworkedObject)
            {
                var netObj = GetComponent<Unity.Netcode.NetworkObject>();
                if (netObj == null || !netObj.IsSpawned) return;
            }

            if (!_targetIdResolved)
            {
                _targetId = ResolveTargetId();
                _targetIdResolved = true;
            }

            if (_isShape) RegisterShapeNow(system);
            else          RegisterCircleNow(system);

            _hasRegisteredOnce = true;
            UnsubscribeFromReadyEvent(); // no longer needed once actually registered
        }

        private void RegisterCircleNow(MID_MasterProjectileSystem system)
        {
            Vector3 pos = transform.position;

            if (_is3D)
            {
                system.RegisterTarget3D(new CollisionTarget3D
                {
                    X = pos.x, Y = pos.y, Z = pos.z,
                    Radius   = _radius,
                    TargetId = _targetId,
                    Active   = 1
                }, gameObject);
            }
            else
            {
                system.RegisterTarget2D(new CollisionTarget
                {
                    X = pos.x, Y = pos.y,
                    Radius   = _radius,
                    TargetId = _targetId,
                    Active   = 1
                }, gameObject);
            }
        }

        /// <summary>
        /// MOVING SHAPES ("must support moving stuff too"): _shapeLocalPoints
        /// are re-projected through transform.TransformPoint on every single
        /// call — not cached as world points anywhere — so a shape correctly
        /// follows this GameObject's current position, rotation, AND scale
        /// every time this runs (Tick()'s cadence, or Start()/OnEnable() once).
        /// </summary>
        private void RegisterShapeNow(MID_MasterProjectileSystem system)
        {
            if (_is3D)
            {
                var shape = new ShapeCollider3D
                {
                    TargetId = _targetId, ShapeType = _shapeType,
                    PointCount = (byte)_shapePointCount,
                    Closed = (byte)(_shapeClosed ? 1 : 0), Active = 1,
                    Thickness = ScaledThickness()
                };
                for (int i = 0; i < _shapePointCount; i++)
                    shape.SetPoint(i, transform.TransformPoint(_shapeLocalPoints[i]));
                system.RegisterShape3D(in shape, gameObject);
            }
            else
            {
                var shape = new ShapeCollider2D
                {
                    TargetId = _targetId, ShapeType = _shapeType,
                    PointCount = (byte)_shapePointCount,
                    Closed = (byte)(_shapeClosed ? 1 : 0), Active = 1,
                    Thickness = ScaledThickness()
                };
                for (int i = 0; i < _shapePointCount; i++)
                {
                    Vector3 world = transform.TransformPoint(_shapeLocalPoints[i]);
                    shape.SetPoint(i, new Vector2(world.x, world.y));
                }
                system.RegisterShape2D(in shape, gameObject);
            }
        }

        /// <summary>
        /// Thickness was baked at detection time in the source collider's own
        /// local units — re-scale it by this transform's current lossyScale
        /// each call so it stays correct even if the object is scaled after
        /// Start() (or scaled dynamically at runtime).
        /// </summary>
        private float ScaledThickness()
        {
            if (_shapeThickness <= 0f) return 0f;
            var s = transform.lossyScale;
            return _shapeThickness * (_is3D ? Mathf.Max(s.x, s.y, s.z) : Mathf.Max(s.x, s.y));
        }

        private void Deactivate()
        {
            if (!_hasRegisteredOnce) return;

            var system = MID_MasterProjectileSystem.HasInstance
                ? MID_MasterProjectileSystem.Instance : null;
            if (system == null) return;

            if (_isShape)
            {
                if (_is3D) system.DeactivateShape3D(_targetId);
                else       system.DeactivateShape2D(_targetId);
            }
            else
            {
                if (_is3D) system.DeactivateTarget3D(_targetId);
                else       system.DeactivateTarget2D(_targetId);
            }
        }

        private uint ResolveTargetId()
        {
            if (_explicitTargetId != 0) return _explicitTargetId;

            var netObj = GetComponent<Unity.Netcode.NetworkObject>();
            if (netObj != null) return (uint)netObj.NetworkObjectId;

            return unchecked((uint)GetInstanceID());
        }

        private void DetectShape()
        {
            if (!_forceCircleOrSphere)
            {
                if (_is3D) { if (DetectShape3D()) return; }
                else       { if (DetectShape2D()) return; }
            }

            // Plain circle/sphere path — forced, or nothing else matched.
            _isShape = false;
            if (_radius <= 0f) _radius = AutoDetectCircleOrSphereRadius();
        }

        /// <returns>true if a shape was detected and baked (registration should
        /// use the shape path); false to fall through to circle/sphere.</returns>
        private bool DetectShape2D()
        {
            // CircleCollider2D takes priority even under force-off logic being
            // absent — it's cheaper and was the original, well-tested path, so
            // an object with both a CircleCollider2D and e.g. a BoxCollider2D
            // (for an unrelated gameplay purpose) keeps behaving exactly as it
            // did before shape detection existed.
            var circle = GetComponentInChildren<CircleCollider2D>();
            if (circle != null) return false; // handled by the circle fallback below

            var box = GetComponentInChildren<BoxCollider2D>();
            if (box != null) { BakeBox2D(box); return true; }

            var capsule = GetComponentInChildren<CapsuleCollider2D>();
            if (capsule != null) { BakeCapsule2D(capsule); return true; }

            var edge = GetComponentInChildren<EdgeCollider2D>();
            if (edge != null) { BakeEdge2D(edge); return true; }

            var poly = GetComponentInChildren<PolygonCollider2D>();
            if (poly != null) { BakePolygon2D(poly); return true; }

            return false;
        }

        private bool DetectShape3D()
        {
            var sphere = GetComponentInChildren<SphereCollider>();
            if (sphere != null) return false;

            var capsule = GetComponentInChildren<CapsuleCollider>();
            if (capsule != null) { BakeCapsule3D(capsule); return true; }

            // BOX APPROXIMATION — see the file header's "3D BOX LIMITATION"
            // note. Not exact; use RustSimCustomShapeAuthoring instead if you
            // need this box represented precisely.
            var box = GetComponentInChildren<BoxCollider>();
            if (box != null) { BakeBoxApprox3D(box); return true; }

            return false;
        }

        // ── 2D bakers ────────────────────────────────────────────────────────

        private void BakeBox2D(BoxCollider2D box)
        {
            Vector2 half = box.size * 0.5f;
            Vector2 c    = box.offset;
            Vector2[] corners =
            {
                c + new Vector2(-half.x, -half.y), c + new Vector2( half.x, -half.y),
                c + new Vector2( half.x,  half.y), c + new Vector2(-half.x,  half.y),
            };
            SetShapeFromLocal2D(box.transform, corners, closed: true,
                thickness: 0f, ShapeColliderType.Box);
        }

        private void BakeCapsule2D(CapsuleCollider2D cap)
        {
            Vector2 half = cap.size * 0.5f;
            bool vertical = cap.direction == CapsuleDirection2D.Vertical;
            float halfLength = vertical ? half.y : half.x;
            float radius     = vertical ? half.x : half.y;
            Vector2 axis     = vertical ? Vector2.up : Vector2.right;
            float segHalf    = Mathf.Max(0f, halfLength - radius);

            Vector2[] pts = { cap.offset + axis * segHalf, cap.offset - axis * segHalf };
            SetShapeFromLocal2D(cap.transform, pts, closed: false,
                thickness: radius, ShapeColliderType.Capsule);
        }

        private void BakeEdge2D(EdgeCollider2D edge)
        {
            Vector2[] pts = edge.points;
            if (pts.Length > ShapeCollider2D.MaxPoints)
                pts = ResamplePolyline(pts, ShapeCollider2D.MaxPoints, closed: false);
            SetShapeFromLocal2D(edge.transform, pts, closed: false,
                thickness: edge.edgeRadius, ShapeColliderType.Edge);
        }

        private void BakePolygon2D(PolygonCollider2D poly)
        {
            // Path 0 only — a PolygonCollider2D can have multiple disjoint
            // paths (e.g. a shape with a hole), which this single-loop shape
            // format can't represent. Use RustSimCustomShapeAuthoring for
            // anything that needs more than one path.
            Vector2[] pts = poly.GetPath(0);
            if (pts.Length > ShapeCollider2D.MaxPoints)
                pts = ResamplePolyline(pts, ShapeCollider2D.MaxPoints, closed: true);
            SetShapeFromLocal2D(poly.transform, pts, closed: true,
                thickness: 0f, ShapeColliderType.Polygon);
        }

        /// <summary>
        /// Converts collider-local 2D points (possibly on a child transform)
        /// into world space via that collider's own transform, then into
        /// THIS transform's local space via InverseTransformPoint — see the
        /// _shapeLocalPoints field doc for why. Common exit point for every
        /// 2D baker above.
        /// </summary>
        private void SetShapeFromLocal2D(
            Transform colliderTransform, Vector2[] localPts,
            bool closed, float thickness, ShapeColliderType type)
        {
            int n = Mathf.Min(localPts.Length, ShapeCollider2D.MaxPoints);
            for (int i = 0; i < n; i++)
            {
                Vector3 world = colliderTransform.TransformPoint(new Vector3(localPts[i].x, localPts[i].y, 0f));
                _shapeLocalPoints[i] = transform.InverseTransformPoint(world);
            }
            _shapePointCount = n;
            _shapeClosed     = closed;
            _shapeThickness  = thickness;
            _shapeType       = (byte)type;
            _isShape         = true;
        }

        // ── 3D bakers ────────────────────────────────────────────────────────

        private void BakeCapsule3D(CapsuleCollider cap)
        {
            float radius     = cap.radius;
            float halfLength = Mathf.Max(0f, cap.height * 0.5f - radius);
            Vector3 axis = cap.direction switch
            {
                0 => Vector3.right, 1 => Vector3.up, _ => Vector3.forward
            };
            Vector3[] pts = { cap.center + axis * halfLength, cap.center - axis * halfLength };
            SetShapeFromLocal3D(cap.transform, pts, closed: false,
                thickness: radius, ShapeColliderType.Capsule);
        }

        /// <summary>
        /// APPROXIMATION — see the file header's "3D BOX LIMITATION" note.
        /// Longest local half-extent becomes the capsule's spine axis; the
        /// other two half-extents are averaged into a single thickness.
        /// Exact for a cube-ish box only by coincidence; a long thin box
        /// (a plank, a wall segment) is where this diverges most from the
        /// real shape — use RustSimCustomShapeAuthoring for that case.
        /// </summary>
        private void BakeBoxApprox3D(BoxCollider box)
        {
            Vector3 half = box.size * 0.5f;
            int longest = 0;
            if (half.y > half[longest]) longest = 1;
            if (half.z > half[longest]) longest = 2;

            Vector3 axis = longest switch
            {
                0 => Vector3.right, 1 => Vector3.up, _ => Vector3.forward
            };
            float spineHalf = half[longest];
            float thickness = longest == 0 ? (half.y + half.z) * 0.5f
                             : longest == 1 ? (half.x + half.z) * 0.5f
                                            : (half.x + half.y) * 0.5f;

            Vector3[] pts = { box.center + axis * spineHalf, box.center - axis * spineHalf };
            SetShapeFromLocal3D(box.transform, pts, closed: false, thickness, ShapeColliderType.Box);
        }

        private void SetShapeFromLocal3D(
            Transform colliderTransform, Vector3[] localPts,
            bool closed, float thickness, ShapeColliderType type)
        {
            int n = Mathf.Min(localPts.Length, ShapeCollider3D.MaxPoints);
            for (int i = 0; i < n; i++)
            {
                Vector3 world = colliderTransform.TransformPoint(localPts[i]);
                _shapeLocalPoints[i] = transform.InverseTransformPoint(world);
            }
            _shapePointCount = n;
            _shapeClosed     = closed;
            _shapeThickness  = thickness;
            _shapeType       = (byte)type;
            _isShape         = true;
        }

        /// <summary>
        /// Arc-length resampling — walks the polyline's cumulative length and
        /// picks evenly-spaced points along it, rather than just truncating
        /// (which would silently drop whatever detail happened to sit past
        /// index targetCount, often the most important part of the shape).
        /// Used when a Unity EdgeCollider2D/PolygonCollider2D has more raw
        /// points than ShapeCollider2D.MaxPoints allows.
        /// </summary>
        private static Vector2[] ResamplePolyline(Vector2[] src, int targetCount, bool closed)
        {
            int srcCount = closed ? src.Length + 1 : src.Length; // +1 to include the closing segment
            var cum = new float[srcCount];
            cum[0] = 0f;
            for (int i = 1; i < srcCount; i++)
            {
                Vector2 a = src[(i - 1) % src.Length];
                Vector2 b = src[i % src.Length];
                cum[i] = cum[i - 1] + Vector2.Distance(a, b);
            }
            float total = cum[srcCount - 1];

            var result = new Vector2[targetCount];
            if (total <= 1e-6f)
            {
                for (int i = 0; i < targetCount; i++) result[i] = src[Mathf.Min(i, src.Length - 1)];
                return result;
            }

            for (int i = 0; i < targetCount; i++)
            {
                float target = total * i / (targetCount - 1);
                int seg = 0;
                while (seg < srcCount - 2 && cum[seg + 1] < target) seg++;
                float segLen = cum[seg + 1] - cum[seg];
                float t = segLen > 1e-6f ? (target - cum[seg]) / segLen : 0f;
                Vector2 a = src[seg % src.Length];
                Vector2 b = src[(seg + 1) % src.Length];
                result[i] = Vector2.Lerp(a, b, t);
            }
            return result;
        }

        private float AutoDetectCircleOrSphereRadius()
        {
            if (_is3D)
            {
                var sphere = GetComponentInChildren<SphereCollider>();
                if (sphere != null)
                {
                    var scale = transform.lossyScale;
                    return sphere.radius * Mathf.Max(scale.x, scale.y, scale.z);
                }
            }
            else
            {
                var circle = GetComponentInChildren<CircleCollider2D>();
                if (circle != null)
                {
                    var scale = transform.lossyScale;
                    return circle.radius * Mathf.Max(scale.x, scale.y);
                }
            }

            // No matching collider found — a small, harmless default rather
            // than a zero radius (which would never register a hit at all).
            return 0.5f;
        }

        #endregion
    }
}
