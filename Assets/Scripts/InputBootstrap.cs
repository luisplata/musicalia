using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;

public class InputBootstrap : MonoBehaviour
{
    private void Awake()
    {
        EnhancedTouchSupport.Enable();
    }
}