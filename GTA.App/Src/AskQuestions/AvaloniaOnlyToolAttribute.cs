using System;

namespace GTA.App;

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class AvaloniaOnlyToolAttribute : Attribute
{
    /// <summary>Exact tool name the LLM will use to call it.</summary>
    public string Name { get; }

    /// <summary>Tool description — appears in the generated JSON schema.</summary>
    public string Description { get; }

    /// <summary>Optional comma-separated category tag(s), mirroring GTA.Tools' [ToolTag] — e.g. "interactive".</summary>
    public string? Tags { get; set; } = null;

    public bool Hidden { get; set; } = true;

    public bool FileMutating { get; set; } = false;

    public AvaloniaOnlyToolAttribute(string name, string description)
    {
        Name        = name;
        Description = description;
    }
}
