// <copyright file="GraphConfig.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

using System.Collections.Frozen;

using System.Collections.Frozen;

/// <summary>
/// Serializable graph-level configuration.
/// Supports per-node enable/disable switches and per-node parameter dictionaries.
/// </summary>
public sealed class GraphConfig
{
    /// <summary>Per-node enable overrides. Key = node name, Value = enabled.</summary>
    public Dictionary<string, bool> Enabled { get; init; } = new();

    /// <summary>Per-node configuration parameters. Key = node name, Value = parameter dictionary.</summary>
    public Dictionary<string, Dictionary<string, object>> NodeConfigs { get; init; } = new();

    /// <summary>Returns whether a node is enabled, falling back to <paramref name="defaultEnabled"/>.</summary>
    public bool IsEnabled(string nodeName, bool defaultEnabled)
        => this.Enabled.TryGetValue(nodeName, out var enabled) ? enabled : defaultEnabled;

    /// <summary>Returns the configuration for a node, or empty config if not found.</summary>
    public NodeConfig GetNodeConfig(string nodeName)
    {
        if (this.NodeConfigs.TryGetValue(nodeName, out var cfg))
        {
            return new NodeConfig(cfg);
        }

        return NodeConfig.Empty;
    }
}
