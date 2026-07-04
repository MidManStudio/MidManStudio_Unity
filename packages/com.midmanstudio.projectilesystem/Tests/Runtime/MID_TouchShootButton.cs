// On-screen shoot button. Reports Pressed/Released via events and IsPressed,
// using pointer down/up directly (not Button.onClick) so a held press can drive
// continuous/automatic fire from a controller script if you want that later.

using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace MidManStudio.Projectiles.MobileControls
{
    public class MID_TouchShootButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public event Action Pressed;
        public event Action Released;

        public bool IsPressed { get; private set; }

        public void OnPointerDown(PointerEventData e)
        {
            IsPressed = true;
            Pressed?.Invoke();
        }

        public void OnPointerUp(PointerEventData e)
        {
            IsPressed = false;
            Released?.Invoke();
        }
    }
}
