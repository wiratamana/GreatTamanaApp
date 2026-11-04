using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using GTA.Tools;

namespace GTA.App;

internal static class ToolPreloadCatalog
{
    internal sealed class Category
    {
        public string Name { get; }
        public string Description { get; }
        public Func<string, bool> Matches { get; }

        public Category(string name, string description, Func<string, bool> matches)
        {
            Name        = name;
            Description = description;
            Matches     = matches;
        }
    }

    private static bool HasAnyTag(string toolName, params string[] tags)
    {
        string[] toolTags = ToolRegistry.GetTagsForTool(toolName);
        if (toolTags.Length == 0) return false;
        foreach (string t in tags)
            if (toolTags.Contains(t, StringComparer.OrdinalIgnoreCase))
                return true;
        return false;
    }

    internal static readonly Category[] Categories =
    {
        new Category(
            "io",
            "File & directory operations: read/write/edit/search files, browse & manage directories, zip/unzip archives.",
            name => HasAnyTag(name, "essential", "io-specialist", "directory", "archive")),

#if !CO_WORKER_USE
        new Category(
            "dotnet",
            "Build, run, and scaffold .NET console projects; enumerate C# source files.",
            name => name.StartsWith("dotnet_", StringComparison.OrdinalIgnoreCase)),

        new Category(
            "git",
            "Local git repository operations: init/status/config, stage/commit/rm/mv, branch/switch/merge/rebase, " +
            "restore/reset/revert/clean, stash, log/diff/show/tag, push/pull/fetch/clone, GitHub pull requests, " +
            "and GitHub Issues (list/read/comment/open-close).",
            name => HasAnyTag(name, "git")),
#endif

        new Category(
            "box",
            "Box.com cloud storage: browse folders, search, download/read docx, xlsx, pptx files and embedded images.",
            name => HasAnyTag(name, "box_api")),

        new Category(
            "jira",
            "JIRA issue tracker: read tickets, read attachments (docx/xlsx/pptx/images/video), post comments.",
            name => HasAnyTag(name, "jira_api")),

        new Category(
            "xlsx",
            "Edit a LOCAL .xlsx file on disk: query sheets, read/write cell values, create/rename sheets, merge/unmerge ranges.",
            name => HasAnyTag(name, "xlsx_local")),

        new Category(
            "pptx",
            "Build/edit a LOCAL .pptx file on disk: slides, shapes, connectors, tables, images, text boxes, lists, PNG render.",
            name => HasAnyTag(name, "pptx_local")),

        new Category(
            "ibmcloud",
            "IBM Cloud Logs: keyword/thread search, match-count stats, automated root-cause trace investigation, evidence export.",
            name => HasAnyTag(name, "ibm_cloud_api")),

#if !CO_WORKER_USE
        new Category(
            "avalonia",
            "Control the local Avalonia desktop chat UI: launch/close, screenshots, send messages, switch agents.",
            name => HasAnyTag(name, "avalonia")),
#endif

#if CO_WORKER_USE
        new Category(
            "external",
            "load_image and local HTTP request.",
            name => HasAnyTag(name, "image", "http")),
#else
        new Category(
            "external",
            "Shell commands, gcc/g++, cmake, load_image, and background app launch/stop.",
            name => HasAnyTag(name, "shell", "image", "gcc", "cmake", "process")),
#endif

        new Category(
            "encyclopedia",
            "Persistent on-disk knowledge base: write free-text entries and query them via Gemini for fact-only answers.",
            name => HasAnyTag(name, "encyclopedia")),

        new Category(
            "bug_report",
            "Persistent on-disk bug log for tool malfunctions only.",
            name => HasAnyTag(name, "bug_report")),

        new Category(
            "interactive",
            "Tools that let the AI pause and ask YOU something mid-turn (a window opens; the " +
            "agent waits for your answer). Not discoverable by the AI itself — only ever enabled " +
            "here, by a human.",
            name => HasAnyTag(name, "interactive")),
    };

    /// <summary>All registered tool names, alphabetically, case-insensitive order.</summary>
    internal static List<string> AllToolNames() =>
        ToolRegistry.Tools.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Tool names belonging to one category, alphabetically.</summary>
    internal static List<string> ToolsIn(Category c) =>
        ToolRegistry.Tools.Keys.Where(c.Matches).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Registered tool names that match none of the categories.</summary>
    internal static List<string> Uncategorized()
    {
        var categorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Category c in Categories)
            foreach (string n in ToolsIn(c))
                categorized.Add(n);

        return ToolRegistry.Tools.Keys
            .Where(n => !categorized.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Short description for one tool (from its schema entry), or null if unknown.</summary>
    internal static string? DescriptionFor(string toolName)
    {
        JsonObject? entry = ToolRegistry.TryGetSchemaEntry(toolName);
        return entry?["description"]?.GetValue<string>();
    }
}
