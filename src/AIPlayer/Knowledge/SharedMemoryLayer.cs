// <copyright file="SharedMemoryLayer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

using Microsoft.Extensions.Logging;

/// <summary>
/// Fugu-style three-layer shadow map memory architecture.
///
/// Maps Fugu-Ultra's agent isolation + shared memory tension to OAPS's digital map:
///   Global Layer  — immutable terrain/geometry/gate data (all agents see)
///   Shared Layer  — swarm knowledge (pheromones, drop rates, routes) — cross-workflow persistent
///   Private Layer — per-agent isolated state (current target, path, task context)
///
/// Fugu principle: "prevent orchestration collapse" — each agent sees only what the
/// AccessList permits, preventing the first agent's trajectory from dominating all others.
/// </summary>
public sealed class SharedMemoryLayer
{
    private readonly ILogger _logger;

    /// <summary>Map width (default 256 for MU maps).</summary>
    private const int MapWidth = 256;
    private const int MapHeight = 256;

    // ═══ Shared Layer: Pheromone Grid with Confidence ═══
    // Each cell stores (intensity, confidence) — Fugu's "experience distribution"
    private readonly (float Intensity, float Confidence)[,] _pheromones =
        new (float, float)[MapWidth, MapHeight];

    // ═══ Shared Layer: Drop Rate Statistics ═══
    // Rolling average of drops per kill at each coordinate zone
    private readonly Dictionary<string, DropRateStats> _dropRateZones = new();

    // ═══ Shared Layer: Route Database ═══
    // Best-known routes between locations, ranked by efficiency
    private readonly List<RouteEntry> _bestRoutes = new();

    // ═══ Shared Layer: Worker Capability Profiles ═══
    // Each AI character's known strengths (for Fugu-style task assignment)
    private readonly Dictionary<string, WorkerProfile> _workerProfiles = new();

    // ═══ Evaporation Timer ═══
    private readonly System.Timers.Timer _evaporationTimer;
    private const double EvaporationIntervalMs = 60_000; // 1 minute
    private const float EvaporationRate = 0.95f;           // 5% decay per minute

    public SharedMemoryLayer(ILogger<SharedMemoryLayer> logger)
    {
        _logger = logger;
        _evaporationTimer = new System.Timers.Timer(EvaporationIntervalMs);
        _evaporationTimer.Elapsed += (_, _) => EvaporatePheromones();
        _evaporationTimer.AutoReset = true;
        _evaporationTimer.Start();
    }

    // ═══ Pheromone Operations ═══

    /// <summary>
    /// Deposits a pheromone mark at the given position with a confidence weight.
    /// High confidence = strong empirical evidence (many kills here).
    /// Low confidence = weak signal (one observation).
    /// </summary>
    public void DepositPheromone(int x, int y, float intensity, float confidence)
    {
        if (x < 0 || x >= MapWidth || y < 0 || y >= MapHeight) return;

        var current = _pheromones[x, y];
        // Weighted update: existing signal × evaporation + new deposition
        _pheromones[x, y] = (
            Intensity: Math.Min(1f, current.Intensity + intensity * confidence),
            Confidence: Math.Min(1f, current.Confidence + confidence * 0.1f)
        );
    }

    /// <summary>
    /// Reads the pheromone value at a position.
    /// </summary>
    public (float Intensity, float Confidence) ReadPheromone(int x, int y)
    {
        if (x < 0 || x >= MapWidth || y < 0 || y >= MapHeight)
            return (0, 0);
        return _pheromones[x, y];
    }

    /// <summary>
    /// Gets the top-N highest pheromone positions as hunting hotspots.
    /// </summary>
    public List<(int X, int Y, float Intensity, float Confidence)> GetTopHotspots(int topN = 10)
    {
        var all = new List<(int, int, float, float)>();
        for (int x = 0; x < MapWidth; x++)
        {
            for (int y = 0; y < MapHeight; y++)
            {
                var (intensity, conf) = _pheromones[x, y];
                if (intensity > 0.01f)
                {
                    all.Add((x, y, intensity, conf));
                }
            }
        }

        return all
            .OrderByDescending(p => p.Item3 * p.Item4) // intensity × confidence
            .Take(topN)
            .ToList();
    }

    private void EvaporatePheromones()
    {
        for (int x = 0; x < MapWidth; x++)
        {
            for (int y = 0; y < MapHeight; y++)
            {
                var (intensity, conf) = _pheromones[x, y];
                _pheromones[x, y] = (intensity * EvaporationRate, conf);
            }
        }
    }

    // ═══ Drop Rate Statistics ═══

