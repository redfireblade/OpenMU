// <copyright file="AiPerformanceController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.API;

using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using MUnique.OpenMU.AIPlayer;

/// <summary>
/// API controller that exposes real-time AI player performance metrics.
/// Used by external monitoring tools and the Blazor debug dashboard.
/// </summary>
[Route("api/ai-performance")]
public class AiPerformanceController : Controller
{
    private readonly IAiDebugService _debugService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiPerformanceController"/> class.
    /// </summary>
    /// <param name="debugService">The AI debug service.</param>
    public AiPerformanceController(IAiDebugService debugService)
    {
        this._debugService = debugService;
    }

    /// <summary>
    /// Gets performance metrics for all active AI players.
    /// </summary>
    /// <returns>A JSON array of per-player performance data.</returns>
    [HttpGet]
    [Route("")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetAll()
    {
        var allData = this._debugService.GetAllDebugData();
        var result = allData.Select(ToPerformanceDto).ToList();
        return this.Ok(result);
    }

    /// <summary>
    /// Gets performance metrics for a specific AI player.
    /// </summary>
    /// <param name="playerId">The AI player ID.</param>
    /// <returns>Performance data for the specified player, or 404 if not found.</returns>
    [HttpGet]
    [Route("{playerId:guid}")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetById(Guid playerId)
    {
        var data = this._debugService.GetDebugData(playerId);
        if (data is null)
        {
            return this.NotFound();
        }

        return this.Ok(ToPerformanceDto(data));
    }

    /// <summary>
    /// Gets a statistical summary of AI player performance metrics.
    /// </summary>
    /// <returns>Aggregated statistics over all active AI players.</returns>
    [HttpGet]
    [Route("summary")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetSummary()
    {
        var allData = this._debugService.GetAllDebugData();
        return this.Ok(ComputeSummary(allData));
    }

    private static object ToPerformanceDto(AiPlayerDebugData data)
    {
        return new
        {
            playerId = data.PlayerId.ToString(),
            characterName = data.CharacterName,
            currentMapId = data.CurrentMapId,
            level = data.Level,
            tickNumber = data.TickNumber,
            runMode = data.RunMode.ToString(),
            timing = new
            {
                totalUs = data.TickTotalUs,
                worldRefreshUs = data.TickWorldRefreshUs,
                scriptExecutionUs = data.TickScriptExecutionUs,
                moduleExecutionUs = data.TickModuleExecutionUs,
                mindEngineUs = data.TickMindEngineUs,
            },
            survival = new
            {
                level = data.SurvivalLevel.ToString(),
                hasTarget = data.HasTarget,
                emergencyRetreat = data.EmergencyRetreat,
            },
            position = new
            {
                x = data.PositionX,
                y = data.PositionY,
            },
        };
    }

    private static object ComputeSummary(IReadOnlyCollection<AiPlayerDebugData> allData)
    {
        if (allData.Count == 0)
        {
            return new
            {
                playerCount = 0,
                runModeDistribution = new { },
                averageTiming = new { },
                maxTiming = new { },
            };
        }

        var modeDistribution = allData
            .GroupBy(d => d.RunMode)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var totalTimings = allData.Select(d => (double)d.TickTotalUs).ToList();
        var worldTimings = allData.Select(d => (double)d.TickWorldRefreshUs).ToList();
        var scriptTimings = allData.Select(d => (double)d.TickScriptExecutionUs).ToList();
        var moduleTimings = allData.Select(d => (double)d.TickModuleExecutionUs).ToList();
        var mindTimings = allData.Select(d => (double)d.TickMindEngineUs).ToList();

        return new
        {
            playerCount = allData.Count,
            runModeDistribution = modeDistribution,
            averageTiming = new
            {
                totalUs = totalTimings.Average(),
                worldRefreshUs = worldTimings.Average(),
                scriptExecutionUs = scriptTimings.Average(),
                moduleExecutionUs = moduleTimings.Average(),
                mindEngineUs = mindTimings.Average(),
            },
            maxTiming = new
            {
                totalUs = totalTimings.Max(),
                worldRefreshUs = worldTimings.Max(),
                scriptExecutionUs = scriptTimings.Max(),
                moduleExecutionUs = moduleTimings.Max(),
                mindEngineUs = mindTimings.Max(),
            },
        };
    }
}
