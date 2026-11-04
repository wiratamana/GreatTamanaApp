using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Threading;
using GTA.Tools;

namespace GTA.App;

public partial class LlmChatWindow
{
    // ── Public agent API ──────────────────────────────────────────────────────

    /// <summary>Returns the name of the currently selected agent.</summary>
    public string GetCurrentAgentName() =>
        _selectedAgent?.Name ?? AgentRegistry.DefaultAgent.Name;

    /// <summary>Change the active agent.</summary>
    public bool SetCurrentAgent(string agentName)
    {
        var agent = AgentRegistry.TryGet(agentName);
        if (agent == null) return false;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetCurrentAgentOnUiThread(agent));
        }
        else
        {
            SetCurrentAgentOnUiThread(agent);
        }
        return true;
    }

    private void SetCurrentAgentOnUiThread(AgentDefinition agent)
    {
        _selectedAgent = agent;

        if (_agentSelectorCombo != null)
        {
            foreach (var item in _agentSelectorCombo.Items)
            {
                if (item is string s &&
                    string.Equals(s, agent.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _agentSelectorCombo.SelectedItem = s;
                    break;
                }
            }
        }

        AppendLog($"[Agent] Switched to '{agent.Name}'");
    }

    /// <summary>Refreshes the agent selector ComboBox from AgentRegistry.All.</summary>
    public void RefreshAgentSelector()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshAgentSelector);
            return;
        }

        if (_agentSelectorCombo == null) return;

        string? current = _selectedAgent?.Name;
        _agentSelectorCombo.Items.Clear();

        foreach (var kv in AgentRegistry.All)
            _agentSelectorCombo.Items.Add(kv.Key);

        // Restore selection
        if (current != null)
        {
            foreach (var item in _agentSelectorCombo.Items)
            {
                if (item is string s &&
                    string.Equals(s, current, StringComparison.OrdinalIgnoreCase))
                {
                    _agentSelectorCombo.SelectedItem = s;
                    return;
                }
            }
        }

        // Fallback: select default
        foreach (var item in _agentSelectorCombo.Items)
        {
            if (item is string s &&
                string.Equals(s, AgentRegistry.DefaultAgent.Name, StringComparison.OrdinalIgnoreCase))
            {
                _agentSelectorCombo.SelectedItem = s;
                return;
            }
        }
    }

    // ── Agent-aware external send API ─────────────────────────────────────────

    /// <summary>Send a plain text message externally with a specific agent override.</summary>
    public bool SendMessageExternalWithAgent(string message, string agentName)
    {
        var agent = AgentRegistry.TryGet(agentName);
        if (agent == null) return false;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SendMessageExternalWithAgent(message, agentName));
            return true;  // already validated
        }

        _pendingAgentOverride = agent;
        _inputBox.Text        = message;
        SendCurrentMessage();
        return true;
    }

    /// <summary>Send a message with file attachments and a specific agent override.</summary>
    public bool SendMessageExternalWithFilesAndAgent(
        string       message,
        List<string> files,
        string       agentName)
    {
        var agent = AgentRegistry.TryGet(agentName);
        if (agent == null) return false;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
                SendMessageExternalWithFilesAndAgent(message, files, agentName));
            return true;
        }

        _pendingAgentOverride = agent;
        _pendingAttachments.Clear();
        foreach (var f in files)
            if (File.Exists(f)) _pendingAttachments.Add(f);

        RefreshAttachmentStrip();
        _inputBox.Text = message;
        SendCurrentMessage();
        return true;
    }
}
