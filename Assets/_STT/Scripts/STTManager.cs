using System;
using UnityEngine;
using System.Runtime.InteropServices;

/// <summary>
/// 통합 STT Manager
/// - Android: 네이티브 SpeechRecognizer
/// - iOS: Speech Framework  
/// - Windows/macOS/Editor: whisper.unity
/// 
/// 게임오브젝트 이름: "STTManager"
/// </summary>
public class STTManager : MonoBehaviour
{
    public static STTManager Instance { get; private set; }

    // 공통 이벤트들
    public event Action OnStarted;
    public event Action OnReady;
    public event Action OnBeginning;
    public event Action OnEndOfSpeech;
    public event Action OnStopped;
    public event Action<string> OnPartialResult;
    public event Action<string> OnResult;
    public event Action<string> OnError;
    public event Action OnPermissionGranted;
    public event Action<string> OnPermissionDenied;

    // 상태
    public string CurrentText { get; private set; } = "";
    public bool IsListening { get; private set; } = false;
    public bool IsInitialized { get; private set; } = false;

    // 현재 사용 중인 백엔드
    public STTBackend CurrentBackend { get; private set; } = STTBackend.None;

    public enum STTBackend
    {
        None,
        AndroidNative,
        iOSNative,
        Whisper
    }

    // ===== 플랫폼별 구현 =====

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject androidPlugin;
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

    // Whisper 플러그인 (Editor/Standalone용)
#if UNITY_EDITOR || UNITY_STANDALONE
    private WhisperSTTPlugin whisperPlugin;
#endif

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            InitializeBackend();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void InitializeBackend()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        InitializeAndroid();
#elif UNITY_IOS && !UNITY_EDITOR
        InitializeiOS();
#elif UNITY_EDITOR || UNITY_STANDALONE
        InitializeWhisper();
#else
        Debug.LogWarning("[STTManager] 지원되지 않는 플랫폼");
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void InitializeAndroid()
    {
        try
        {
            using (var pluginClass = new AndroidJavaClass("com.voyager.eterna.stt.UnitySTTPlugin"))
            {
                androidPlugin = pluginClass.CallStatic<AndroidJavaObject>("getInstance");
                androidPlugin.Call("setGameObjectName", gameObject.name);
            }
            CurrentBackend = STTBackend.AndroidNative;
            IsInitialized = true;
            Debug.Log("[STTManager] Android 네이티브 초기화 완료");
        }
        catch (Exception e)
        {
            Debug.LogError($"[STTManager] Android 초기화 실패: {e.Message}");
        }
    }
#endif

#if UNITY_IOS && !UNITY_EDITOR
    private void InitializeiOS()
    {
        try
        {
            _STTSetGameObjectName(gameObject.name);
            CurrentBackend = STTBackend.iOSNative;
            IsInitialized = true;
            Debug.Log("[STTManager] iOS 네이티브 초기화 완료");
        }
        catch (Exception e)
        {
            Debug.LogError($"[STTManager] iOS 초기화 실패: {e.Message}");
        }
    }
#endif

#if UNITY_EDITOR || UNITY_STANDALONE
    private void InitializeWhisper()
    {
        try
        {
            whisperPlugin = gameObject.AddComponent<WhisperSTTPlugin>();
            
            // Whisper 이벤트 연결
            whisperPlugin.OnStarted += () => OnSTTStarted("");
            whisperPlugin.OnReady += () => OnSTTReady("");
            whisperPlugin.OnStopped += () => OnSTTStopped("");
            whisperPlugin.OnPartialResult += (text) => OnSTTPartialResult(text);
            whisperPlugin.OnResult += (text) => OnSTTResult(text);
            whisperPlugin.OnError += (error) => OnSTTError(error);
            
            // Whisper 초기화는 비동기로 진행됨
            whisperPlugin.OnInitialized += (success) =>
            {
                if (success)
                {
                    CurrentBackend = STTBackend.Whisper;
                    IsInitialized = true;
                    Debug.Log("[STTManager] Whisper 초기화 완료");
                }
                else
                {
                    Debug.LogError("[STTManager] Whisper 초기화 실패");
                }
            };
            
            whisperPlugin.Initialize();
        }
        catch (Exception e)
        {
            Debug.LogError($"[STTManager] Whisper 초기화 실패: {e.Message}");
        }
    }
#endif

    /// <summary>
    /// 권한 요청 (iOS에서 필요, 다른 플랫폼은 자동 처리)
    /// </summary>
    public void RequestPermission()
    {
#if UNITY_IOS && !UNITY_EDITOR
        _STTRequestPermission();
#elif UNITY_ANDROID && !UNITY_EDITOR
        // Android는 별도의 PermissionHelper 사용 권장
        OnPermissionGranted?.Invoke();
#else
        // Desktop/Editor는 권한 불필요
        OnPermissionGranted?.Invoke();
#endif
    }

    /// <summary>
    /// STT 시작
    /// </summary>
    /// <param name="languageCode">언어 코드 (예: "ko-KR", "en-US", "ko", "en")</param>
    public void StartListening(string languageCode = "ko-KR")
    {
        if (!IsInitialized)
        {
            OnError?.Invoke("STT가 초기화되지 않음");
            return;
        }

        CurrentText = "";

#if UNITY_ANDROID && !UNITY_EDITOR
        androidPlugin?.Call("startListening", languageCode);
#elif UNITY_IOS && !UNITY_EDITOR
        _STTStartListening(languageCode);
#elif UNITY_EDITOR || UNITY_STANDALONE
        whisperPlugin?.StartListening(languageCode);
#endif
    }

    /// <summary>
    /// STT 중지
    /// </summary>
    public void StopListening()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        androidPlugin?.Call("stopListening");
#elif UNITY_IOS && !UNITY_EDITOR
        _STTStopListening();
#elif UNITY_EDITOR || UNITY_STANDALONE
        whisperPlugin?.StopListening();
#endif
    }

    private void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (androidPlugin != null)
        {
            androidPlugin.Call("destroy");
            androidPlugin.Dispose();
            androidPlugin = null;
        }
#endif

        if (Instance == this)
            Instance = null;
    }

    // ===== 네이티브에서 호출되는 콜백들 (Android/iOS) =====
    // Whisper는 이벤트로 직접 연결됨

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
