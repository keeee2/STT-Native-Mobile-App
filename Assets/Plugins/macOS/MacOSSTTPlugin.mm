#import <Foundation/Foundation.h>

#if TARGET_OS_OSX
#import <Speech/Speech.h>
#import <AVFoundation/AVFoundation.h>
#import <AppKit/AppKit.h>

// Unity 콜백용
extern "C" {
    void UnitySendMessage(const char* obj, const char* method, const char* msg);
}

@interface MacOSSTTPlugin : NSObject <SFSpeechRecognizerDelegate>

@property (nonatomic, strong) SFSpeechRecognizer *speechRecognizer;
@property (nonatomic, strong) SFSpeechAudioBufferRecognitionRequest *recognitionRequest;
@property (nonatomic, strong) SFSpeechRecognitionTask *recognitionTask;
@property (nonatomic, strong) AVAudioEngine *audioEngine;
@property (nonatomic, assign) BOOL isListening;
@property (nonatomic, assign) BOOL isCancelling;
@property (nonatomic, strong) NSString *gameObjectName;
@property (nonatomic, strong) NSString *currentLanguage;

+ (instancetype)sharedInstance;

@end

@implementation MacOSSTTPlugin

+ (instancetype)sharedInstance {
    static MacOSSTTPlugin *instance = nil;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        instance = [[MacOSSTTPlugin alloc] init];
    });
    return instance;
}

- (instancetype)init {
    self = [super init];
    if (self) {
        _audioEngine = [[AVAudioEngine alloc] init];
        _isListening = NO;
        _isCancelling = NO;
        _gameObjectName = @"STTManager";
        _currentLanguage = @"ko-KR";
    }
    return self;
}

- (void)setGameObject:(NSString *)name {
    self.gameObjectName = name;
}

- (void)requestPermission {
    // macOS: 마이크 권한 요청 (10.14+)
    if (@available(macOS 10.14, *)) {
        AVAuthorizationStatus micStatus = [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio];
        
        if (micStatus == AVAuthorizationStatusNotDetermined) {
            [AVCaptureDevice requestAccessForMediaType:AVMediaTypeAudio completionHandler:^(BOOL granted) {
                if (granted) {
                    [self requestSpeechPermission];
                } else {
                    dispatch_async(dispatch_get_main_queue(), ^{
                        [self sendToUnity:@"OnSTTPermissionDenied" message:@"microphone"];
                    });
                }
            }];
        } else if (micStatus == AVAuthorizationStatusAuthorized) {
            [self requestSpeechPermission];
        } else {
            dispatch_async(dispatch_get_main_queue(), ^{
                [self sendToUnity:@"OnSTTPermissionDenied" message:@"microphone_denied"];
            });
        }
    } else {
        // macOS 10.14 미만: 권한 요청 불필요
        [self requestSpeechPermission];
    }
}

- (void)requestSpeechPermission {
    // 음성 인식 권한 (macOS 10.15+)
    if (@available(macOS 10.15, *)) {
        [SFSpeechRecognizer requestAuthorization:^(SFSpeechRecognizerAuthorizationStatus status) {
            dispatch_async(dispatch_get_main_queue(), ^{
                switch (status) {
                    case SFSpeechRecognizerAuthorizationStatusAuthorized:
                        [self sendToUnity:@"OnSTTPermissionGranted" message:@""];
                        break;
                    case SFSpeechRecognizerAuthorizationStatusDenied:
                        [self sendToUnity:@"OnSTTPermissionDenied" message:@"denied"];
                        break;
                    case SFSpeechRecognizerAuthorizationStatusRestricted:
                        [self sendToUnity:@"OnSTTPermissionDenied" message:@"restricted"];
                        break;
                    case SFSpeechRecognizerAuthorizationStatusNotDetermined:
                        [self sendToUnity:@"OnSTTPermissionDenied" message:@"not_determined"];
                        break;
                }
            });
        }];
    } else {
        [self sendToUnity:@"OnSTTPermissionDenied" message:@"unsupported_os"];
    }
}

