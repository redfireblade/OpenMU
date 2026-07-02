// <copyright file="KnowledgeGraphHolder.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Static holder for the Knowledge Graph query service.
/// Set once by DI registration after all services are initialized.
/// Avoids circular DI dependencies during startup.
/// </summary>
public static class KnowledgeGraphHolder
{
    /// <summary>
    /// Gets or sets the singleton Knowledge Graph query service.
    /// Set by DI registration after GameConfiguration is fully initialized.
    /// </summary>
    public static IKnowledgeGraphQuery? Instance { get; set; }

    /// <summary>
    /// Gets or sets the raw Knowledge Graph data structure.
    /// Used by KnowledgeGraphRuntimeLearner which needs the graph directly.
    /// Set by DI registration alongside <see cref="Instance"/>.
    /// </summary>
    public static KnowledgeGraph? Graph { get; set; }
}
