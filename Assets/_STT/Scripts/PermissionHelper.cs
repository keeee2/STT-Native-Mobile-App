using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class PermissionHelper : MonoBehaviour
{
    public void RequestPermission() => RequestMicrophonePermission();

    public static bool HasMicrophonePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
        return true;
#endif
    }

    public static void RequestMicrophonePermission(System.Action onGranted = null, System.Action onDenied = null)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += (permission) => onGranted?.Invoke();
            callbacks.PermissionDenied += (permission) => onDenied?.Invoke();
            callbacks.PermissionDeniedAndDontAskAgain += (permission) => onDenied?.Invoke();
            
            Permission.RequestUserPermission(Permission.Microphone, callbacks);
        }
        else
        {
            onGranted?.Invoke();
        }
#else
        onGranted?.Invoke();
#endif
    }
}