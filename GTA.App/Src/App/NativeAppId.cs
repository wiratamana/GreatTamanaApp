using System;
using System.Runtime.InteropServices;
using GTA.Core;

namespace GTA.App;

// Sets a stable Windows AppUserModelID so the process keeps one identity across rebuilds.
internal static class NativeAppId
{
    private const string AppUserModelId = "GTA.App.LlmChat";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private static bool _applied;

    /// <summary>Sets the process-wide AUMID once.</summary>
    public static void SetCurrentProcessExplicitAppUserModelId()
    {
        if (_applied) return;

        try
        {
            int hr = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (hr == 0)
            {
                _applied = true;
            }
            else
            {
                SessionFileLogger.Log("NativeAppId",
                    $"SetCurrentProcessExplicitAppUserModelID failed, HRESULT=0x{hr:X8}.");
            }
        }
        catch (Exception ex)
        {
            SessionFileLogger.Log("NativeAppId", $"SetCurrentProcessExplicitAppUserModelID threw: {ex}");
        }
    }
}
