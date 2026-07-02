// <copyright file="KnowledgeGraphExtensions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;

/// <summary>
/// Extension methods for registering the Knowledge Graph services.
/// </summary>
public static class KnowledgeGraphExtensions
{
    /// <summary>
    /// Adds the knowledge graph services to the service collection.
    /// </summary>
    public static IServiceCollection AddKnowledgeGraph(this IServiceCollection services)
    {
        services.AddSingleton<KnowledgeGraphBuilder>();
        services.AddSingleton(sp =>
        {
            var builder = sp.GetRequiredService<KnowledgeGraphBuilder>();
            var config = sp.GetRequiredService<GameConfiguration>();
            var knowledge = sp.GetRequiredService<GameKnowledgeService>();
            var logger = sp.GetService<ILogger<KnowledgeGraphBuilder>>();
            return logger is not null
                ? builder.Build(config, knowledge)
                : builder.Build(config, knowledge);
        });
        return services;
    }
}
