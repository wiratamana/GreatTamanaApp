using System;

namespace GTA.Tools
{
    /// <summary>Marks a static, parameterless method that returns <see cref="AgentDefinition"/> as a registered agent factory.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class AgentInfoAttribute : Attribute
    {
        public string Name { get; }

        public AgentInfoAttribute(string name) => Name = name;
    }

    /// <summary>Marks the agent method as the <em>default</em> agent returned by <see cref="AgentRegistry.DefaultAgent"/>.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class DefaultAgentAttribute : Attribute { }
}
