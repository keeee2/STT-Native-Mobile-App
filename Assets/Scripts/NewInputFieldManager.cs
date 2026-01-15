using UnityEngine;
using UMI;

public class NewInputFieldManager : MonoBehaviour
{
    void Awake()
    {
        MobileInput.Init();
        MobileInput.OnKeyboardAction += OnKeyboardAction;
        MobileInput.OnOrientationChange += OnOrientationChange;
    }

    void OnOrientationChange(HardwareOrientation orientation)
    {
        // raise when the screen orientation is changed
        Debug.Log(orientation);
    }

    void OnKeyboardAction(bool isShow, int height)
    {
        // raise when the keyboard is displayed or hidden, and when the keyboard height is changed
        Debug.Log($"isShow: {isShow}, height: {height}");
    }
}