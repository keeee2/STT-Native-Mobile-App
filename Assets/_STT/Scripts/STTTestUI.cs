using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// STT 테스트 UI
/// - 모든 플랫폼(Android/iOS/Windows/MacOS/Editor)에서 동일하게 동작
/// - 현재 사용 중인 백엔드 표시
/// </summary>
public class STTTestUI : MonoBehaviour
{
    [Header("UI 요소")]
    [SerializeField] private Button recordButton;
    [SerializeField] private TMP_Text buttonText;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text resultText;
    [SerializeField] private TMP_Text backendText;

    [Header("설정")]
    [SerializeField] private string languageCode = "ko-KR";
    [SerializeField] private bool showDebugInfo = true;

    private bool isRecording = false;
    private bool hasPermission = false;
    private string accumulatedText = "";
    private string currentPartial = "";

    private void Start()
    {
        SetupUI();
        SetupEventListeners();
        InitializePlatform();
    }

    private void SetupUI()
    {
        if (buttonText == null && recordButton != null)
        {
            buttonText = recordButton.GetComponentInChildren<TMP_Text>();
        }

        UpdateBackendInfo();
    }

    private void SetupEventListeners()
    {
        if (STTManager.Instance == null)
        {
            Debug.LogError("[STTTestUI] STTManager.Instance가 없음. 씬에 STTManager 게임오브젝트를 추가하세요.");
            if (statusText != null)
                statusText.text = "STTManager 없음";
            return;
        }

        // 이벤트 구독
        STTManager.Instance.OnStarted += () => SetStatus("녹음 시작됨");
        STTManager.Instance.OnReady += () => SetStatus("말씀하세요...");
        STTManager.Instance.OnStopped += () => SetStatus("녹음 중지됨");

        STTManager.Instance.OnPartialResult += OnPartialResult;
        STTManager.Instance.OnResult += OnFinalResult;
        STTManager.Instance.OnError += OnError;

        // iOS/Android 권한 콜백
        STTManager.Instance.OnPermissionGranted += OnPermissionGranted;
        STTManager.Instance.OnPermissionDenied += OnPermissionDenied;

        // 버튼 클릭
        if (recordButton != null)
        {
            recordButton.onClick.AddListener(ToggleRecording);
        }
    }

    private void UpdateBackendInfo()
    {
        if (backendText == null) return;

        if (STTManager.Instance != null)
        {
            string backend = STTManager.Instance.CurrentBackend switch
            {
                STTManager.STTBackend.AndroidNative => "Android Native STT",
                STTManager.STTBackend.iOSNative => "iOS Speech Framework",
                STTManager.STTBackend.Whisper => "Standalone STT",
                _ => "초기화 중..."
            };

            backendText.text = $"백엔드: {backend}";

            if (showDebugInfo)
            {
                backendText.text += $"\n플랫폼: {Application.platform}";
                backendText.text += $"\n초기화: {(STTManager.Instance.IsInitialized ? "완료" : "실패")}";
            }
        }
        else
        {
            backendText.text = "STTManager 없음";
        }
    }

    private void InitializePlatform()
    {
        if (STTManager.Instance == null)
        {
            recordButton.interactable = false;
            return;
        }

#if UNITY_IOS && !UNITY_EDITOR
        // iOS: STTManager 통해 권한 요청
        recordButton.interactable = false;
        SetStatus("권한 요청 중...");
        STTManager.Instance.RequestPermission();

#elif UNITY_ANDROID && !UNITY_EDITOR
        // Android: PermissionHelper로 런타임 권한 요청
        recordButton.interactable = false;
        SetStatus("권한 요청 중...");
        
        PermissionHelper.RequestMicrophonePermission(
            onGranted: OnPermissionGranted,
            onDenied: () => OnPermissionDenied("마이크 권한 거부됨")
        );

#elif UNITY_STANDALONE || UNITY_EDITOR
        // Desktop/Editor: 권한 필요 없음
        if (STTManager.Instance.IsInitialized)
        {
            hasPermission = true;
            SetStatus("준비 완료");
            recordButton.interactable = true;
        }
        else
        {
            SetStatus("초기화 실패 - 모델 확인 필요");
            recordButton.interactable = false;
        }
#else
        hasPermission = true;
        SetStatus("준비 완료");
        recordButton.interactable = true;
#endif

        UpdateBackendInfo();
    }

    private void OnPermissionGranted()
    {
        hasPermission = true;
        SetStatus("준비 완료");
        recordButton.interactable = true;
    }

    private void OnPermissionDenied(string reason)
    {
        hasPermission = false;
        SetStatus($"권한 거부됨: {reason}");
        recordButton.interactable = false;
    }

    private void OnPartialResult(string text)
    {
        currentPartial = text;
        UpdateResultText();
    }

    private void OnFinalResult(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            if (!string.IsNullOrEmpty(accumulatedText))
            {
                accumulatedText += " ";
            }

            accumulatedText += text;
            currentPartial = "";
            UpdateResultText();
        }
    }

    private void OnError(string error)
    {
        SetStatus($"오류: {error}");
        Debug.LogWarning($"[STTTestUI] 오류: {error}");
    }

    private void UpdateResultText()
    {
        if (resultText == null) return;

        string display = accumulatedText;
        if (!string.IsNullOrEmpty(currentPartial))
        {
            if (!string.IsNullOrEmpty(display))
            {
                display += " ";
            }

            display += $"<color=#888888>{currentPartial}</color>"; // 진행 중인 텍스트는 회색으로
        }

        resultText.text = display;
    }

    private void ToggleRecording()
    {
        if (STTManager.Instance == null)
        {
            SetStatus("STTManager 없음");
            return;
        }

        if (!hasPermission)
        {
            SetStatus("권한이 필요합니다");
            return;
        }

        if (!isRecording)
        {
            StartRecording();
        }
        else
        {
            StopRecording();
        }
    }

    private void StartRecording()
    {
        // 초기화
        accumulatedText = "";
        currentPartial = "";
        if (resultText != null)
            resultText.text = "";

        STTManager.Instance.StartListening(languageCode);

        if (buttonText != null)
            buttonText.text = "녹음 중지";

        isRecording = true;
    }

    private void StopRecording()
    {
        STTManager.Instance.StopListening();

        if (buttonText != null)
            buttonText.text = "녹음 시작";

        isRecording = false;

        // 최종 결과 로그
        if (showDebugInfo)
        {
            Debug.Log($"[STTTestUI] 최종 누적 결과: {accumulatedText}");
        }
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
        {
            statusText.text = message;
        }

        if (showDebugInfo)
        {
            Debug.Log($"[STTTestUI] 상태: {message}");
        }
    }

    private void OnDestroy()
    {
        if (STTManager.Instance != null)
        {
            // 이벤트 구독 해제
            STTManager.Instance.OnStarted -= () => SetStatus("녹음 시작됨");
            STTManager.Instance.OnReady -= () => SetStatus("말씀하세요...");
            STTManager.Instance.OnStopped -= () => SetStatus("녹음 중지됨");
            STTManager.Instance.OnPartialResult -= OnPartialResult;
            STTManager.Instance.OnResult -= OnFinalResult;
            STTManager.Instance.OnError -= OnError;
            STTManager.Instance.OnPermissionGranted -= OnPermissionGranted;
            STTManager.Instance.OnPermissionDenied -= OnPermissionDenied;
        }
    }
}