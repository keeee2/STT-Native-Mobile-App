using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Android/iOS 네이티브 STT를 Unity에서 사용하기 위한 매니저
/// 이 스크립트가 붙은 게임오브젝트 이름이 "STTManager"여야 함
/// </summary>
public class STTManager : MonoBehaviour
{
    public static STTManager Instance { get; private set; }

    // 이벤트들
    public event Action OnStarted;
    public event Action OnReady;
    public event Action OnBeginning;
    public event Action OnEndOfSpeech;
    public event Action OnStopped;
    public event Action<string> OnPartialResult; // 중간 결과
    public event Action<string> OnResult; // 최종 결과
    public event Action<string> OnError;
    public event Action OnPermissionGranted;
    public event Action<string> OnPermissionDenied;

    // 현재 인식된 텍스트
    public string CurrentText { get; private set; } = "";
    public bool IsListening { get; private set; } = false;

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject plugin;
#endif

#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void _STTSetGameObjectName(string name);
    
    [DllImport("__Internal")]
    private static extern void _STTRequestPermission();
    
    [DllImport("__Internal")]
    private static extern void _STTStartListening(string languageCode);
    
    [DllImport("__Internal")]
    private static extern void _STTStopListening();
#endif

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            InitializePlugin();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void InitializePlugin()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var pluginClass = new AndroidJavaClass("com.voyager.eterna.stt.UnitySTTPlugin"))
            {
                plugin = pluginClass.CallStatic<AndroidJavaObject>("getInstance");
                plugin.Call("setGameObjectName", gameObject.name);
            }
            Debug.Log("[STTManager] Android plugin initialized");
        }
        catch (Exception e)
        {
            Debug.LogError($"[STTManager] Failed to initialize: {e.Message}");
        }
#elif UNITY_IOS && !UNITY_EDITOR
        try
        {
            _STTSetGameObjectName(gameObject.name);
            Debug.Log("[STTManager] iOS plugin initialized");
        }
        catch (Exception e)
        {
            Debug.LogError($"[STTManager] Failed to initialize: {e.Message}");
        }
#else
        Debug.Log("[STTManager] STT only works on Android/iOS device");
#endif
    }

    /// <summary>
    /// 권한 요청 (iOS에서 필요, Android는 별도 권한 시스템 사용 권장)
    /// </summary>
    public void RequestPermission()
    {
#if UNITY_IOS && !UNITY_EDITOR
        _STTRequestPermission();
#else
        Debug.Log("[STTManager] RequestPermission - iOS only");
        OnPermissionGranted?.Invoke();
#endif
    }

    /// <summary>
    /// STT 시작
    /// </summary>
    /// <param name="languageCode">언어 코드 (예: "ko-KR", "en-US", "ja-JP")</param>
    public void StartListening(string languageCode = "ko-KR")
    {
        CurrentText = "";

#if UNITY_ANDROID && !UNITY_EDITOR
        if (plugin != null)
        {
            plugin.Call("startListening", languageCode);
        }
#elif UNITY_IOS && !UNITY_EDITOR
        _STTStartListening(languageCode);
#else
        Debug.Log($"[STTManager] StartListening({languageCode}) - Editor에서는 동작 안 함");
        OnStarted?.Invoke();
#endif
    }

    /// <summary>
    /// STT 중지
    /// </summary>
    public void StopListening()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (plugin != null)
        {
            plugin.Call("stopListening");
        }
#elif UNITY_IOS && !UNITY_EDITOR
        _STTStopListening();
#else
        Debug.Log("[STTManager] StopListening - Editor에서는 동작 안 함");
        OnStopped?.Invoke();
#endif
    }

    private void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (plugin != null)
        {
            plugin.Call("destroy");
            plugin.Dispose();
            plugin = null;
        }
#endif
    }

    // ===== 네이티브에서 호출되는 콜백들 =====

    private void OnSTTStarted(string message)
    {
        IsListening = true;
        Debug.Log("[STTManager] Started");
        OnStarted?.Invoke();
    }

    private void OnSTTReady(string message)
    {
        Debug.Log("[STTManager] Ready for speech");
        OnReady?.Invoke();
    }

    private void OnSTTBeginning(string message)
    {
        Debug.Log("[STTManager] Speech beginning");
        OnBeginning?.Invoke();
    }

    private void OnSTTEndOfSpeech(string message)
    {
        Debug.Log("[STTManager] End of speech");
        OnEndOfSpeech?.Invoke();
    }

    private void OnSTTStopped(string message)
    {
        IsListening = false;
        Debug.Log("[STTManager] Stopped");
        OnStopped?.Invoke();
    }

    private void OnSTTPartialResult(string text)
    {
        CurrentText = text;
        Debug.Log($"[STTManager] Partial: {text}");
        OnPartialResult?.Invoke(text);
    }

    private void OnSTTResult(string text)
    {
        CurrentText = text;
        Debug.Log($"[STTManager] Result: {text}");
        OnResult?.Invoke(text);
    }

    private void OnSTTError(string error)
    {
        Debug.LogWarning($"[STTManager] Error: {error}");
        OnError?.Invoke(error);
    }

    // iOS 권한 콜백
    private void OnSTTPermissionGranted(string message)
    {
        Debug.Log("[STTManager] Permission granted");
        OnPermissionGranted?.Invoke();
    }

    private void OnSTTPermissionDenied(string message)
    {
        Debug.LogWarning($"[STTManager] Permission denied: {message}");
        OnPermissionDenied?.Invoke(message);
    }
}