#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Globalization;
using UnityEngine;
using System.Speech.Recognition;

/// <summary>
/// Windows 내장 STT (SAPI) 플러그인
/// - Windows 10/11 기본 지원
/// - 한국어는 Windows 언어 팩 설치 필요
/// - 오프라인 동작 가능
/// - Editor에서도 테스트 가능!
/// </summary>
public class WindowsSTTPlugin : IDisposable
{
    public static WindowsSTTPlugin Instance { get; private set; }

    // 이벤트 (STTManager로 전달)
    public event Action OnStarted;
    public event Action OnReady;
    public event Action OnStopped;
    public event Action<string> OnPartialResult;
    public event Action<string> OnResult;
    public event Action<string> OnError;

    public bool IsListening { get; private set; }
    public bool IsInitialized { get; private set; }

    private SpeechRecognitionEngine recognizer;
    private string currentLanguage = "ko-KR";
    private bool isDisposed;

    public static WindowsSTTPlugin GetInstance()
    {
        if (Instance == null)
        {
            Instance = new WindowsSTTPlugin();
        }

        return Instance;
    }

    private WindowsSTTPlugin()
    {
        IsInitialized = false;
        IsListening = false;
    }

    /// <summary>
    /// 특정 언어로 초기화
    /// </summary>
    public bool Initialize(string languageCode = "ko-KR")
    {
        try
        {
            currentLanguage = languageCode;
            var culture = new CultureInfo(languageCode);

            // 해당 언어를 지원하는 인식기 찾기
            recognizer = new SpeechRecognitionEngine(culture);

            // 기본 받아쓰기 문법 로드
            recognizer.LoadGrammar(new DictationGrammar());

            // 이벤트 연결
            recognizer.SpeechRecognized += OnSpeechRecognized;
            recognizer.SpeechHypothesized += OnSpeechHypothesized;
            recognizer.RecognizeCompleted += OnRecognizeCompleted;
            recognizer.SpeechDetected += OnSpeechDetected;

            // 마이크 입력 설정
            recognizer.SetInputToDefaultAudioDevice();

            IsInitialized = true;
            Debug.Log($"[WindowsSTT] 초기화 완료: {languageCode}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[WindowsSTT] 초기화 실패: {e.Message}");
            OnError?.Invoke($"초기화 실패: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 설치된 음성 인식 언어 목록 확인
    /// </summary>
    public static string[] GetInstalledLanguages()
    {
        try
        {
            var installedRecognizers = SpeechRecognitionEngine.InstalledRecognizers();
            var languages = new string[installedRecognizers.Count];
            for (int i = 0; i < installedRecognizers.Count; i++)
            {
                languages[i] = installedRecognizers[i].Culture.Name;
            }

            return languages;
        }
        catch
        {
            return new string[0];
        }
    }

    /// <summary>
    /// 특정 언어 지원 여부 확인
    /// </summary>
    public static bool IsLanguageSupported(string languageCode)
    {
        try
        {
            var culture = new CultureInfo(languageCode);
            foreach (var recognizer in SpeechRecognitionEngine.InstalledRecognizers())
            {
                if (recognizer.Culture.Equals(culture))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 음성 인식 시작 (연속 인식 모드)
    /// </summary>
    public void StartListening(string languageCode = null)
    {
        if (IsListening)
        {
            Debug.LogWarning("[WindowsSTT] 이미 인식 중");
            return;
        }

        // 언어가 변경되면 재초기화
        if (languageCode != null && languageCode != currentLanguage)
        {
            Dispose();
            Instance = this; // Dispose에서 null로 설정되므로 복구
            isDisposed = false;
            if (!Initialize(languageCode))
                return;
        }
        else if (!IsInitialized)
        {
            if (!Initialize(languageCode ?? currentLanguage))
                return;
        }

        try
        {
            // 연속 인식 모드로 시작
            recognizer.RecognizeAsync(RecognizeMode.Multiple);
            IsListening = true;

            OnStarted?.Invoke();
            OnReady?.Invoke();

            Debug.Log("[WindowsSTT] 인식 시작");
        }
        catch (Exception e)
        {
            Debug.LogError($"[WindowsSTT] 시작 실패: {e.Message}");
            OnError?.Invoke($"시작 실패: {e.Message}");
        }
    }

    /// <summary>
    /// 음성 인식 중지
    /// </summary>
    public void StopListening()
    {
        if (!IsListening)
            return;

        try
        {
            recognizer?.RecognizeAsyncCancel();
            IsListening = false;
            OnStopped?.Invoke();
            Debug.Log("[WindowsSTT] 인식 중지");
        }
        catch (Exception e)
        {
            Debug.LogError($"[WindowsSTT] 중지 실패: {e.Message}");
        }
    }

    private void OnSpeechDetected(object sender, SpeechDetectedEventArgs e)
    {
        Debug.Log("[WindowsSTT] 음성 감지됨");
    }

    private void OnSpeechHypothesized(object sender, SpeechHypothesizedEventArgs e)
    {
        // Partial result (중간 결과)
        MainThreadDispatcher.Enqueue(() => { OnPartialResult?.Invoke(e.Result.Text); });
    }

    private void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
    {
        // Final result
        if (e.Result.Confidence > 0.3f) // 신뢰도 필터
        {
            MainThreadDispatcher.Enqueue(() => { OnResult?.Invoke(e.Result.Text); });
        }
    }

    private void OnRecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
    {
        if (e.Error != null)
        {
            MainThreadDispatcher.Enqueue(() => { OnError?.Invoke(e.Error.Message); });
        }

        if (e.Cancelled)
        {
            Debug.Log("[WindowsSTT] 인식 취소됨");
        }
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        StopListening();

        if (recognizer != null)
        {
            recognizer.SpeechRecognized -= OnSpeechRecognized;
            recognizer.SpeechHypothesized -= OnSpeechHypothesized;
            recognizer.RecognizeCompleted -= OnRecognizeCompleted;
            recognizer.SpeechDetected -= OnSpeechDetected;
            recognizer.Dispose();
            recognizer = null;
        }

        IsInitialized = false;
        isDisposed = true;
        Instance = null;
    }
}

/// <summary>
/// 메인 스레드 디스패처 (SAPI 콜백이 백그라운드 스레드에서 오기 때문에 필요)
/// </summary>
public class MainThreadDispatcher : MonoBehaviour
{
    private static MainThreadDispatcher instance;

    private static readonly System.Collections.Generic.Queue<Action> executionQueue =
        new System.Collections.Generic.Queue<Action>();

    public static void Enqueue(Action action)
    {
        lock (executionQueue)
        {
            executionQueue.Enqueue(action);
        }
    }

    public static void Initialize()
    {
        if (instance == null)
        {
            var go = new GameObject("MainThreadDispatcher");
            instance = go.AddComponent<MainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }
    }

    private void Update()
    {
        lock (executionQueue)
        {
            while (executionQueue.Count > 0)
            {
                executionQueue.Dequeue()?.Invoke();
            }
        }
    }

    private void OnDestroy()
    {
        instance = null;
    }
}
#endif