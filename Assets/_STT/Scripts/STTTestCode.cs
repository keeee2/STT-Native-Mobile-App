using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// STT 테스트 UI - 두 가지 모드 지원
///
/// 1. Streaming: VAD 기반, 목소리 감지 → 침묵 감지 시 자동 전송 → 다시 대기
/// 2. Recording: 버튼 누르면 녹음, 다시 누르면 전체를 STT 변환
/// </summary>
public class STTTestCode : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private Button streamingButton;
    [SerializeField] private Button recordButton;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text resultText;

    [Header("설정")]
    [SerializeField] private string languageCode = "ko-KR";

    [Header("성능")]
    [Tooltip("스트리밍 Partial 결과가 너무 자주 들어올 때 UI 갱신을 줄입니다(초)")]
    [SerializeField] private float uiUpdateThrottleSec = 0.05f;

    [Tooltip("Desktop/Editor에서 STT 초기화 대기 타임아웃(초)")]
    [SerializeField] private float initTimeoutSec = 60f;

    // 상태
    private bool isStreamingMode = false;
    private bool isRecording = false;
    private bool hasPermission = false;
    private bool isInitialized = false;

    // 텍스트
    private string accumulatedText = "";
    private string currentPartial = "";

    private float _lastUiUpdateTime = -999f;
    private Coroutine _waitInitCo;

    private void Start()
    {
        if (streamingButton != null) streamingButton.interactable = false;
        if (recordButton != null) recordButton.interactable = false;
        if (statusText != null) statusText.text = "초기화 중...";

        SubscribeEvents();

        if (streamingButton != null) streamingButton.onClick.AddListener(ToggleStreaming);
        if (recordButton != null) recordButton.onClick.AddListener(ToggleRecording);

        InitializePlatform();
    }

    private void SubscribeEvents()
    {
        var stt = STTManager.Instance;
        if (stt == null)
        {
            if (statusText != null) statusText.text = "STTManager 없음!";
            return;
        }

        // 람다 금지: 해제 불가
        stt.OnStarted += HandleStarted;
        stt.OnReady += HandleReady;
        stt.OnStopped += HandleStopped;
        stt.OnPartialResult += HandlePartial;
        stt.OnResult += HandleFinal;
        stt.OnError += HandleError;
        stt.OnPermissionGranted += OnPermissionGranted;
        stt.OnPermissionDenied += OnPermissionDenied;
    }

    private void UnsubscribeEvents()
    {
        var stt = STTManager.Instance;
        if (stt == null) return;

        stt.OnStarted -= HandleStarted;
        stt.OnReady -= HandleReady;
        stt.OnStopped -= HandleStopped;
        stt.OnPartialResult -= HandlePartial;
        stt.OnResult -= HandleFinal;
        stt.OnError -= HandleError;
        stt.OnPermissionGranted -= OnPermissionGranted;
        stt.OnPermissionDenied -= OnPermissionDenied;
    }

    private void InitializePlatform()
    {
#if UNITY_IOS && !UNITY_EDITOR
        if (statusText != null) statusText.text = "권한 요청 중...";
        STTManager.Instance.RequestPermission();
#elif UNITY_ANDROID && !UNITY_EDITOR
        if (statusText != null) statusText.text = "권한 요청 중...";
        PermissionHelper.RequestMicrophonePermission(
            onGranted: OnPermissionGranted,
            onDenied: () => OnPermissionDenied("마이크 권한 거부")
        );
#else
        // Editor/Standalone - Whisper 초기화 대기
        _waitInitCo = StartCoroutine(WaitForInitialization());
#endif
    }

    private IEnumerator WaitForInitialization()
    {
        if (statusText != null) statusText.text = "Whisper 로딩 중...";

        float start = Time.realtimeSinceStartup;
        while (STTManager.Instance == null || !STTManager.Instance.IsInitialized)
        {
            if (initTimeoutSec > 0f && Time.realtimeSinceStartup - start > initTimeoutSec)
            {
                if (statusText != null) statusText.text = "초기화 타임아웃(모델/GPU 설정 확인)";
                yield break;
            }

            yield return new WaitForSeconds(0.1f);
        }

        OnPermissionGranted();
    }

    private void OnPermissionGranted()
    {
        hasPermission = true;
        isInitialized = true;
        if (statusText != null) statusText.text = "✅ 준비 완료";
        if (streamingButton != null) streamingButton.interactable = true;
        if (recordButton != null) recordButton.interactable = true;

        UpdateButtonTexts();
    }

    private void OnPermissionDenied(string reason)
    {
        hasPermission = false;
        if (statusText != null) statusText.text = $"❌ 권한 거부: {reason}";

        if (streamingButton != null) streamingButton.interactable = false;
        if (recordButton != null) recordButton.interactable = false;
    }

    // ===== STT 이벤트 핸들러 =====

    private void HandleStarted()
    {
        if (statusText == null) return;
        statusText.text = isStreamingMode ? "🎙️ 스트리밍 활성화" : "🔴 녹음 중...";
    }

    private void HandleReady()
    {
        if (!isStreamingMode || statusText == null) return;
        statusText.text = "🎙️ 대기 중... (말씀하세요)";
    }

    private void HandleStopped()
    {
        if (statusText == null) return;
        if (!isStreamingMode && !isRecording)
            statusText.text = "녹음 완료";
    }

    private void HandlePartial(string text)
    {
        currentPartial = text;
        UpdateResultText();

        if (isStreamingMode && statusText != null)
            statusText.text = "🎙️ 듣는 중...";
    }

    private void HandleFinal(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (!string.IsNullOrEmpty(accumulatedText))
            accumulatedText += "\n";
        accumulatedText += text;

        currentPartial = "";
        UpdateResultText(force: true);

        Debug.Log($"[STT] 결과: {text}");

        if (isStreamingMode && statusText != null)
            statusText.text = "🎙️ 대기 중... (말씀하세요)";
    }

    private void HandleError(string error)
    {
        if (statusText != null) statusText.text = $"오류: {error}";
        Debug.LogWarning($"[STT] 오류: {error}");
    }

    /// <summary>
    /// Streaming 모드 토글
    /// - ON: VAD 기반 자동 감지/전송 시작
    /// - OFF: 스트리밍 완전 종료
    /// </summary>
    private void ToggleStreaming()
    {
        if (!hasPermission || !isInitialized)
        {
            if (statusText != null) statusText.text = "초기화 필요";
            return;
        }

        if (!isStreamingMode)
        {
            isStreamingMode = true;
            accumulatedText = "";
            currentPartial = "";
            if (resultText != null) resultText.text = "";

            if (recordButton != null) recordButton.interactable = false;

            STTManager.Instance.StartListening(languageCode);
            if (statusText != null) statusText.text = "🎙️ 스트리밍 시작...";
        }
        else
        {
            isStreamingMode = false;
            STTManager.Instance.StopListening();

            if (recordButton != null) recordButton.interactable = true;
            if (statusText != null) statusText.text = "스트리밍 종료";
        }

        UpdateButtonTexts();
    }

    /// <summary>
    /// Recording 모드 토글
    /// - 시작: 녹음 시작 (버튼 누를 때까지 계속)
    /// - 종료: 녹음 종료 후 전체 STT 변환
    /// </summary>
    private void ToggleRecording()
    {
        if (!hasPermission || !isInitialized)
        {
            if (statusText != null) statusText.text = "초기화 필요";
            return;
        }

        if (!isRecording)
        {
            isRecording = true;
            accumulatedText = "";
            currentPartial = "";
            if (resultText != null) resultText.text = "";

            if (streamingButton != null) streamingButton.interactable = false;

            STTManager.Instance.StartListening(languageCode);
            if (statusText != null) statusText.text = "🔴 녹음 중... (버튼을 눌러 종료)";
        }
        else
        {
            isRecording = false;
            STTManager.Instance.StopListening();

            if (streamingButton != null) streamingButton.interactable = true;
            if (statusText != null) statusText.text = "변환 완료";

            Debug.Log($"[STT] 최종 결과: {accumulatedText}");
        }

        UpdateButtonTexts();
    }

    private void UpdateButtonTexts()
    {
        if (streamingButton != null)
        {
            var streamingText = streamingButton.GetComponentInChildren<TMP_Text>();
            if (streamingText != null)
                streamingText.text = isStreamingMode ? "⏹ 스트리밍 중지" : "🎙️ 스트리밍";
        }

        if (recordButton != null)
        {
            var recordText = recordButton.GetComponentInChildren<TMP_Text>();
            if (recordText != null)
                recordText.text = isRecording ? "⏹ 녹음 종료" : "⏺ 녹음";
        }
    }

    private void UpdateResultText(bool force = false)
    {
        if (resultText == null) return;

        if (!force && uiUpdateThrottleSec > 0f)
        {
            if (Time.realtimeSinceStartup - _lastUiUpdateTime < uiUpdateThrottleSec)
                return;
        }

        string display = accumulatedText;

        if (!string.IsNullOrEmpty(currentPartial))
        {
            if (!string.IsNullOrEmpty(display))
                display += "\n";
            display += $"<color=#888888>{currentPartial}</color>";
        }

        resultText.text = display;
        _lastUiUpdateTime = Time.realtimeSinceStartup;
    }

    private void OnDestroy()
    {
        if (_waitInitCo != null)
        {
            StopCoroutine(_waitInitCo);
            _waitInitCo = null;
        }

        if (STTManager.Instance != null)
        {
            if (isStreamingMode || isRecording)
                STTManager.Instance.StopListening();
        }

        UnsubscribeEvents();

        if (streamingButton != null) streamingButton.onClick.RemoveListener(ToggleStreaming);
        if (recordButton != null) recordButton.onClick.RemoveListener(ToggleRecording);
    }
}