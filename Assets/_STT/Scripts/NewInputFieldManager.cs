using System;
using UnityEngine;
using UMI;

public class NewInputFieldManager : MonoBehaviour
{
    [SerializeField] private Canvas canvas;
    [SerializeField] private RectTransform panelToMove;

    private Vector2 _basePos;

    private void Awake()
    {
        _basePos = panelToMove.anchoredPosition;
        MobileInput.Init();
        MobileInput.OnKeyboardAction += OnKeyboardAction;
        MobileInput.OnOrientationChange += OnOrientationChange;
        MobileInput.OnKeyboardAction += OnKeyboard;
    }

    private void OnDestroy()
    {
        MobileInput.OnKeyboardAction -= OnKeyboard;
    }

    private void OnKeyboard(bool isShow, int heightPx)
    {
        float uiOffset = (canvas != null ? heightPx / canvas.scaleFactor : heightPx);

        panelToMove.anchoredPosition = isShow
            ? _basePos + Vector2.up * uiOffset
            : _basePos;
    }

    private void OnOrientationChange(HardwareOrientation orientation)
    {
        // raise when the screen orientation is changed
        Debug.Log(orientation);
    }

    private void OnKeyboardAction(bool isShow, int height)
    {
        // raise when the keyboard is displayed or hidden, and when the keyboard height is changed
        Debug.Log($"isShow: {isShow}, height: {height}");
    }
}