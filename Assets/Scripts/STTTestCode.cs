using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class STTTestCode : MonoBehaviour
{
    [SerializeField] private Button recordButton;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text resultText;

    private bool isRecording = false;
    private bool hasPermission = false;
    
    private string accumulatedText = "";
    private string currentPartial = "";

    private void Start()
    {
        // 이벤트 구독
        STTManager.Instance.OnStarted += () => statusText.text = "녹음 시작됨";
        STTManager.Instance.OnReady += () => statusText.text = "말씀하세요...";
        STTManager.Instance.OnStopped += () => statusText.text = "녹음 중지됨";

        STTManager.Instance.OnPartialResult += text =>
        {
            currentPartial = text;
            resultText.text = accumulatedText + (string.IsNullOrEmpty(accumulatedText) ? "" : " ") + text;
        };

        STTManager.Instance.OnResult += text =>
        {
            if (!string.IsNullOrEmpty(text))
            {
                accumulatedText += (string.IsNullOrEmpty(accumulatedText) ? "" : " ") + text;
                currentPartial = "";
                resultText.text = accumulatedText;
            }
        };

        STTManager.Instance.OnError += error => 
        { 
            if (error.Contains("NO_MATCH") || error.Contains("SPEECH_TIMEOUT"))
            {
                statusText.text = "계속 말씀하세요...";
            }
            else
            {
                statusText.text = $"에러: {error}"; 
            }
        };

        // iOS 권한 콜백
        STTManager.Instance.OnPermissionGranted += OnPermissionGranted;
        STTManager.Instance.OnPermissionDenied += OnPermissionDenied;

        // 버튼 클릭
        recordButton.onClick.AddListener(ToggleRecording);

        // 플랫폼별 초기화
        InitializePlatform();
    }

    private void InitializePlatform()
    {
#if UNITY_IOS && !UNITY_EDITOR
        // iOS: STTManager 통해 권한 요청
        recordButton.interactable = false;
        statusText.text = "권한 요청 중...";
        STTManager.Instance.RequestPermission();
        
#elif UNITY_ANDROID && !UNITY_EDITOR
        // Android: PermissionHelper로 런타임 권한 요청
        recordButton.interactable = false;
        statusText.text = "권한 요청 중...";
        
        PermissionHelper.RequestMicrophonePermission(
            onGranted: () => {
                hasPermission = true;
                statusText.text = "준비 완료";
                recordButton.interactable = true;
            },
            onDenied: () => {
                hasPermission = false;
                statusText.text = "마이크 권한이 거부됨";
                recordButton.interactable = false;
            }
        );
#else
        // Editor
        hasPermission = true;
        statusText.text = "준비 완료 (Editor)";
        recordButton.interactable = true;
#endif
    }

    private void OnPermissionGranted()
    {
        hasPermission = true;
        statusText.text = "준비 완료";
        recordButton.interactable = true;
    }

    private void OnPermissionDenied(string reason)
    {
        hasPermission = false;
        statusText.text = $"권한 거부됨: {reason}";
        recordButton.interactable = false;
    }

    private void ToggleRecording()
    {
        if (!hasPermission)
        {
            statusText.text = "권한이 필요합니다";
            return;
        }

        if (!isRecording)
        {
            // 시작할 때 초기화
            accumulatedText = "";
            currentPartial = "";
            resultText.text = "";
            
            STTManager.Instance.StartListening("ko-KR");
            recordButton.GetComponentInChildren<TMP_Text>().text = "녹음 중지";
            isRecording = true;
        }
        else
        {
            STTManager.Instance.StopListening();
            recordButton.GetComponentInChildren<TMP_Text>().text = "녹음 시작";
            isRecording = false;
            
            // 최종 결과 로그
            Debug.Log($"[STT] 최종 누적 결과: {accumulatedText}");
        }
    }
}
