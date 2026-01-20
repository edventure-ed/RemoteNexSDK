using System;
using System.Runtime.InteropServices;

public static class RemoteNex
{
    public static event Action<string> OnInputReceived;

    public static event Action<string> OnDataSent;

    [DllImport("__Internal")]
    private static extern void SendDataToReact(string str);

    public static void TriggerInput(string data)
    {
        OnInputReceived?.Invoke(data);
    }

    public static void SendData(string data)
    {
        OnDataSent?.Invoke(data);

#if UNITY_WEBGL && !UNITY_EDITOR
            // WebGL'de çalýþýyorsak Jslib'i çaðýr
            SendDataToReact(data);
#else
        UnityEngine.Debug.Log("Web'e Gönderildi: " + data);
#endif
    }
}