- (void)startListening:(NSString *)languageCode {
    if (self.isListening) {
        return;
    }
    
    self.currentLanguage = languageCode;
    self.isCancelling = NO;
    
    // Locale 설정
    NSLocale *locale = [NSLocale localeWithLocaleIdentifier:languageCode];
    self.speechRecognizer = [[SFSpeechRecognizer alloc] initWithLocale:locale];
    self.speechRecognizer.delegate = self;
    
    if (!self.speechRecognizer.isAvailable) {
        [self sendToUnity:@"OnSTTError" message:@"Speech recognizer not available for this language"];
        return;
    }
    
    // 이전 태스크 정리
    [self cleanupRecognition];
    
    // 오디오 입력 설정 (macOS)
    AVAudioInputNode *inputNode = self.audioEngine.inputNode;
    AVAudioFormat *recordingFormat = [inputNode outputFormatForBus:0];
    
    // 샘플레이트 확인
    if (recordingFormat.sampleRate == 0) {
        [self sendToUnity:@"OnSTTError" message:@"No audio input device available"];
        return;
    }
    
    // 인식 요청 생성
    self.recognitionRequest = [[SFSpeechAudioBufferRecognitionRequest alloc] init];
    self.recognitionRequest.shouldReportPartialResults = YES;
    
    // macOS 10.15+에서 on-device 인식 가능하면 사용
    if (@available(macOS 10.15, *)) {
        if (self.speechRecognizer.supportsOnDeviceRecognition) {
            // 네트워크 사용 허용 (정확도 우선)
            self.recognitionRequest.requiresOnDeviceRecognition = NO;
        }
    }
    
    // 기존 tap 제거
    [inputNode removeTapOnBus:0];
    
    // 오디오 탭 설치
    [inputNode installTapOnBus:0 
                    bufferSize:1024 
                        format:recordingFormat 
                         block:^(AVAudioPCMBuffer *buffer, AVAudioTime *when) {
        if (self.recognitionRequest) {
            [self.recognitionRequest appendAudioPCMBuffer:buffer];
        }
    }];
    
    // 인식 태스크 시작
    __weak typeof(self) weakSelf = self;
    self.recognitionTask = [self.speechRecognizer recognitionTaskWithRequest:self.recognitionRequest
                                                               resultHandler:^(SFSpeechRecognitionResult *result, NSError *error) {
        __strong typeof(weakSelf) strongSelf = weakSelf;
        if (!strongSelf) return;
        
        BOOL isFinal = NO;
        
        if (result) {
            NSString *text = result.bestTranscription.formattedString;
            isFinal = result.isFinal;
            
            if (isFinal) {
                [strongSelf sendToUnity:@"OnSTTResult" message:text];
            } else {
                [strongSelf sendToUnity:@"OnSTTPartialResult" message:text];
            }
        }
        
        // 에러 처리 (cancel로 인한 에러는 무시)
        if (error && !strongSelf.isCancelling) {
            NSInteger errorCode = error.code;
            // 일반적인 cancel/중단 에러 무시
            if (errorCode != 216 && errorCode != 1110 && errorCode != 301) {
                [strongSelf sendToUnity:@"OnSTTError" message:error.localizedDescription];
            }
        }
        
        // 1분 제한으로 인한 자동 종료 시 재시작
        if (isFinal && strongSelf.isListening && !strongSelf.isCancelling) {
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.1 * NSEC_PER_SEC)), 
                           dispatch_get_main_queue(), ^{
                __strong typeof(weakSelf) innerSelf = weakSelf;
                if (innerSelf && innerSelf.isListening && !innerSelf.isCancelling) {
                    [innerSelf restartListening];
                }
            });
        }
    }];
    
    // 오디오 엔진 시작
    NSError *error;
    [self.audioEngine prepare];
    [self.audioEngine startAndReturnError:&error];
    
    if (error) {
        [self sendToUnity:@"OnSTTError" message:[NSString stringWithFormat:@"Audio engine error: %@", error.localizedDescription]];
        [self cleanupRecognition];
        return;
    }
    
    self.isListening = YES;
    [self sendToUnity:@"OnSTTStarted" message:@""];
    [self sendToUnity:@"OnSTTReady" message:@""];
}

