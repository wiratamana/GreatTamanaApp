using System;
using GTA.Core;

namespace GTA.App;

// Plain net10.0 stub (no WinRT): toasts are only written to the session log.
internal static class ToastNotifier
{
    public static void Register() { }

    public static void Unregister() { }

    public static void ShowToast(string title, string message)
    {
        try
        {
            SessionFileLogger.Log("ToastNotifier", $"{title}: {message}");
        }
        catch (Exception)
        {
        }
    }
}
