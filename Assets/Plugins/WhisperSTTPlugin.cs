#if UNITY_EDITOR || UNITY_STANDALONE
using System;
using System.IO;
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
    private const string MODEL_FOLDER_NAME = "WhisperModels";
    private const string DEFAULT_MODEL_NAME = "ggml-small.bin";

    [Header("Whisper 설정")]
    [Tooltip("Whisper 모델 파일명 (WhisperModels 폴더 내)")]
    [SerializeField] private string modelFileName = DEFAULT_MODEL_NAME;

    [Tooltip("GPU 가속 사용 (Vulkan/Metal)")]
    [SerializeField] private bool useGpu = false;

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
        // Standalone: 실행 파일 옆
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

    private System.Collections.IEnumerator InitializeCoroutine()
    {
        // 모델 경로 확인
        string modelFolderPath = GetModelFolderPath();
        string fullModelPath = Path.Combine(modelFolderPath, modelFileName);

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
        whisperGo.SetActive(false); // 먼저 비활성화!
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

        // 이벤트 구독
        _whisperManager.OnNewSegment += OnWhisperNewSegment;

        // ⭐ 이제 활성화 → Awake 호출 → 설정된 경로로 InitModel 실행
        whisperGo.SetActive(true);

        Debug.Log("[WhisperSTT] 모델 로딩 시작...");

        // 모델 로드 완료 대기
        while (_whisperManager.IsLoading)
        {
            yield return null;
        }

        if (!_whisperManager.IsLoaded)
        {
            Debug.LogError("[WhisperSTT] 모델 로드 실패");
            OnError?.Invoke("모델 로드 실패");
            OnInitialized?.Invoke(false);
            yield break;
        }

        // MicrophoneRecord 생성
        _microphoneRecord = gameObject.AddComponent<MicrophoneRecord>();

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

        // 언어 코드 정규화 (ko-KR -> ko)
        _currentLanguage = NormalizeLanguageCode(languageCode);
        _whisperManager.language = _currentLanguage;

        // 누적 텍스트 초기화
        _accumulatedText = "";

        try
        {
            // 마이크 녹음 시작
            _microphoneRecord.StartRecord();

            // WhisperStream 생성
            _whisperStream = await _whisperManager.CreateStream(_microphoneRecord);

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

            Debug.Log($"[WhisperSTT] 인식 시작 (언어: {_currentLanguage})");
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

        try
        {
            // 마이크 녹음 중지 (이게 StopStream도 트리거함)
            _microphoneRecord?.StopRecord();
        }
        catch (Exception e)
        {
            Debug.LogError($"[WhisperSTT] 중지 실패: {e.Message}");
        }
    }

    // ===== Whisper 이벤트 핸들러 =====

    private void OnWhisperNewSegment(WhisperSegment segment)
    {
        // 새 세그먼트 (실시간 콜백)
        Debug.Log($"[WhisperSTT] 새 세그먼트: {segment.Text}");
    }

    private void OnStreamResultUpdated(string updatedResult)
    {
        // 전체 결과 업데이트 (partial)
        if (!string.IsNullOrEmpty(updatedResult))
        {
            OnPartialResult?.Invoke(updatedResult.Trim());
        }
    }

    private void OnStreamSegmentUpdated(WhisperResult segment)
    {
        // 세그먼트 업데이트 중
        if (segment != null && !string.IsNullOrEmpty(segment.Result))
        {
            OnPartialResult?.Invoke(segment.Result.Trim());
        }
    }

    private void OnStreamSegmentFinished(WhisperResult segment)
    {
        // 세그먼트 완료 (final result for this segment)
        if (segment != null && !string.IsNullOrEmpty(segment.Result))
        {
            string segmentText = segment.Result.Trim();
            Debug.Log($"[WhisperSTT] 세그먼트 완료: {segmentText}");

            // 누적
            if (!string.IsNullOrEmpty(_accumulatedText))
                _accumulatedText += " ";
            _accumulatedText += segmentText;

            OnResult?.Invoke(segmentText);
        }
    }

    private void OnStreamFinished(string finalResult)
    {
        // 스트림 완전 종료
        Debug.Log($"[WhisperSTT] 스트림 종료. 최종 결과: {finalResult}");

        // 이벤트 구독 해제
        if (_whisperStream != null)
        {
            _whisperStream.OnResultUpdated -= OnStreamResultUpdated;
            _whisperStream.OnSegmentUpdated -= OnStreamSegmentUpdated;
            _whisperStream.OnSegmentFinished -= OnStreamSegmentFinished;
            _whisperStream.OnStreamFinished -= OnStreamFinished;
            _whisperStream = null;
        }

        IsListening = false;
        OnStopped?.Invoke();
    }

    /// <summary>
    /// 언어 코드 정규화 (ko-KR -> ko, en-US -> en)
    /// </summary>
    private string NormalizeLanguageCode(string code)
    {
        if (string.IsNullOrEmpty(code))
            return "auto";

        // ISO 코드에서 언어 부분만 추출
        if (code.Contains("-"))
        {
            code = code.Split('-')[0];
        }

        return code.ToLower();
    }

    private void OnDestroy()
    {
        if (IsListening)
        {
            StopListening();
        }

        if (_whisperManager != null)
        {
            _whisperManager.OnNewSegment -= OnWhisperNewSegment;
        }
    }
}
#endif