#import <Foundation/Foundation.h>
#import <Speech/Speech.h>
#import <AVFoundation/AVFoundation.h>

// Unity 콜백용
extern "C" {
    void UnitySendMessage(const char* obj, const char* method, const char* msg);
}

@interface UnitySTTPlugin : NSObject <SFSpeechRecognizerDelegate>

@property (nonatomic, strong) SFSpeechRecognizer *speechRecognizer;
@property (nonatomic, strong) SFSpeechAudioBufferRecognitionRequest *recognitionRequest;
@property (nonatomic, strong) SFSpeechRecognitionTask *recognitionTask;
@property (nonatomic, strong) AVAudioEngine *audioEngine;
@property (nonatomic, assign) BOOL isListening;
@property (nonatomic, strong) NSString *gameObjectName;

+ (instancetype)sharedInstance;

@end

@implementation UnitySTTPlugin

+ (instancetype)sharedInstance {
    static UnitySTTPlugin *instance = nil;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        instance = [[UnitySTTPlugin alloc] init];
    });
    return instance;
}

- (instancetype)init {
    self = [super init];
    if (self) {
        _audioEngine = [[AVAudioEngine alloc] init];
        _isListening = NO;
        _gameObjectName = @"STTManager";
    }
    return self;
}

- (void)setGameObject:(NSString *)name {
    self.gameObjectName = name;
}

- (void)requestPermission {
    // 마이크 권한
    [AVAudioSession.sharedInstance requestRecordPermission:^(BOOL granted) {
        if (granted) {
            // 음성 인식 권한
            [SFSpeechRecognizer requestAuthorization:^(SFSpeechRecognizerAuthorizationStatus status) {
                dispatch_async(dispatch_get_main_queue(), ^{
                    if (status == SFSpeechRecognizerAuthorizationStatusAuthorized) {
                        [self sendToUnity:@"OnSTTPermissionGranted" message:@""];
                    } else {
                        [self sendToUnity:@"OnSTTPermissionDenied" message:@""];
                    }
                });
            }];
        } else {
            [self sendToUnity:@"OnSTTPermissionDenied" message:@"microphone"];
        }
    }];
}

- (void)startListening:(NSString *)languageCode {
    if (self.isListening) {
        return;
    }
    
    // Locale 설정
    NSLocale *locale = [NSLocale localeWithLocaleIdentifier:languageCode];
    self.speechRecognizer = [[SFSpeechRecognizer alloc] initWithLocale:locale];
    self.speechRecognizer.delegate = self;
    
    // 이전 태스크 정리
    if (self.recognitionTask) {
        [self.recognitionTask cancel];
        self.recognitionTask = nil;
    }
    
    // 오디오 세션 설정
    NSError *error;
    AVAudioSession *audioSession = [AVAudioSession sharedInstance];
    [audioSession setCategory:AVAudioSessionCategoryRecord 
                         mode:AVAudioSessionModeMeasurement 
                      options:AVAudioSessionCategoryOptionDuckOthers 
                        error:&error];
    [audioSession setActive:YES withOptions:AVAudioSessionSetActiveOptionNotifyOthersOnDeactivation error:&error];
    
    // 인식 요청 생성
    self.recognitionRequest = [[SFSpeechAudioBufferRecognitionRequest alloc] init];
    self.recognitionRequest.shouldReportPartialResults = YES;
    
    // 오디오 입력 설정
    AVAudioInputNode *inputNode = self.audioEngine.inputNode;
    AVAudioFormat *recordingFormat = [inputNode outputFormatForBus:0];
    
    [inputNode installTapOnBus:0 
                    bufferSize:1024 
                        format:recordingFormat 
                         block:^(AVAudioPCMBuffer *buffer, AVAudioTime *when) {
        [self.recognitionRequest appendAudioPCMBuffer:buffer];
    }];
    
    // 인식 태스크 시작
    self.recognitionTask = [self.speechRecognizer recognitionTaskWithRequest:self.recognitionRequest
                                                               resultHandler:^(SFSpeechRecognitionResult *result, NSError *error) {
        if (result) {
            NSString *text = result.bestTranscription.formattedString;
            
            if (result.isFinal) {
                [self sendToUnity:@"OnSTTResult" message:text];
            } else {
                [self sendToUnity:@"OnSTTPartialResult" message:text];
            }
        }
        
        if (error) {
            [self sendToUnity:@"OnSTTError" message:error.localizedDescription];
        }
    }];
    
    // 오디오 엔진 시작
    [self.audioEngine prepare];
    [self.audioEngine startAndReturnError:&error];
    
    if (error) {
        [self sendToUnity:@"OnSTTError" message:error.localizedDescription];
        return;
    }
    
    self.isListening = YES;
    [self sendToUnity:@"OnSTTStarted" message:@""];
    [self sendToUnity:@"OnSTTReady" message:@""];
}

- (void)stopListening {
    if (!self.isListening) {
        return;
    }
    
    [self.audioEngine stop];
    [self.audioEngine.inputNode removeTapOnBus:0];
    
    [self.recognitionRequest endAudio];
    
    if (self.recognitionTask) {
        [self.recognitionTask cancel];
        self.recognitionTask = nil;
    }
    
    self.isListening = NO;
    [self sendToUnity:@"OnSTTStopped" message:@""];
}

- (void)sendToUnity:(NSString *)method message:(NSString *)message {
    UnitySendMessage([self.gameObjectName UTF8String], [method UTF8String], [message UTF8String]);
}

#pragma mark - SFSpeechRecognizerDelegate

- (void)speechRecognizer:(SFSpeechRecognizer *)speechRecognizer availabilityDidChange:(BOOL)available {
    if (!available) {
        [self sendToUnity:@"OnSTTError" message:@"Speech recognition not available"];
    }
}

@end

// ===== C 인터페이스 (Unity에서 호출) =====

extern "C" {
    void _STTSetGameObjectName(const char* name) {
        [[UnitySTTPlugin sharedInstance] setGameObject:[NSString stringWithUTF8String:name]];
    }
    
    void _STTRequestPermission() {
        [[UnitySTTPlugin sharedInstance] requestPermission];
    }
    
    void _STTStartListening(const char* languageCode) {
        [[UnitySTTPlugin sharedInstance] startListening:[NSString stringWithUTF8String:languageCode]];
    }
    
    void _STTStopListening() {
        [[UnitySTTPlugin sharedInstance] stopListening];
    }
}
