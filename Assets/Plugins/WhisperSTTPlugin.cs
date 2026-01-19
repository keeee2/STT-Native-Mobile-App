#if UNITY_EDITOR || UNITY_STANDALONE
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using Whisper;
using Whisper.Utils;

/// <summary>
/// whisper.unity 래퍼 플러그인
/// - WhisperManager + WhisperStream + MicrophoneRecord 통합
/// - STTManager와 동일한 이벤트 인터페이스 제공
///
/// 모델 위치:
/// - Editor: 프로젝트폴더/WhisperModels/
/// - Standalone: 실행파일옆/WhisperModels/
/// </summary>
public class WhisperSTTPlugin : MonoBehaviour
{
    // 모델 폴더 이름 (빌드 파일 옆에 수동으로 배치)
    public const string MODEL_FOLDER_NAME = "WhisperModels";
    public const string DEFAULT_MODEL_NAME = "ggml-small.bin";

    [Header("Whisper 설정")]
    [Tooltip("Whisper 모델 파일명 (WhisperModels 폴더 내)")]
    [SerializeField] private string modelFileName = DEFAULT_MODEL_NAME;

    [Tooltip("GPU 가속 사용 (Vulkan/Metal)")]
    [SerializeField] private bool useGpu = true;

    [Header("스트리밍 설정")]
    [Tooltip("스트리밍 스텝 간격 (초)")]
    [SerializeField] private float stepSec = 3f;

    [Tooltip("이전 세그먼트 유지 시간 (초)")]
    [SerializeField] private float keepSec = 0.2f;

    [Tooltip("컨텍스트 업데이트까지 최대 시간 (초)")]
    [SerializeField] private float lengthSec = 10f;

    [Tooltip("VAD(음성 감지) 사용")]
    [SerializeField] private bool useVad = true;

    [Header("언어 설정")]
    [Tooltip("인식 언어 (ko, en, ja 등). 비워두면 자동 감지")]
    [SerializeField] private string defaultLanguage = "ko";

    [Header("안전/성능")]
    [Tooltip("모델 로딩 타임아웃(초). GPU/Vulkan 문제로 로딩이 끝없이 걸릴 때 대비")]
    [SerializeField] private float modelLoadTimeoutSec = 60f;

    [Tooltip("스트림 생성 타임아웃(ms)")]
    [SerializeField] private int streamCreateTimeoutMs = 15000;

    [Tooltip("Partial 결과 UI/이벤트 갱신 최소 간격(초). 스트리밍 콜백 폭주 방지")]
    [SerializeField] private float partialThrottleSec = 0.10f;

    [Tooltip("세그먼트 로그 출력")]
    [SerializeField] private bool logSegments = false;

    [Tooltip("Partial 로그 출력")]
    [SerializeField] private bool logPartial = false;

    // 이벤트 (STTManager 인터페이스와 동일)
    public event Action<bool> OnInitialized;
    public event Action OnStarted;
    public event Action OnReady;
    public event Action OnStopped;
    public event Action<string> OnPartialResult;
    public event Action<string> OnResult;
    public event Action<string> OnError;

    // 상태
    public bool IsInitialized => _whisperManager != null && _whisperManager.IsLoaded;
    public bool IsLoading => _whisperManager != null && _whisperManager.IsLoading;
    public bool IsListening { get; private set; }

    // Whisper 컴포넌트
    private WhisperManager _whisperManager;
    private MicrophoneRecord _microphoneRecord;
    private WhisperStream _whisperStream;

    // 내부 상태
    private string _currentLanguage;
    private string _accumulatedText = "";

    // Partial throttle
    private float _lastPartialEmitTime = -999f;
    private string _lastPartialText = "";
    private string _pendingPartialText = null;

    // Main thread dispatcher (콜백 스레드가 불명확할 때를 대비)
    private readonly object _queueLock = new object();
    private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();
    private readonly List<Action> _drainList = new List<Action>(64);

    private bool _isDestroying;

    // Stop 이벤트 중복 방지 + 강제 정리용
    private bool _stopNotified;
    private int _sessionId;
    private Coroutine _forceCleanupCo;


    /// <summary>
    /// WhisperModels 폴더 경로 반환
    /// - Editor: 프로젝트 루트/WhisperModels/
    /// - Standalone: 실행파일 옆/WhisperModels/
    /// </summary>
    public static string GetModelFolderPath()
    {
#if UNITY_EDITOR
        // Editor: 프로젝트 루트 폴더 (Assets의 상위)
        return Path.Combine(Path.GetDirectoryName(Application.dataPath), MODEL_FOLDER_NAME);
#else
        // Standalone: 실행 파일 옆 (AppName_Data의 상위)
        return Path.Combine(Path.GetDirectoryName(Application.dataPath), MODEL_FOLDER_NAME);
#endif
    }

