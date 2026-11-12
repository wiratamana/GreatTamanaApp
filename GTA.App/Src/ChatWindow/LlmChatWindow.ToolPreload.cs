using Avalonia.Threading;

namespace GTA.App;

public partial class LlmChatWindow
{
    /// <summary>Opens (or re-activates) the Pre-load Tools popup for the active session.</summary>
    private async void OpenPreloadToolsPopup()
    {
        var session = _current;
        if (session == null) return;

        if (_toolPreloadPopup != null)
        {
            if (ReferenceEquals(_toolPreloadPopupSession, session))
            {
                _toolPreloadPopup.Activate();
                return;
            }

            _toolPreloadPopup.Close();
        }

        var popup = new ToolPreloadPopupWindow(
            session.Runtime.PreloadedToolNames, RefreshPreloadToolsButtonLabel, session.Title);
        _toolPreloadPopup        = popup;
        _toolPreloadPopupSession = session;
        popup.Closed += (_, _) =>
        {
            _toolPreloadPopup        = null;
            _toolPreloadPopupSession = null;

            // Write the pick to disk now. Without this it is saved only after the next turn,
            // so closing the app before sending a message lost the pick.
            ConversationPersistence.SaveSession(session);
        };

        await popup.ShowDialog(this);
    }

    /// <summary>Updates the header Tools button label with the active session's preload count (thread-safe).</summary>
    internal void RefreshPreloadToolsButtonLabel()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshPreloadToolsButtonLabel);
            return;
        }

        if (_preloadToolsButton == null) return;

        int count = _current?.Runtime.PreloadedToolNames.Count ?? 0;
        _preloadToolsButton.Content = count > 0 ? $"🧩 Tools ({count})" : "🧩 Tools";
    }
}