    /// <summary>
    /// Records a kill → drop event in the zone statistics.
    /// </summary>
    public void RecordDropEvent(int zoneX, int zoneY, bool dropped, string? itemName = null)
    {
        var key = $"{zoneX},{zoneY}";
        if (!_dropRateZones.TryGetValue(key, out var stats))
        {
            stats = new DropRateStats { CenterX = zoneX, CenterY = zoneY };
            _dropRateZones[key] = stats;
        }

        stats.TotalKills++;
        if (dropped) stats.TotalDrops++;
        if (itemName is not null)
        {
            stats.ItemCounts.TryGetValue(itemName, out var count);
            stats.ItemCounts[itemName] = count + 1;
        }

        stats.DropRate = (float)stats.TotalDrops / Math.Max(1, stats.TotalKills);
    }

    /// <summary>
    /// Gets the estimated drop rate at a zone.
    /// </summary>
    public float GetDropRate(int zoneX, int zoneY)
    {
        var key = $"{zoneX},{zoneY}";
        return _dropRateZones.TryGetValue(key, out var stats) ? stats.DropRate : 0f;
    }

    /// <summary>
    /// Gets the most productive zones by drop rate.
    /// </summary>
    public List<DropRateStats> GetBestDropZones(int topN = 5)
    {
        return _dropRateZones.Values
            .Where(s => s.TotalKills >= 10)
            .OrderByDescending(s => s.DropRate)
            .Take(topN)
            .ToList();
    }

    // ═══ Route Database ═══

    /// <summary>
    /// Records a route that was successfully navigated.
    /// </summary>
    public void RecordRoute((int X, int Y) from, (int X, int Y) to, int stepCount, float reward)
    {
        _bestRoutes.Add(new RouteEntry
        {
            FromX = from.X, FromY = from.Y,
            ToX = to.X, ToY = to.Y,
            StepCount = stepCount,
            Reward = reward,
            Timestamp = DateTime.UtcNow,
        });

        // Keep bounded
        if (_bestRoutes.Count > 1000)
        {
            _bestRoutes.RemoveRange(0, 200);
        }
    }

    /// <summary>
    /// Finds the best known routes near a source position.
    /// </summary>
    public List<RouteEntry> FindBestRoutesNear(int x, int y, int radius = 5, int topN = 3)
    {
        return _bestRoutes
            .Where(r => Math.Abs(r.FromX - x) <= radius && Math.Abs(r.FromY - y) <= radius)
            .OrderByDescending(r => r.Reward / Math.Max(1, r.StepCount)) // reward per step
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// Gets the best route score near a position (for StateVector encoding).
    /// </summary>
    public float GetBestRouteScore(int x, int y)
    {
        var best = FindBestRoutesNear(x, y, topN: 1).FirstOrDefault();
        if (best is null) return 0f;
        return best.Reward / Math.Max(1, best.StepCount);
    }

    // ═══ Worker Capability Profiles ═══

    /// <summary>
    /// Updates a worker's capability profile based on performance data.
    /// Maps to Fugu's "empirical performance data" per worker model.
    /// </summary>
    public void UpdateWorkerProfile(string workerId, string capability, float score)
    {
        if (!_workerProfiles.TryGetValue(workerId, out var profile))
        {
            profile = new WorkerProfile { WorkerId = workerId };
            _workerProfiles[workerId] = profile;
        }

        profile.Capabilities[capability] = score;
        profile.LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets workers best suited for a specific capability (for task assignment).
    /// </summary>
    public List<WorkerProfile> FindWorkersByCapability(string capability, float minScore = 0.5f, int topN = 5)
    {
        return _workerProfiles.Values
            .Where(p => p.Capabilities.TryGetValue(capability, out var s) && s >= minScore)
            .OrderByDescending(p => p.Capabilities[capability])
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// Gets a worker's full capability profile.
    /// </summary>
    public WorkerProfile? GetWorkerProfile(string workerId)
    {
        return _workerProfiles.TryGetValue(workerId, out var profile) ? profile : null;
    }

    // ═══ Cleanup ═══

    public void Dispose()
    {
        _evaporationTimer.Stop();
        _evaporationTimer.Dispose();
    }
}

// ═══ Data Types ═══

/// <summary>
/// Zone-level drop rate tracking.
/// </summary>
public class DropRateStats
{
    public int CenterX { get; set; }
    public int CenterY { get; set; }
    public int TotalKills { get; set; }
    public int TotalDrops { get; set; }
    public float DropRate { get; set; }
    public Dictionary<string, int> ItemCounts { get; set; } = new();
}

/// <summary>
/// A recorded successful navigation route.
/// </summary>
public class RouteEntry
{
    public int FromX { get; init; }
    public int FromY { get; init; }
    public int ToX { get; init; }
    public int ToY { get; init; }
    public int StepCount { get; init; }
    public float Reward { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Fugu-style worker capability profile.
/// Maps to Fugu's per-worker empirical performance statistics.
/// </summary>
public class WorkerProfile
{
    public string WorkerId { get; init; } = string.Empty;
    public Dictionary<string, float> Capabilities { get; init; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
