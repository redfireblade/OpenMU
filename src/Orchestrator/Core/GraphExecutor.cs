// <copyright file="GraphExecutor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Orchestrator;

using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

/// <summary>
/// Sequential graph executor. Runs all enabled nodes in topological order.
/// Provides telemetry, error isolation, and optional step-mode for testing.
/// </summary>
public sealed class GraphExecutor
{
    private readonly ILogger<GraphExecutor>? _logger;
    private long _cycleNumber;

    /// <summary>Initializes a new instance of the <see cref="GraphExecutor"/> class.</summary>
    public GraphExecutor(ILogger<GraphExecutor>? logger = null)
    {
        this._logger = logger;
    }

    /// <summary>
    /// Executes all enabled nodes in topological order.
    /// Nodes are isolated: if one node throws, subsequent nodes still run.
    /// Telemetry is recorded via the context's <see cref="ITelemetrySink"/>.
    /// </summary>
    public async ValueTask ExecuteAsync(ExecutionGraph graph, IExecutionContext context)
    {
        var order = graph.GetExecutionOrder();
        var telemetry = context.Telemetry;
        var cycleNumber = Interlocked.Increment(ref this._cycleNumber);
        var cycleStartedAt = DateTime.UtcNow;

        foreach (var node in order)
        {
            if (!node.Enabled)
            {
                continue;
            }

            telemetry.OnNodeStarted(node.Name);
            var sw = Stopwatch.StartNew();
            Exception? error = null;

            try
            {
                this._logger?.LogTrace("Executing node {NodeName}", node.Name);
                await node.ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex;
                this._logger?.LogWarning(ex, "Node {NodeName} failed", node.Name);
            }
            finally
            {
                sw.Stop();
                telemetry.OnNodeCompleted(node.Name, sw.Elapsed, error);
            }
        }

        if (telemetry is TelemetrySink sink)
        {
            sink.CompleteCycle(cycleNumber, cycleStartedAt);
        }
    }

    /// <summary>
    /// Gets the current cycle number. Increments each call to <see cref="ExecuteAsync"/>.
    /// Useful for correlating telemetry across cycles.
    /// </summary>
    public long CycleNumber => Interlocked.Read(ref this._cycleNumber);
}
