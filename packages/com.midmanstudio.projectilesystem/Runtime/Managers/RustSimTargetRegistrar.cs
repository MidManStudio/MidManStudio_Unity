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
        [Tooltip("Hit radius reported to RustSim's collision check. Leave at 0 " +
                 "to auto-detect from a CircleCollider2D/SphereCollider on this " +
                 "object or its children at Start — set explicitly to override " +
                 "or if no such collider exists.")]
        [SerializeField] private float _radius = 0f;

        [Header("Target Id")]
        [Tooltip("Leave at 0 to auto-derive: this object's NetworkObjectId if it " +
                 "has a NetworkObject, otherwise a locally-generated id from " +
                 "GetInstanceID(). Only set this explicitly if you need a " +
                 "specific, predictable id (e.g. to reference from elsewhere).")]
        [SerializeField] private uint _explicitTargetId = 0;

        [Header("Update Rate")]
        [Tooltip("FixedUpdate matches physics tick rate — the usual choice for " +
                 "anything moving via Rigidbody. Switch to Update if this " +
                 "target's position changes outside FixedUpdate.")]
        [SerializeField] private bool _useFixedUpdate = true;

        [Tooltip("Re-register (refresh position) every N ticks instead of every " +
                 "single one — see the file header's performance note. 1 = every tick.")]
        [SerializeField, Min(1)] private int _updateEveryNTicks = 1;

        #endregion

        #region State

        private uint _targetId;
        private int  _tickCounter;
        private bool _hasRegisteredOnce;

        #endregion

        #region Lifecycle

        private void Start()
        {
            _targetId = ResolveTargetId();

            if (_radius <= 0f)
                _radius = AutoDetectRadius();

            RegisterNow();
        }

        private void OnEnable()
        {
            // Re-registers on re-enable (e.g. object pooled and reactivated) —
            // harmless no-op the very first time, since Start() above already
            // registers before OnEnable would meaningfully differ from it.
            if (_hasRegisteredOnce) RegisterNow();
        }

        private void OnDisable() => Deactivate();
        private void OnDestroy() => Deactivate();

        private void Update()
        {
            if (_useFixedUpdate) return;
            Tick();
        }

        private void FixedUpdate()
        {
            if (!_useFixedUpdate) return;
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

        private void RegisterNow()
        {
            var system = MID_MasterProjectileSystem.HasInstance
                ? MID_MasterProjectileSystem.Instance : null;
            if (system == null) return;

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

            _hasRegisteredOnce = true;
        }

        private void Deactivate()
        {
            if (!_hasRegisteredOnce) return;

            var system = MID_MasterProjectileSystem.HasInstance
                ? MID_MasterProjectileSystem.Instance : null;
            if (system != null)
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

        private float AutoDetectRadius()
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
