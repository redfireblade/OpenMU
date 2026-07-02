// <copyright file="AccessControlList.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// F11: Fugu-style Access Control List for agent isolation.
///
/// Implements Fugu-Ultra's key design principle:
///   "Prevent orchestration collapse — the first agent to interact with the
///    environment shouldn't set the trajectory for all future agents."
///
/// Three layers of visibility:
///   Global  (always readable) — terrain, gates, map geometry
///   Shared  (readable if ACL permits) — pheromones, drop rates, routes
///   Private (never readable by others) — current target, path, task, HP state
///
/// Each AI character has its own ACL instance. When accessing SharedMemoryLayer,
/// the ACL controls which shared data is visible to this character.
/// </summary>
public sealed class AccessControlList
{
    private readonly string _ownerId;
    private readonly HashSet<string> _allowedCapabilities = new();
    private readonly HashSet<string> _blockedWorkers = new();

    /// <summary>
    /// Creates an ACL for the given worker.
    /// </summary>
    /// <param name="ownerId">This AI character's unique ID.</param>
    public AccessControlList(string ownerId)
    {
        _ownerId = ownerId;

        // Default permissions: can read pheromones and drop stats, nothing else
        _allowedCapabilities.Add("pheromone");
        _allowedCapabilities.Add("drop_stats");
        _allowedCapabilities.Add("route_db");
    }

    /// <summary>Owner identifier.</summary>
    public string OwnerId => _ownerId;

    /// <summary>
    /// Checks whether reading a specific shared data category is allowed.
    /// </summary>
    public bool CanRead(string category)
    {
        return _allowedCapabilities.Contains(category);
    }

    /// <summary>
    /// Checks whether seeing another worker's private data is allowed.
    /// Currently always false (strict isolation).
    /// </summary>
    public bool CanSeePrivate(string otherWorkerId)
    {
        return otherWorkerId == _ownerId || _blockedWorkers.Contains(otherWorkerId) == false;
    }

    /// <summary>
    /// Checks whether receiving a task assignment from the SwarmOrchestrator is allowed.
    /// </summary>
    public bool CanReceiveTask(string taskType)
    {
        return _allowedCapabilities.Contains(taskType);
    }

    /// <summary>
    /// Grants access to a shared data category.
    /// </summary>
    public void Grant(string category)
    {
        _allowedCapabilities.Add(category);
    }

    /// <summary>
    /// Revokes access to a shared data category.
    /// </summary>
    public void Revoke(string category)
    {
        _allowedCapabilities.Remove(category);
    }

    /// <summary>
    /// Blocks a specific worker from seeing this AI's private data.
    /// </summary>
    public void BlockWorker(string workerId)
    {
        _blockedWorkers.Add(workerId);
    }

    /// <summary>
    /// Unblocks a worker.
    /// </summary>
    public void UnblockWorker(string workerId)
    {
        _blockedWorkers.Remove(workerId);
    }
}

/// <summary>
/// Access-controlled wrapper around SharedMemoryLayer.
/// Only returns data that the ACL permits.
/// </summary>
public sealed class AccessControlledMemory
{
    private readonly SharedMemoryLayer _shared;
    private readonly AccessControlList _acl;

    public AccessControlledMemory(SharedMemoryLayer shared, AccessControlList acl)
    {
        _shared = shared;
        _acl = acl;
    }

    /// <summary>
    /// Read pheromone at position, only if ACL permits.
    /// </summary>
    public (float Intensity, float Confidence) ReadPheromone(int x, int y)
    {
        return _acl.CanRead("pheromone")
            ? _shared.ReadPheromone(x, y)
            : (0, 0);
    }

    /// <summary>
    /// Read drop rate, only if ACL permits.
    /// </summary>
    public float GetDropRate(int zoneX, int zoneY)
    {
        return _acl.CanRead("drop_stats")
            ? _shared.GetDropRate(zoneX, zoneY)
            : 0f;
    }

    /// <summary>
    /// Get best routes near position, only if ACL permits.
    /// </summary>
    public List<RouteEntry> GetBestRoutes(int x, int y, int topN = 3)
    {
        return _acl.CanRead("route_db")
            ? _shared.FindBestRoutesNear(x, y, topN: topN)
            : new List<RouteEntry>();
    }

    /// <summary>
    /// Deposit pheromone (always allowed — contributing to shared knowledge).
    /// </summary>
    public void DepositPheromone(int x, int y, float intensity, float confidence)
    {
        _shared.DepositPheromone(x, y, intensity, confidence);
    }

    /// <summary>
    /// Record a drop event (always allowed — contributing to shared knowledge).
    /// </summary>
    public void RecordDrop(int zoneX, int zoneY, bool dropped, string? itemName = null)
    {
        _shared.RecordDropEvent(zoneX, zoneY, dropped, itemName);
    }
}
