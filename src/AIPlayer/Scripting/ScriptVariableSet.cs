// <copyright file="ScriptVariableSet.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using System.Collections.Generic;

/// <summary>
/// Per-character runtime variable store. Persists across ticks.
/// All variables are integers. Thread-safety: not required (AI tick is single-threaded).
/// </summary>
public sealed class ScriptVariableSet
{
    private readonly Dictionary<string, int> _vars = new();

    /// <summary>
    /// Gets the value of a variable. Returns 0 if the variable doesn't exist.
    /// Variable names are case-insensitive.
    /// </summary>
    public int Get(string name)
    {
        return this._vars.TryGetValue(Normalize(name), out var value) ? value : 0;
    }

    /// <summary>
    /// Sets a variable to the specified value.
    /// </summary>
    public void Set(string name, int value)
    {
        this._vars[Normalize(name)] = value;
    }

    /// <summary>
    /// Increments a variable by delta (default 1). Creates the variable with value 0 if it doesn't exist.
    /// </summary>
    public void Inc(string name, int delta = 1)
    {
        var key = Normalize(name);
        this._vars.TryGetValue(key, out var current);
        this._vars[key] = current + delta;
    }

    /// <summary>
    /// Decrements a variable by delta (default 1). Creates the variable with value 0 if it doesn't exist.
    /// </summary>
    public void Dec(string name, int delta = 1)
    {
        var key = Normalize(name);
        this._vars.TryGetValue(key, out var current);
        this._vars[key] = current - delta;
    }

    /// <summary>
    /// Returns true if the variable's value equals the specified value.
    /// Non-existent variables are treated as 0.
    /// </summary>
    public bool Equal(string name, int value)
    {
        return this.Get(name) == value;
    }

    /// <summary>
    /// Returns true if the variable's value is greater than the specified value.
    /// Non-existent variables are treated as 0.
    /// </summary>
    public bool Large(string name, int value)
    {
        return this.Get(name) > value;
    }

    /// <summary>
    /// Returns true if the variable's value is less than the specified value.
    /// Non-existent variables are treated as 0.
    /// </summary>
    public bool Small(string name, int value)
    {
        return this.Get(name) < value;
    }

    /// <summary>
    /// Returns true if the variable's value is greater than or equal to the specified value.
    /// </summary>
    public bool LargeOrEqual(string name, int value)
    {
        return this.Get(name) >= value;
    }

    /// <summary>
    /// Returns true if the variable's value is less than or equal to the specified value.
    /// </summary>
    public bool SmallOrEqual(string name, int value)
    {
        return this.Get(name) <= value;
    }

    /// <summary>
    /// Clears all variables.
    /// </summary>
    public void Clear()
    {
        this._vars.Clear();
    }

    /// <summary>
    /// Returns a snapshot of all variables for logging/debugging.
    /// </summary>
    public IReadOnlyDictionary<string, int> Snapshot()
    {
        return new Dictionary<string, int>(this._vars);
    }

    private static string Normalize(string name)
    {
        return name.ToUpperInvariant();
    }
}
