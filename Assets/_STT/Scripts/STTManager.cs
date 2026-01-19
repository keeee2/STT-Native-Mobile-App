using System;
using UnityEngine;
using System.Runtime.InteropServices;

/// <summary>
/// 통합 STT Manager
/// - Android: 네이티브 SpeechRecognizer
/// - iOS: Speech Framework
/// - Windows: System.Speech (SAPI)
/// - macOS: Speech Framework (10.15+)
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
        WindowsNative,
        MacOSNative
    }

    // ===== 플랫폼별 구현 =====

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject androidPlugin;
#endif

#if (UNITY_IOS || UNITY_STANDALONE_OSX) && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void _STTSetGameObjectName(string name);
    
    [DllImport("__Internal")]
    private static extern void _STTRequestPermission();
    
    [DllImport("__Internal")]
    private static extern void _STTStartListening(string languageCode);
    
    [DllImport("__Internal")]
    private static extern void _STTStopListening();
#endif

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private WindowsSTTPlugin windowsPlugin;
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
#elif UNITY_STANDALONE_OSX && !UNITY_EDITOR
        InitializeMacOS();
#elif UNITY_STANDALONE_WIN && !UNITY_EDITOR
        InitializeWindows();
#elif UNITY_EDITOR_WIN
        InitializeWindowsEditor();
#elif UNITY_EDITOR_OSX
        InitializeMacOSEditor();
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

#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
    private void InitializeMacOS()
    {
        try
        {
            _STTSetGameObjectName(gameObject.name);
            CurrentBackend = STTBackend.MacOSNative;
            IsInitialized = true;
            Debug.Log("[STTManager] macOS Speech Framework 초기화 완료");
        }
        catch (Exception e)
        {
            Debug.LogError($"[STTManager] macOS 초기화 실패: {e.Message}");
        }
    }
#endif

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private void InitializeWindows()
    {
        try
        {
            // MainThread Dispatcher 초기화 (SAPI 콜백용)
            MainThreadDispatcher.Initialize();
            
            windowsPlugin = WindowsSTTPlugin.GetInstance();
            
            // 이벤트 연결
            windowsPlugin.OnStarted += () => OnSTTStarted("");
            windowsPlugin.OnReady += () => OnSTTReady("");
            windowsPlugin.OnStopped += () => OnSTTStopped("");
            windowsPlugin.OnPartialResult += (text) => OnSTTPartialResult(text);
            windowsPlugin.OnResult += (text) => OnSTTResult(text);
            windowsPlugin.OnError += (error) => OnSTTError(error);
            
            // 기본 언어로 초기화 시도
            if (windowsPlugin.Initialize("ko-KR"))
            {
                CurrentBackend = STTBackend.WindowsNative;
                IsInitialized = true;
                Debug.Log("[STTManager] Windows SAPI 초기화 완료");
            }
            else
            {
                // 한국어 실패 시 영어로 시도
                if (windowsPlugin.Initialize("en-US"))
                {
                    CurrentBackend = STTBackend.WindowsNative;
                    IsInitialized = true;
                    Debug.Log("[STTManager] Windows SAPI 초기화 완료 (en-US)");
                }
                else
                {
                    Debug.LogError("[STTManager] Windows SAPI 초기화 실패 - 음성 인식 언어 팩 확인 필요");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[STTManager] Windows 초기화 실패: {e.Message}");
        }
    }
#endif

#if UNITY_EDITOR_WIN
    private WindowsSTTPlugin windowsPluginEditor;

    private void InitializeWindowsEditor()
    {
        try
        {
            // MainThread Dispatcher 초기화 (SAPI 콜백용)
            MainThreadDispatcher.Initialize();

            windowsPluginEditor = WindowsSTTPlugin.GetInstance();

            // 이벤트 연결
            windowsPluginEditor.OnStarted += () => OnSTTStarted("");
            windowsPluginEditor.OnReady += () => OnSTTReady("");
            windowsPluginEditor.OnStopped += () => OnSTTStopped("");
            windowsPluginEditor.OnPartialResult += (text) => OnSTTPartialResult(text);
            windowsPluginEditor.OnResult += (text) => OnSTTResult(text);
            windowsPluginEditor.OnError += (error) => OnSTTError(error);

            // 기본 언어로 초기화 시도
            if (windowsPluginEditor.Initialize("ko-KR"))
            {
                CurrentBackend = STTBackend.WindowsNative;
                IsInitialized = true;
                Debug.Log("[STTManager] Windows Editor SAPI 초기화 완료");
            }
            else if (windowsPluginEditor.Initialize("en-US"))
            {
                CurrentBackend = STTBackend.WindowsNative;
                IsInitialized = true;
                Debug.Log("[STTManager] Windows Editor SAPI 초기화 완료 (en-US)");
            }
            else
            {
                Debug.LogError("[STTManager] Windows SAPI 초기화 실패 - 음성 인식 언어 팩 확인 필요");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[STTManager] Windows Editor 초기화 실패: {e.Message}");
        }
    }
#endif

#if UNITY_EDITOR_OSX
    private void InitializeMacOSEditor()
    {
        // Editor에서는 네이티브 플러그인 사용 불가
        Debug.LogWarning("[STTManager] macOS Editor에서는 STT 테스트 불가 - 빌드 후 테스트하세요");
        CurrentBackend = STTBackend.None;
        IsInitialized = false;
    }
#endif

    /// <summary>
    /// 권한 요청 (iOS/macOS에서 필요, 다른 플랫폼은 자동 처리)
    /// </summary>
    public void RequestPermission()
    {
#if UNITY_IOS && !UNITY_EDITOR
        _STTRequestPermission();
#elif UNITY_STANDALONE_OSX && !UNITY_EDITOR
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
    /// <param name="languageCode">언어 코드 (예: "ko-KR", "en-US")</param>
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
#elif UNITY_STANDALONE_OSX && !UNITY_EDITOR
        _STTStartListening(languageCode);
#elif UNITY_STANDALONE_WIN && !UNITY_EDITOR
        windowsPlugin?.StartListening(languageCode);
#elif UNITY_EDITOR_WIN
        windowsPluginEditor?.StartListening(languageCode);
#else
        OnError?.Invoke("현재 플랫폼에서는 STT를 사용할 수 없습니다");
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
#elif UNITY_STANDALONE_OSX && !UNITY_EDITOR
        _STTStopListening();
#elif UNITY_STANDALONE_WIN && !UNITY_EDITOR
        windowsPlugin?.StopListening();
#elif UNITY_EDITOR_WIN
        windowsPluginEditor?.StopListening();
#endif
    }

    /// <summary>
    /// 특정 언어 지원 여부 확인 (Windows 전용)
    /// </summary>
    public bool IsLanguageSupported(string languageCode)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return WindowsSTTPlugin.IsLanguageSupported(languageCode);
#else
        // 다른 플랫폼은 대부분의 주요 언어 지원
        return true;
#endif
    }

    /// <summary>
    /// 설치된 언어 목록 (Windows 전용)
    /// </summary>
    public string[] GetInstalledLanguages()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return WindowsSTTPlugin.GetInstalledLanguages();
#else
        return new string[] { "ko-KR", "en-US", "ja-JP", "zh-CN" };
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

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        windowsPlugin?.Dispose();
#elif UNITY_EDITOR_WIN
        windowsPluginEditor?.Dispose();
#endif

        if (Instance == this)
            Instance = null;
    }

    // ===== 네이티브에서 호출되는 콜백들 (Android/iOS/macOS) =====
    // Windows는 이벤트로 직접 연결됨

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