using UnityEngine;
using UMI;

/// <summary>
/// 모바일 키보드 표시/숨김에 따라 UI 패널 위치를 보정
/// </summary>
public class NewInputFieldManager : MonoBehaviour
{
    [SerializeField] private Canvas canvas;
    [SerializeField] private RectTransform panelToMove;

    [Header("Debug")]
    [SerializeField] private bool logKeyboardEvents = false;
    [SerializeField] private bool logOrientationEvents = false;

    private Vector2 _basePos;
    private bool _subscribed;

    private static bool s_inited;

    private void Awake()
    {
        if (panelToMove == null)
        {
            Debug.LogError("[NewInputFieldManager] panelToMove가 null입니다.");
            enabled = false;
            return;
        }

        _basePos = panelToMove.anchoredPosition;

        if (!s_inited)
        {
            MobileInput.Init();
            s_inited = true;
        }

        Subscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;

        MobileInput.OnKeyboardAction += OnKeyboardAction;
        MobileInput.OnOrientationChange += OnOrientationChange;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;

        MobileInput.OnKeyboardAction -= OnKeyboardAction;
        MobileInput.OnOrientationChange -= OnOrientationChange;
        _subscribed = false;
    }

    private void OnKeyboardAction(bool isShow, int heightPx)
    {
        // 키보드 표시/숨김 + 높이 변경
        float uiOffset = (canvas != null ? heightPx / canvas.scaleFactor : heightPx);

        panelToMove.anchoredPosition = isShow
            ? _basePos + Vector2.up * uiOffset
            : _basePos;

        if (logKeyboardEvents)
        {
            Debug.Log($"[NewInputFieldManager] Keyboard isShow={isShow}, heightPx={heightPx}, uiOffset={uiOffset}");
        }
    }

    private void OnOrientationChange(HardwareOrientation orientation)
    {
        if (logOrientationEvents)
        {
            Debug.Log($"[NewInputFieldManager] Orientation={orientation}");
        }
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }
}