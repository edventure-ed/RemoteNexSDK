using System;
using UnityEngine;

public static class RemoteNex
{
    public static event Action<string> OnInputReceived;

    public static void TriggerInput(string data)
    {
        OnInputReceived?.Invoke(data);
    }
}