- (void)restartListening {
    // 현재 인식 정리
    [self cleanupRecognition];
    
    // 약간의 딜레이 후 재시작
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.2 * NSEC_PER_SEC)), 
                   dispatch_get_main_queue(), ^{
        if (self.isListening && !self.isCancelling) {
            // 새 인식 요청 생성
            self.recognitionRequest = [[SFSpeechAudioBufferRecognitionRequest alloc] init];
            self.recognitionRequest.shouldReportPartialResults = YES;
            
            // 오디오 입력 재설정
            AVAudioInputNode *inputNode = self.audioEngine.inputNode;
            AVAudioFormat *recordingFormat = [inputNode outputFormatForBus:0];
            
            [inputNode removeTapOnBus:0];
            [inputNode installTapOnBus:0 
                            bufferSize:1024 
                                format:recordingFormat 
                                 block:^(AVAudioPCMBuffer *buffer, AVAudioTime *when) {
                if (self.recognitionRequest) {
                    [self.recognitionRequest appendAudioPCMBuffer:buffer];
                }
            }];
            
            // 새 인식 태스크
            __weak typeof(self) weakSelf = self;
            self.recognitionTask = [self.speechRecognizer recognitionTaskWithRequest:self.recognitionRequest
                                                                       resultHandler:^(SFSpeechRecognitionResult *result, NSError *error) {
                __strong typeof(weakSelf) strongSelf = weakSelf;
                if (!strongSelf) return;
                
                BOOL isFinal = NO;
                
                if (result) {
                    NSString *text = result.bestTranscription.formattedString;
                    isFinal = result.isFinal;
                    
                    if (isFinal) {
                        [strongSelf sendToUnity:@"OnSTTResult" message:text];
                    } else {
                        [strongSelf sendToUnity:@"OnSTTPartialResult" message:text];
                    }
                }
                
                if (error && !strongSelf.isCancelling) {
                    NSInteger errorCode = error.code;
                    if (errorCode != 216 && errorCode != 1110 && errorCode != 301) {
                        [strongSelf sendToUnity:@"OnSTTError" message:error.localizedDescription];
                    }
                }
                
                // 재시작 로직
                if (isFinal && strongSelf.isListening && !strongSelf.isCancelling) {
                    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.1 * NSEC_PER_SEC)), 
                                   dispatch_get_main_queue(), ^{
                        __strong typeof(weakSelf) innerSelf = weakSelf;
                        if (innerSelf && innerSelf.isListening && !innerSelf.isCancelling) {
                            [innerSelf restartListening];
                        }
                    });
                }
            }];
            
            // 오디오 엔진 재시작
            NSError *error;
            [self.audioEngine prepare];
            [self.audioEngine startAndReturnError:&error];
            
            if (error) {
                [self sendToUnity:@"OnSTTError" message:[NSString stringWithFormat:@"Restart error: %@", error.localizedDescription]];
            }
        }
    });
}

- (void)stopListening {
    if (!self.isListening) {
        return;
    }
    
    self.isCancelling = YES;
    self.isListening = NO;
    
    // 오디오 엔진 정지
    if (self.audioEngine.isRunning) {
        [self.audioEngine stop];
    }
    [self.audioEngine.inputNode removeTapOnBus:0];
    
    // 인식 종료
    if (self.recognitionRequest) {
        [self.recognitionRequest endAudio];
    }
    
    [self cleanupRecognition];
    
    [self sendToUnity:@"OnSTTStopped" message:@""];
    
    // 플래그 해제 (딜레이)
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.3 * NSEC_PER_SEC)), 
                   dispatch_get_main_queue(), ^{
        self.isCancelling = NO;
    });
}

- (void)cleanupRecognition {
    if (self.recognitionTask) {
        [self.recognitionTask cancel];
        self.recognitionTask = nil;
    }
    self.recognitionRequest = nil;
}

- (void)sendToUnity:(NSString *)method message:(NSString *)message {
    UnitySendMessage([self.gameObjectName UTF8String], [method UTF8String], [message UTF8String]);
}

#pragma mark - SFSpeechRecognizerDelegate

- (void)speechRecognizer:(SFSpeechRecognizer *)speechRecognizer availabilityDidChange:(BOOL)available {
    if (!available && self.isListening) {
        [self sendToUnity:@"OnSTTError" message:@"Speech recognition became unavailable"];
    }
}

@end

// ===== C 인터페이스 (Unity에서 호출) =====
// iOS와 동일한 함수명 사용하여 STTManager에서 통합 처리

extern "C" {
    void _STTSetGameObjectName(const char* name) {
        [[MacOSSTTPlugin sharedInstance] setGameObject:[NSString stringWithUTF8String:name]];
    }
    
    void _STTRequestPermission() {
        [[MacOSSTTPlugin sharedInstance] requestPermission];
    }
    
    void _STTStartListening(const char* languageCode) {
        [[MacOSSTTPlugin sharedInstance] startListening:[NSString stringWithUTF8String:languageCode]];
    }
    
    void _STTStopListening() {
        [[MacOSSTTPlugin sharedInstance] stopListening];
    }
    
    // macOS 전용: 설치된 언어 확인
    bool _STTIsLanguageAvailable(const char* languageCode) {
        NSLocale *locale = [NSLocale localeWithLocaleIdentifier:[NSString stringWithUTF8String:languageCode]];
        SFSpeechRecognizer *recognizer = [[SFSpeechRecognizer alloc] initWithLocale:locale];
        return recognizer.isAvailable;
    }
}

#endif // TARGET_OS_OSX
