using UnityEngine;
using UnityEngine.Events;

public class RemoteNexRelay : MonoBehaviour
{
    [System.Serializable]
    public class StringEvent : UnityEvent<string> { }

    [Header("🔗 Bağlantı")]
    [Tooltip("SDK'dan veri geldiğinde bu olay tetiklenir.")]
    public StringEvent OnInputReceived;

    void OnEnable()
    {
        RemoteNex.OnInputReceived += RelayData;
    }

    void OnDisable()
    {
        RemoteNex.OnInputReceived -= RelayData;
    }

    void RelayData(string data)
    {
        if (OnInputReceived != null)
        {
            OnInputReceived.Invoke(data);
        }
    }
}