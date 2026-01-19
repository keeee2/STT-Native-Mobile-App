using System.Collections;
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

    [Header("성능")]
    [Tooltip("스트리밍 Partial 결과가 너무 자주 들어올 때 UI 갱신을 줄입니다(초)")]
    [SerializeField] private float uiUpdateThrottleSec = 0.05f;

    [Tooltip("Desktop/Editor에서 STT 초기화 대기 타임아웃(초)")]
    [SerializeField] private float initTimeoutSec = 60f;

    private bool isRecording = false;
    private bool hasPermission = false;
    private string accumulatedText = "";
    private string currentPartial = "";

    private float _lastUiUpdateTime = -999f;
    private Coroutine _waitInitCo;

    private void Start()
    {
        SetupUI();
        SetupEventListeners();
        InitializePlatform();
    }

    private void SetupUI()
    {
        if (buttonText == null && recordButton != null)
            buttonText = recordButton.GetComponentInChildren<TMP_Text>();

        UpdateBackendInfo();
    }

    private void SetupEventListeners()
    {
        if (STTManager.Instance == null)
        {
            Debug.LogError("[STTTestUI] STTManager.Instance가 없음. 씬에 STTManager 게임오브젝트를 추가하세요.");
            if (statusText != null) statusText.text = "STTManager 없음";
            return;
        }

        // 이벤트 구독 (람다 금지: 해제 불가)
        STTManager.Instance.OnStarted += HandleStarted;
        STTManager.Instance.OnReady += HandleReady;
        STTManager.Instance.OnStopped += HandleStopped;

        STTManager.Instance.OnPartialResult += OnPartialResult;
        STTManager.Instance.OnResult += OnFinalResult;
        STTManager.Instance.OnError += OnError;

        STTManager.Instance.OnPermissionGranted += OnPermissionGranted;
        STTManager.Instance.OnPermissionDenied += OnPermissionDenied;

        if (recordButton != null)
            recordButton.onClick.AddListener(ToggleRecording);
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
                STTManager.STTBackend.Whisper => "Standalone/Editor (Whisper)",
                _ => "초기화 중..."
            };

            backendText.text = $"백엔드: {backend}";

            if (showDebugInfo)
            {
                backendText.text += $"\n플랫폼: {Application.platform}";
                backendText.text += $"\n초기화: {(STTManager.Instance.IsInitialized ? "완료" : "대기/실패")}";
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
            if (recordButton != null) recordButton.interactable = false;
            return;
        }

#if UNITY_IOS && !UNITY_EDITOR
        // iOS: STTManager 통해 권한 요청
        if (recordButton != null) recordButton.interactable = false;
        SetStatus("권한 요청 중...");
        STTManager.Instance.RequestPermission();

#elif UNITY_ANDROID && !UNITY_EDITOR
        // Android: PermissionHelper로 런타임 권한 요청
        if (recordButton != null) recordButton.interactable = false;
        SetStatus("권한 요청 중...");

        PermissionHelper.RequestMicrophonePermission(
            onGranted: OnPermissionGranted,
            onDenied: () => OnPermissionDenied("마이크 권한 거부됨")
        );

#elif UNITY_STANDALONE || UNITY_EDITOR
        // Desktop/Editor: 권한 필요 없음 + 초기화는 비동기
        hasPermission = true;
        if (recordButton != null) recordButton.interactable = false;

        if (_waitInitCo != null) StopCoroutine(_waitInitCo);
        _waitInitCo = StartCoroutine(WaitForInitialization());
#else
        hasPermission = true;
        SetStatus("준비 완료");
        if (recordButton != null) recordButton.interactable = true;