    /// <summary>
    /// 모델 파일 전체 경로 반환
    /// </summary>
    public static string GetModelFilePath(string modelFileName = DEFAULT_MODEL_NAME)
    {
        return Path.Combine(GetModelFolderPath(), modelFileName);
    }

    public void Initialize()
    {
        StartCoroutine(InitializeCoroutine());
    }

    private void EnqueueMainThread(Action action)
    {
        if (action == null) return;
        lock (_queueLock)
        {
            _mainThreadQueue.Enqueue(action);
        }
    }

    private void Update()
    {
        // Drain actions
        _drainList.Clear();
        lock (_queueLock)
        {
            while (_mainThreadQueue.Count > 0)
            {
                _drainList.Add(_mainThreadQueue.Dequeue());
            }
        }

        for (int i = 0; i < _drainList.Count; i++)
        {
            try
            {
                _drainList[i]?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // Partial throttle: pending 처리
        if (!string.IsNullOrEmpty(_pendingPartialText))
        {
            if (Time.realtimeSinceStartup - _lastPartialEmitTime >= partialThrottleSec)
            {
                var txt = _pendingPartialText;
                _pendingPartialText = null;

                _stopNotified = false;
                _sessionId++;
                if (_forceCleanupCo != null)
                {
                    StopCoroutine(_forceCleanupCo);
                    _forceCleanupCo = null;
                }

                EmitPartialNow(txt);
            }
        }
    }

    private IEnumerator InitializeCoroutine()
    {
        // 모델 경로 확인
        string fullModelPath = GetModelFilePath(modelFileName);

        if (!File.Exists(fullModelPath))
        {
            string errorMsg = $"모델 파일을 찾을 수 없음: {fullModelPath}";
            Debug.LogError($"[WhisperSTT] {errorMsg}");
            Debug.LogError($"[WhisperSTT] '{MODEL_FOLDER_NAME}' 폴더를 만들고 모델을 넣어주세요.");
            Debug.LogError("[WhisperSTT] 다운로드: https://huggingface.co/ggerganov/whisper.cpp/tree/main");
            OnError?.Invoke(errorMsg);
            OnInitialized?.Invoke(false);
            yield break;
        }

        Debug.Log($"[WhisperSTT] 모델 경로 확인됨: {fullModelPath}");

        // ⭐ 비활성화 상태에서 WhisperManager 생성 (Awake 방지)
        var whisperGo = new GameObject("WhisperManager");
        whisperGo.SetActive(false);
        whisperGo.transform.SetParent(transform);
        _whisperManager = whisperGo.AddComponent<WhisperManager>();

        // 모델 경로 설정 (Awake 전에 설정해야 함!)
        _whisperManager.ModelPath = fullModelPath;
        _whisperManager.IsModelPathInStreamingAssets = false;

        // 기본 설정
        _whisperManager.language = string.IsNullOrEmpty(defaultLanguage) ? "auto" : defaultLanguage;
        _whisperManager.stepSec = stepSec;
        _whisperManager.keepSec = keepSec;
        _whisperManager.lengthSec = lengthSec;
        _whisperManager.useVad = useVad;

        // (가능하면) GPU 설정 적용
        TryApplyWhisperManagerBool(_whisperManager, useGpu, "useGpu", "UseGpu", "useGPU", "UseGPU");

        // 이벤트 구독
        _whisperManager.OnNewSegment += OnWhisperNewSegment;

        // ⭐ 이제 활성화 → Awake 호출 → 설정된 경로로 InitModel 실행
        whisperGo.SetActive(true);

        Debug.Log("[WhisperSTT] 모델 로딩 시작...");

        float start = Time.realtimeSinceStartup;
        while (_whisperManager.IsLoading)
        {
            if (modelLoadTimeoutSec > 0f && Time.realtimeSinceStartup - start > modelLoadTimeoutSec)
            {
                Debug.LogError("[WhisperSTT] 모델 로딩 타임아웃");
                OnError?.Invoke("모델 로딩 타임아웃");
                OnInitialized?.Invoke(false);

                // 정리
                SafeDestroyWhisperObjects();
                yield break;
            }

            yield return null;
        }

        if (!_whisperManager.IsLoaded)
        {
            Debug.LogError("[WhisperSTT] 모델 로드 실패");
            OnError?.Invoke("모델 로드 실패");
            OnInitialized?.Invoke(false);
            SafeDestroyWhisperObjects();
            yield break;
        }

        // MicrophoneRecord 생성 (필요 시)
        _microphoneRecord = gameObject.GetComponent<MicrophoneRecord>();
        if (_microphoneRecord == null)
        {
            _microphoneRecord = gameObject.AddComponent<MicrophoneRecord>();
        }

        Debug.Log("[WhisperSTT] 초기화 완료");
        OnInitialized?.Invoke(true);
    }

    /// <summary>
    /// 음성 인식 시작
    /// </summary>
    /// <param name="languageCode">언어 코드 (ko-KR, en-US, ko, en 등)</param>
    public async void StartListening(string languageCode = "ko")
    {
        if (!IsInitialized)
        {
            OnError?.Invoke("Whisper가 초기화되지 않음");
            return;
        }

        if (IsListening)
        {
            Debug.LogWarning("[WhisperSTT] 이미 인식 중");
            return;
        }

        if (_microphoneRecord == null)
        {
            _microphoneRecord = gameObject.AddComponent<MicrophoneRecord>();
        }

        // 언어 코드 정규화 (ko-KR -> ko)
        _currentLanguage = NormalizeLanguageCode(languageCode);
        _whisperManager.language = _currentLanguage;

        // 누적 텍스트 초기화
        _accumulatedText = "";
        _lastPartialText = "";
        _pendingPartialText = null;

        _stopNotified = false;
        _sessionId++;
        if (_forceCleanupCo != null)
        {
            StopCoroutine(_forceCleanupCo);
            _forceCleanupCo = null;
        }

        try
        {
            // 마이크 녹음 시작
            _microphoneRecord.StartRecord();

            // WhisperStream 생성 (타임아웃)
            var createTask = _whisperManager.CreateStream(_microphoneRecord);
            var completed = await Task.WhenAny(createTask, Task.Delay(streamCreateTimeoutMs));
            if (completed != createTask)
            {
                OnError?.Invoke("스트림 생성 타임아웃");
                _microphoneRecord.StopRecord();
                return;
            }

            _whisperStream = await createTask;

            if (_whisperStream == null)
            {
                OnError?.Invoke("스트림 생성 실패");
                _microphoneRecord.StopRecord();
                return;
            }

            // 스트림 이벤트 구독
            _whisperStream.OnResultUpdated += OnStreamResultUpdated;
            _whisperStream.OnSegmentUpdated += OnStreamSegmentUpdated;
            _whisperStream.OnSegmentFinished += OnStreamSegmentFinished;
            _whisperStream.OnStreamFinished += OnStreamFinished;

            // 스트리밍 시작
            _whisperStream.StartStream();

            IsListening = true;
            OnStarted?.Invoke();
            OnReady?.Invoke();

            Debug.Log($"[WhisperSTT] 인식 시작 (언어: {_currentLanguage}, GPU: {useGpu})");
        }
        catch (Exception e)
        {
            Debug.LogError($"[WhisperSTT] 시작 실패: {e.Message}");
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

        IsListening = false;

        // Notify immediately for manual stop (deduped)
        if (!_stopNotified)
        {
            _stopNotified = true;
            OnStopped?.Invoke();
        }

        try
        {
            // Stop stream explicitly if available
            TryInvokeStopStream(_whisperStream);

            // Stop microphone record (may also trigger StopStream)
            _microphoneRecord?.StopRecord();
        }
        catch (Exception e)
        {
            Debug.LogError($"[WhisperSTT] stop failed: {e.Message}");
        }

        // Force cleanup if stream callbacks never arrive
        if (_forceCleanupCo != null)
            StopCoroutine(_forceCleanupCo);
        _forceCleanupCo = StartCoroutine(ForceCleanupAfterDelay(_sessionId, 2f));
    }

    private IEnumerator ForceCleanupAfterDelay(int sessionId, float delaySec)
    {
        if (delaySec <= 0f)
            yield break;

        yield return new WaitForSecondsRealtime(delaySec);

        if (_isDestroying)
            yield break;

        // If a new session started, do nothing
        if (sessionId != _sessionId)
            yield break;

        // If still listening, do nothing
        if (IsListening)
            yield break;

        CleanupStreamSubscriptions();
        _forceCleanupCo = null;
    }

    // ===== Whisper 이벤트 핸들러 =====

    private void OnWhisperNewSegment(WhisperSegment segment)
    {
        if (!logSegments || segment == null) return;
        Debug.Log($"[WhisperSTT] 새 세그먼트: {segment.Text}");
    }

    private void OnStreamResultUpdated(string updatedResult)
    {
        if (_isDestroying) return;
        if (string.IsNullOrWhiteSpace(updatedResult)) return;

        var txt = updatedResult.Trim();
        if (logPartial) Debug.Log($"[WhisperSTT] Partial(updated): {txt}");

        EnqueueMainThread(() => EmitPartial(txt));
    }

    private void OnStreamSegmentUpdated(WhisperResult segment)
    {
        if (_isDestroying) return;
        if (segment == null || string.IsNullOrWhiteSpace(segment.Result)) return;

        var txt = segment.Result.Trim();
        if (logPartial) Debug.Log($"[WhisperSTT] Partial(segment): {txt}");

        EnqueueMainThread(() => EmitPartial(txt));
    }

    private void OnStreamSegmentFinished(WhisperResult segment)
    {
        if (_isDestroying) return;
        if (segment == null || string.IsNullOrWhiteSpace(segment.Result)) return;

        string segmentText = segment.Result.Trim();

        EnqueueMainThread(() =>
        {
            if (logSegments) Debug.Log($"[WhisperSTT] 세그먼트 완료: {segmentText}");

            if (!string.IsNullOrEmpty(_accumulatedText))
                _accumulatedText += " ";
            _accumulatedText += segmentText;

            OnResult?.Invoke(segmentText);
        });
    }

    private void OnStreamFinished(string finalResult)
    {
        if (_isDestroying) return;

        EnqueueMainThread(() =>
        {
            if (logSegments) Debug.Log($"[WhisperSTT] 스트림 종료. 최종 결과: {finalResult}");

            if (_forceCleanupCo != null)
            {
                StopCoroutine(_forceCleanupCo);
                _forceCleanupCo = null;
            }

            CleanupStreamSubscriptions();
            IsListening = false;

            if (!_stopNotified)
            {
                _stopNotified = true;
                OnStopped?.Invoke();
            }
        });
    }

    private void EmitPartial(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var trimmed = text.Trim();

        // 중복 방지
        if (trimmed == _lastPartialText)
            return;

        // 스로틀
        if (partialThrottleSec > 0f && Time.realtimeSinceStartup - _lastPartialEmitTime < partialThrottleSec)
        {
            _pendingPartialText = trimmed; // 최신 값으로 덮어쓰기
            return;
        }

        EmitPartialNow(trimmed);
    }

    private void EmitPartialNow(string trimmed)
    {
        if (string.IsNullOrWhiteSpace(trimmed)) return;

        _lastPartialText = trimmed;
        _lastPartialEmitTime = Time.realtimeSinceStartup;
        OnPartialResult?.Invoke(trimmed);
    }

    private void CleanupStreamSubscriptions()
    {
        if (_whisperStream != null)
        {
            _whisperStream.OnResultUpdated -= OnStreamResultUpdated;
            _whisperStream.OnSegmentUpdated -= OnStreamSegmentUpdated;
            _whisperStream.OnSegmentFinished -= OnStreamSegmentFinished;
            _whisperStream.OnStreamFinished -= OnStreamFinished;
            _whisperStream = null;
        }

        _pendingPartialText = null;

        _stopNotified = false;
        _sessionId++;
        if (_forceCleanupCo != null)
        {
            StopCoroutine(_forceCleanupCo);
            _forceCleanupCo = null;
        }
    }

    /// <summary>
    /// 언어 코드 정규화 (ko-KR -> ko, en-US -> en)
    /// </summary>
    private string NormalizeLanguageCode(string code)
    {
        if (string.IsNullOrEmpty(code))
            return "auto";

        if (code.Contains("-"))
        {
            code = code.Split('-')[0];
        }

        return code.ToLower();
    }

    private static void TryApplyWhisperManagerBool(object whisperManager, bool value, params string[] memberNames)
    {
        if (whisperManager == null || memberNames == null || memberNames.Length == 0)
            return;

        var t = whisperManager.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var name in memberNames)
        {
            try
            {
                var prop = t.GetProperty(name, flags);
                if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
                {
                    prop.SetValue(whisperManager, value);
                    return;
                }

                var field = t.GetField(name, flags);
                if (field != null && field.FieldType == typeof(bool))
                {
                    field.SetValue(whisperManager, value);
                    return;
                }
            }
            catch
            {
                // ignore
            }
        }

        Debug.LogWarning("[WhisperSTT] WhisperManager에서 GPU 토글 멤버를 찾지 못했습니다. whisper.unity 버전/필드명을 확인하세요.");
    }

    private static void TryInvokeStopStream(object stream)
    {
        if (stream == null) return;

        try
        {
            var t = stream.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var m = t.GetMethod("StopStream", flags);
            if (m != null && m.GetParameters().Length == 0)
            {
                m.Invoke(stream, null);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void SafeDestroyWhisperObjects()
    {
        CleanupStreamSubscriptions();

        if (_whisperManager != null)
        {
            _whisperManager.OnNewSegment -= OnWhisperNewSegment;
            var go = _whisperManager.gameObject;
            _whisperManager = null;
            if (go != null)
            {
                Destroy(go);
            }
        }

        if (_microphoneRecord != null)
        {
            try
            {
                _microphoneRecord.StopRecord();
            }
            catch
            {
                /* ignore */
            }
        }

        IsListening = false;
    }

    private void OnDestroy()
    {
        _isDestroying = true;

        if (IsListening)
        {
            StopListening();
        }

        SafeDestroyWhisperObjects();
    }
}
#endif