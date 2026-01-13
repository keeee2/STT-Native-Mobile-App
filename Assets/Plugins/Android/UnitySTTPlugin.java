package com.voyager.eterna.stt;

import android.content.Context;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.speech.RecognitionListener;
import android.speech.RecognizerIntent;
import android.speech.SpeechRecognizer;
import com.unity3d.player.UnityPlayer;
import java.util.ArrayList;
import java.util.Locale;

public class UnitySTTPlugin {
    private static UnitySTTPlugin instance;
    private SpeechRecognizer recognizer;
    private boolean isListening = false;
    private Handler mainHandler;
    private String gameObjectName = "STTManager";
    private String currentLanguage = "ko-KR";

    public static UnitySTTPlugin getInstance() {
        if (instance == null) {
            instance = new UnitySTTPlugin();
        }
        return instance;
    }

    private UnitySTTPlugin() {
        mainHandler = new Handler(Looper.getMainLooper());
    }

    public void setGameObjectName(String name) {
        this.gameObjectName = name;
    }

    public void startListening(String languageCode) {
        this.currentLanguage = languageCode;
        mainHandler.post(() -> {
            Context context = UnityPlayer.currentActivity;

            if (recognizer != null) {
                recognizer.destroy();
            }

            recognizer = SpeechRecognizer.createSpeechRecognizer(context);
            recognizer.setRecognitionListener(createListener());

            isListening = true;
            recognizer.startListening(createIntent(languageCode));

            sendToUnity("OnSTTStarted", "");
        });
    }

    public void stopListening() {
        mainHandler.post(() -> {
            isListening = false;
            if (recognizer != null) {
                // recognizer.stopListening();
                recognizer.cancel();
            }
            sendToUnity("OnSTTStopped", "");
        });
    }

    public void destroy() {
        mainHandler.post(() -> {
            isListening = false;
            if (recognizer != null) {
                recognizer.destroy();
                recognizer = null;
            }
        });
    }

    private Intent createIntent(String languageCode) {
        Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
        intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL,
                        RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
        intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE, languageCode);
        intent.putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true);
        intent.putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1);
        return intent;
    }

    private RecognitionListener createListener() {
        return new RecognitionListener() {
            @Override
            public void onReadyForSpeech(Bundle params) {
                sendToUnity("OnSTTReady", "");
            }

            @Override
            public void onBeginningOfSpeech() {
                sendToUnity("OnSTTBeginning", "");
            }

            @Override
            public void onRmsChanged(float rmsdB) {
            }

            @Override
            public void onBufferReceived(byte[] buffer) {
            }

            @Override
            public void onEndOfSpeech() {
                sendToUnity("OnSTTEndOfSpeech", "");
            }

            @Override
            public void onError(int error) {
                String errorMessage = getErrorMessage(error);
                sendToUnity("OnSTTError", errorMessage);

                if (isListening && (error == SpeechRecognizer.ERROR_NO_MATCH ||
                                    error == SpeechRecognizer.ERROR_SPEECH_TIMEOUT)) {
                    mainHandler.postDelayed(() -> {
                        if (isListening && recognizer != null) {
                            recognizer.startListening(createIntent(currentLanguage));
                        }
                    }, 100);
                }
            }

            @Override
            public void onResults(Bundle results) {
                ArrayList<String> matches = results.getStringArrayList(
                    SpeechRecognizer.RESULTS_RECOGNITION);

                if (matches != null && !matches.isEmpty()) {
                    String text = matches.get(0);
                    sendToUnity("OnSTTResult", text);
                }

                if (isListening) {
                    mainHandler.postDelayed(() -> {
                        if (isListening && recognizer != null) {
                            recognizer.startListening(createIntent(currentLanguage));
                        }
                    }, 100);
                }
            }

            @Override
            public void onPartialResults(Bundle partialResults) {
                ArrayList<String> matches = partialResults.getStringArrayList(
                    SpeechRecognizer.RESULTS_RECOGNITION);

                if (matches != null && !matches.isEmpty()) {
                    String text = matches.get(0);
                    sendToUnity("OnSTTPartialResult", text);
                }
            }

            @Override
            public void onEvent(int eventType, Bundle params) {
            }
        };
    }

    private String getErrorMessage(int error) {
        switch (error) {
            case SpeechRecognizer.ERROR_AUDIO:
                return "ERROR_AUDIO";
            case SpeechRecognizer.ERROR_CLIENT:
                return "ERROR_CLIENT";
            case SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS:
                return "ERROR_INSUFFICIENT_PERMISSIONS";
            case SpeechRecognizer.ERROR_NETWORK:
                return "ERROR_NETWORK";
            case SpeechRecognizer.ERROR_NETWORK_TIMEOUT:
                return "ERROR_NETWORK_TIMEOUT";
            case SpeechRecognizer.ERROR_NO_MATCH:
                return "ERROR_NO_MATCH";
            case SpeechRecognizer.ERROR_RECOGNIZER_BUSY:
                return "ERROR_RECOGNIZER_BUSY";
            case SpeechRecognizer.ERROR_SERVER:
                return "ERROR_SERVER";
            case SpeechRecognizer.ERROR_SPEECH_TIMEOUT:
                return "ERROR_SPEECH_TIMEOUT";
            default:
                return "ERROR_UNKNOWN";
        }
    }

    private void sendToUnity(String methodName, String message) {
        UnityPlayer.UnitySendMessage(gameObjectName, methodName, message);
    }
}