#endif

        UpdateBackendInfo();
    }

    private IEnumerator WaitForInitialization()
    {
        SetStatus("Whisper 로딩 중...");

        float start = Time.realtimeSinceStartup;
        while (STTManager.Instance != null && !STTManager.Instance.IsInitialized)
        {
            if (initTimeoutSec > 0f && Time.realtimeSinceStartup - start > initTimeoutSec)
            {
                SetStatus("초기화 타임아웃 - 모델/백엔드 확인 필요");
                if (recordButton != null) recordButton.interactable = false;
                yield break;
            }

            UpdateBackendInfo();
            yield return new WaitForSeconds(0.25f);
        }

        if (STTManager.Instance == null)
        {
            SetStatus("STTManager 없음");
            if (recordButton != null) recordButton.interactable = false;
            yield break;
        }

        SetStatus("준비 완료");
        if (recordButton != null) recordButton.interactable = true;
        UpdateBackendInfo();
    }

    private void HandleStarted() => SetStatus("녹음 시작됨");

    private void HandleReady() => SetStatus("말씀하세요...");

    private void HandleStopped() => SetStatus("녹음 중지됨");

    private void OnPermissionGranted()
    {
        hasPermission = true;
        SetStatus("준비 완료");
        if (recordButton != null) recordButton.interactable = true;
        UpdateBackendInfo();
    }

    private void OnPermissionDenied(string reason)
    {
        hasPermission = false;
        SetStatus($"권한 거부됨: {reason}");
        if (recordButton != null) recordButton.interactable = false;
        UpdateBackendInfo();
    }

    private void OnPartialResult(string text)
    {
        currentPartial = text;
        ThrottledUpdateResultText();
    }

    private void OnFinalResult(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (!string.IsNullOrEmpty(accumulatedText))
            accumulatedText += " ";

        accumulatedText += text;
        currentPartial = "";
        UpdateResultText(force: true);
    }

    private void OnError(string error)
    {
        SetStatus($"오류: {error}");
        Debug.LogWarning($"[STTTestUI] 오류: {error}");
    }

    private void ThrottledUpdateResultText()
    {
        if (uiUpdateThrottleSec <= 0f)
        {
            UpdateResultText(force: false);
            return;
        }

        if (Time.realtimeSinceStartup - _lastUiUpdateTime < uiUpdateThrottleSec)
            return;

        _lastUiUpdateTime = Time.realtimeSinceStartup;
        UpdateResultText(force: false);
    }

    private void UpdateResultText(bool force)
    {
        if (resultText == null) return;

        string display = accumulatedText;
        if (!string.IsNullOrEmpty(currentPartial))
        {
            if (!string.IsNullOrEmpty(display))
                display += " ";

            display += $"<color=#888888>{currentPartial}</color>";
        }

        resultText.text = display;

        if (force)
            _lastUiUpdateTime = Time.realtimeSinceStartup;
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

        if (!isRecording) StartRecording();
        else StopRecording();
    }

    private void StartRecording()
    {
        accumulatedText = "";
        currentPartial = "";
        if (resultText != null) resultText.text = "";

        STTManager.Instance.StartListening(languageCode);

        if (buttonText != null) buttonText.text = "녹음 중지";
        isRecording = true;
    }

    private void StopRecording()
    {
        STTManager.Instance.StopListening();

        if (buttonText != null) buttonText.text = "녹음 시작";
        isRecording = false;

        if (showDebugInfo)
            Debug.Log($"[STTTestUI] 최종 누적 결과: {accumulatedText}");
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
            statusText.text = message;

        if (showDebugInfo)
            Debug.Log($"[STTTestUI] 상태: {message}");
    }

    private void OnDestroy()
    {
        if (_waitInitCo != null)
        {
            StopCoroutine(_waitInitCo);
            _waitInitCo = null;
        }

        if (recordButton != null)
        {
            recordButton.onClick.RemoveListener(ToggleRecording);
        }

        if (STTManager.Instance != null)
        {
            STTManager.Instance.OnStarted -= HandleStarted;
            STTManager.Instance.OnReady -= HandleReady;
            STTManager.Instance.OnStopped -= HandleStopped;

            STTManager.Instance.OnPartialResult -= OnPartialResult;
            STTManager.Instance.OnResult -= OnFinalResult;
            STTManager.Instance.OnError -= OnError;

            STTManager.Instance.OnPermissionGranted -= OnPermissionGranted;
            STTManager.Instance.OnPermissionDenied -= OnPermissionDenied;
        }
    }
}