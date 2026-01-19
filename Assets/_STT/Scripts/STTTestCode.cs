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

    // 상태
    private bool isStreamingMode = false;
    private bool isRecording = false;
    private bool hasPermission = false;
    private bool isInitialized = false;

    // 텍스트
    private string accumulatedText = "";
    private string currentPartial = "";

    private void Start()
    {
        // 버튼 초기 비활성화
        streamingButton.interactable = false;
        recordButton.interactable = false;
        statusText.text = "초기화 중...";

        // STTManager 이벤트 구독
        SubscribeEvents();

        // 버튼 클릭 이벤트
        streamingButton.onClick.AddListener(ToggleStreaming);
        recordButton.onClick.AddListener(ToggleRecording);

        // 플랫폼별 초기화
        InitializePlatform();
    }

    private void SubscribeEvents()
    {
        var stt = STTManager.Instance;
        if (stt == null)
        {
            statusText.text = "STTManager 없음!";
            return;
        }

        stt.OnStarted += () => { statusText.text = isStreamingMode ? "🎙️ 스트리밍 활성화" : "🔴 녹음 중..."; };

        stt.OnReady += () =>
        {
            if (isStreamingMode)
                statusText.text = "🎙️ 대기 중... (말씀하세요)";
        };

        stt.OnStopped += () =>
        {
            if (!isStreamingMode && !isRecording)
                statusText.text = "녹음 완료";
        };

        stt.OnPartialResult += text =>
        {
            currentPartial = text;
            UpdateResultText();

            if (isStreamingMode)
                statusText.text = "🎙️ 듣는 중...";
        };

        stt.OnResult += text =>
        {
            if (!string.IsNullOrEmpty(text))
            {
                // 누적
                if (!string.IsNullOrEmpty(accumulatedText))
                    accumulatedText += "\n";
                accumulatedText += text;
                currentPartial = "";
                UpdateResultText();

                Debug.Log($"[STT] 결과: {text}");

                if (isStreamingMode)
                    statusText.text = "🎙️ 대기 중... (말씀하세요)";
            }
        };

        stt.OnError += error =>
        {
            statusText.text = $"오류: {error}";
            Debug.LogWarning($"[STT] 오류: {error}");
        };

        stt.OnPermissionGranted += OnPermissionGranted;
        stt.OnPermissionDenied += OnPermissionDenied;
    }

    private void InitializePlatform()
    {
#if UNITY_IOS && !UNITY_EDITOR
        statusText.text = "권한 요청 중...";
        STTManager.Instance.RequestPermission();

#elif UNITY_ANDROID && !UNITY_EDITOR
        statusText.text = "권한 요청 중...";
        PermissionHelper.RequestMicrophonePermission(
            onGranted: OnPermissionGranted,
            onDenied: () => OnPermissionDenied("마이크 권한 거부")
        );
#else
        // Editor/Standalone - Whisper 초기화 대기
        StartCoroutine(WaitForInitialization());
#endif
    }

    private System.Collections.IEnumerator WaitForInitialization()
    {
        statusText.text = "Whisper 로딩 중...";

        // STTManager 초기화 대기
        while (STTManager.Instance == null || !STTManager.Instance.IsInitialized)
        {
            yield return new WaitForSeconds(0.1f);
        }

        OnPermissionGranted();
    }

    private void OnPermissionGranted()
    {
        hasPermission = true;
        isInitialized = true;
        statusText.text = "✅ 준비 완료";
        streamingButton.interactable = true;
        recordButton.interactable = true;

        UpdateButtonTexts();
    }

    private void OnPermissionDenied(string reason)
    {
        hasPermission = false;
        statusText.text = $"❌ 권한 거부: {reason}";
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
            statusText.text = "초기화 필요";
            return;
        }

        if (!isStreamingMode)
        {
            // Streaming 시작
            isStreamingMode = true;
            accumulatedText = "";
            currentPartial = "";
            resultText.text = "";

            // Recording 버튼 비활성화
            recordButton.interactable = false;

            STTManager.Instance.StartListening(languageCode);
            statusText.text = "🎙️ 스트리밍 시작...";
        }
        else
        {
            // Streaming 종료
            isStreamingMode = false;
            STTManager.Instance.StopListening();

            // Recording 버튼 활성화
            recordButton.interactable = true;
            statusText.text = "스트리밍 종료";
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
            statusText.text = "초기화 필요";
            return;
        }

        if (!isRecording)
        {
            // Recording 시작
            isRecording = true;
            accumulatedText = "";
            currentPartial = "";
            resultText.text = "";

            // Streaming 버튼 비활성화
            streamingButton.interactable = false;

            STTManager.Instance.StartListening(languageCode);
            statusText.text = "🔴 녹음 중... (버튼을 눌러 종료)";
        }
        else
        {
            // Recording 종료 & STT 변환
            isRecording = false;
            STTManager.Instance.StopListening();

            // Streaming 버튼 활성화
            streamingButton.interactable = true;
            statusText.text = "변환 완료";

            Debug.Log($"[STT] 최종 결과: {accumulatedText}");
        }

        UpdateButtonTexts();
    }

    private void UpdateButtonTexts()
    {
        var streamingText = streamingButton.GetComponentInChildren<TMP_Text>();
        var recordText = recordButton.GetComponentInChildren<TMP_Text>();

        if (streamingText != null)
            streamingText.text = isStreamingMode ? "⏹ 스트리밍 중지" : "🎙️ 스트리밍";

        if (recordText != null)
            recordText.text = isRecording ? "⏹ 녹음 종료" : "⏺ 녹음";
    }

    private void UpdateResultText()
    {
        string display = accumulatedText;

        if (!string.IsNullOrEmpty(currentPartial))
        {
            if (!string.IsNullOrEmpty(display))
                display += "\n";
            display += $"<color=#888888>{currentPartial}</color>";
        }

        resultText.text = display;
    }

    private void OnDestroy()
    {
        if (STTManager.Instance != null)
        {
            // 진행 중인 작업 중지
            if (isStreamingMode || isRecording)
            {
                STTManager.Instance.StopListening();
            }
        }
    }
}