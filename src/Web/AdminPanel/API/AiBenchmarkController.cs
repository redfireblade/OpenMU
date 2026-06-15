// <copyright file="AiBenchmarkController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.API;

using Microsoft.AspNetCore.Mvc;
using MUnique.OpenMU.AIPlayer;

/// <summary>
/// API controller that exposes computed AI player benchmark metrics.
/// Used by monitoring tools and the benchmark dashboard.
/// </summary>
[Route("api/ai/benchmark")]
[ApiController]
public class AiBenchmarkController : ControllerBase
{
    private readonly IAiDebugService _debugService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiBenchmarkController"/> class.
    /// </summary>
    /// <param name="debugService">The AI debug service.</param>
    public AiBenchmarkController(IAiDebugService debugService)
    {
        this._debugService = debugService;
    }

    /// <summary>
    /// Gets benchmark metrics for all active AI players.
    /// </summary>
    [HttpGet]
    public Microsoft.AspNetCore.Mvc.IActionResult GetAll()
    {
        var allData = this._debugService.GetAllDebugData();
        var result = allData.Select(ToBenchmarkDto).ToList();
        return this.Ok(result);
    }

    private static object ToBenchmarkDto(AiPlayerDebugData data)
    {
        return new
        {
            playerId = data.PlayerId.ToString(),
            playerName = data.CharacterName,
            ticksPerSecond = data.TicksPerSecond,
            avgTickUs = data.AvgTickUs,
            p95TickUs = data.P95TickUs,
            mode = data.RunMode.ToString(),
            isDegraded = data.RunMode == ExecutionMode.Degraded,
        };
    }
}
