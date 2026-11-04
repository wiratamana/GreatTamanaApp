#if CO_WORKER_USE
using System.Threading.Tasks;
using GTA.Core;

namespace GTA.App;

/// <summary>CO_WORKER_USE only: Settings window (encrypted credentials + additional system prompt). See SettingsWindow.</summary>
public partial class LlmChatWindow
{
    /// <summary>Opens the modal Settings window.</summary>
    internal async Task OpenSettingsWindowAsync()
    {
        var win = new SettingsWindow();
        bool saved = await win.ShowDialog<bool>(this);
        AppendLog(saved
            ? "[Settings] Settings saved (credentials encrypted; additional prompt in Credentials\\additional_system_prompt.txt)."
            : "[Settings] Closed without saving.");
    }

    /// <summary>
    /// Startup check. Credentials are OPTIONAL, so empty credentials never open Settings by themselves:
    /// the matching tool just reports "credential is not set" when it is used.
    /// Settings only opens on its own when the encrypted file cannot be read (the window shows why).
    /// </summary>
    internal async Task PromptForCredentialsIfNeededAsync()
    {
        if (SecureCredentialStore.Status == SecureStoreStatus.Ok)
        {
            if (SecureCredentialStore.UserCredentialsIncomplete())
                AppendLog("[Settings] Some credentials are empty. Services that need them cannot be used " +
                          "until you enter them (sidebar > Settings).");
            return;
        }

        AppendLog("[Settings] " + SecureCredentialStore.StatusMessage);
        await OpenSettingsWindowAsync();
    }
}
#endif
