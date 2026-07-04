// On-screen virtual joystick. Attach to a UI Image acting as the joystick's
// background (a translucent circle works well); assign a child Image as the
// Handle. Drag anywhere inside the background — the handle follows, clamped to
// the background's radius, and Value reports the offset normalized to -1..1.

using UnityEngine;
using UnityEngine.EventSystems;

namespace MidManStudio.Projectiles.MobileControls
{
    [RequireComponent(typeof(RectTransform))]
    public class MID_TouchJoystick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        [SerializeField] private RectTransform _handle;

        [Tooltip("How far the handle can travel from center, in local units of the background rect.")]
        [SerializeField] private float _handleRange = 60f;

        [Tooltip("Below this magnitude, Value snaps to zero — filters accidental micro-drags.")]
        [SerializeField] [Range(0f, 0.5f)] private float _deadZone = 0.1f;

        private RectTransform _background;
        private Vector2 _value;

        public Vector2 Value  => _value;
        public bool     IsHeld { get; private set; }

        private void Awake() => _background = (RectTransform)transform;

        public void OnPointerDown(PointerEventData e) => OnDrag(e);

        public void OnDrag(PointerEventData e)
        {
            IsHeld = true;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _background, e.position, e.pressEventCamera, out Vector2 local);

            Vector2 clamped = Vector2.ClampMagnitude(local, _handleRange);
            if (_handle != null) _handle.anchoredPosition = clamped;

            Vector2 normalized = clamped / _handleRange;
            _value = normalized.magnitude < _deadZone ? Vector2.zero : normalized;
        }

        public void OnPointerUp(PointerEventData e)
        {
            IsHeld = false;
            _value = Vector2.zero;
            if (_handle != null) _handle.anchoredPosition = Vector2.zero;
        }
    }
}
