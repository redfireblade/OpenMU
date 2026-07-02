// <copyright file="AlgorithmSelector.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Pathfinding;

using System.Threading;
using MUnique.OpenMU.Pathfinding.Algorithms;

/// <summary>
/// Defines how the <see cref="AlgorithmSelector"/> selects a pathfinding algorithm.
/// </summary>
public enum SelectionMode
{
    /// <summary>
    /// Randomly picks an algorithm each time.
    /// </summary>
    Random,

    /// <summary>
    /// Cycles through algorithms in registration order.
    /// </summary>
    RoundRobin,

    /// <summary>
    /// Selects by the algorithm's <see cref="IPathFindingAlgorithm.Name"/>.
    /// </summary>
    ByName,

    /// <summary>
    /// Selects based on a character type hint (reserved for future use).
    /// </summary>
    ByCharacterType,
}

/// <summary>
/// Selects a pathfinding algorithm from a registered collection based on the configured mode.
/// </summary>
public sealed class AlgorithmSelector
{
    private readonly List<IPathFindingAlgorithm> _algorithms = new();
    private readonly Random _random = new();
    private int _roundRobinIndex;

    /// <summary>
    /// Gets the number of registered algorithms.
    /// </summary>
    public int Count => this._algorithms.Count;

    /// <summary>
    /// Registers an algorithm for selection.
    /// </summary>
    /// <param name="algorithm">The algorithm to register.</param>
    public void RegisterAlgorithm(IPathFindingAlgorithm algorithm)
    {
        this._algorithms.Add(algorithm);
    }

    /// <summary>
    /// Registers all built-in pathfinding algorithms.
    /// </summary>
    public void RegisterAlgorithms()
    {
        this._algorithms.Add(new AStarAlgorithm());
        this._algorithms.Add(new WeightedAStarAlgorithm());
        this._algorithms.Add(new DiffusionPathAlgorithm());
        this._algorithms.Add(new AntColonyAlgorithm());
        this._algorithms.Add(new GeneticPathAlgorithm());
        this._algorithms.Add(new RandomWalkAlgorithm());
    }

    /// <summary>
    /// Registers multiple algorithms at once.
    /// </summary>
    /// <param name="algorithms">The algorithms to register.</param>
    public void RegisterAlgorithms(IEnumerable<IPathFindingAlgorithm> algorithms)
    {
        this._algorithms.AddRange(algorithms);
    }

    /// <summary>
    /// Selects an algorithm based on the specified mode.
    /// </summary>
    /// <param name="mode">The selection mode.</param>
    /// <param name="hint">Optional hint: algorithm name for <see cref="SelectionMode.ByName"/>, or character type for <see cref="SelectionMode.ByCharacterType"/>.</param>
    /// <returns>The selected algorithm, or <c>null</c> if no algorithm matches.</returns>
    public IPathFindingAlgorithm? Select(SelectionMode mode, string? hint = null)
    {
        if (this._algorithms.Count == 0)
        {
            return null;
        }

        return mode switch
        {
            SelectionMode.Random => this._algorithms[this._random.Next(this._algorithms.Count)],
            SelectionMode.RoundRobin => this.SelectRoundRobin(),
            SelectionMode.ByName => this.SelectByName(hint),
            SelectionMode.ByCharacterType => this.SelectByCharacterType(hint),
            _ => this._algorithms[0],
        };
    }

    /// <summary>
    /// Returns all registered algorithms.
    /// </summary>
    public IReadOnlyList<IPathFindingAlgorithm> GetAll() => this._algorithms.AsReadOnly();

    private IPathFindingAlgorithm SelectRoundRobin()
    {
        var index = Interlocked.Increment(ref this._roundRobinIndex) % this._algorithms.Count;
        if (index < 0)
        {
            index = 0;
        }

        return this._algorithms[index];
    }

    private IPathFindingAlgorithm? SelectByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return this._algorithms.FirstOrDefault();
        }

        return this._algorithms.FirstOrDefault(a =>
            a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private IPathFindingAlgorithm? SelectByCharacterType(string? characterType)
    {
        // Reserved for Phase 2+: personality-based algorithm selection
        // For now, falls back to random selection
        return this._algorithms[this._random.Next(this._algorithms.Count)];
    }
}
