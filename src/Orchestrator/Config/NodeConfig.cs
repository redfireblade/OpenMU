// <copyright file="NodeConfig.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

using System.Collections.Frozen;

using System.Collections.Frozen;

/// <summary>Configuration container for a single node — maps JSON config keys to typed values.</summary>
public sealed class NodeConfig
{
    private readonly FrozenDictionary<string, object> _values;

    /// <summary>Initializes a new instance of the <see cref="NodeConfig"/> class.</summary>
    public NodeConfig(Dictionary<string, object> values)
    {
        this._values = values.ToFrozenDictionary();
    }

    /// <summary>Empty config (all getters return default values).</summary>
    public static NodeConfig Empty { get; } = new(new Dictionary<string, object>());

    /// <summary>Gets an integer value, or <paramref name="defaultValue"/> if not present or wrong type.</summary>
    public int GetInt(string key, int defaultValue = 0)
        => this._values.TryGetValue(key, out var v) && v is int i ? i : defaultValue;

    /// <summary>Gets a float value, or <paramref name="defaultValue"/> if not present or wrong type.</summary>
    public float GetFloat(string key, float defaultValue = 0f)
        => this._values.TryGetValue(key, out var v) && v is float f ? f : defaultValue;

    /// <summary>Gets a bool value, or <paramref name="defaultValue"/> if not present or wrong type.</summary>
    public bool GetBool(string key, bool defaultValue = false)
        => this._values.TryGetValue(key, out var v) && v is bool b ? b : defaultValue;

    /// <summary>Gets a string value, or <paramref name="defaultValue"/> if not present or wrong type.</summary>
    public string? GetString(string key, string? defaultValue = null)
        => this._values.TryGetValue(key, out var v) && v is string s ? s : defaultValue;
}
