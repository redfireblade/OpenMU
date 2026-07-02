// <copyright file="AiPathRecorder.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.Pathfinding;

/// <summary>
/// AI 行走路径记录器。
/// 记录从安全区出发到目标区域的完整路径坐标数组，
/// 用于中断后从当前位置续建路径、死亡后参考历史路径重新寻路。
/// 路径数据最终保存到 AI 角色的数字影子地图中。
/// </summary>
public sealed class AiPathRecorder
{
    private readonly List<Point> _pathPoints = new();
    private readonly string _characterName;
    private bool _isRecording;

    /// <summary>
    /// 获取当前记录的路径坐标（只读）。
    /// </summary>
    public IReadOnlyList<Point> CurrentPath => _pathPoints.AsReadOnly();

    /// <summary>
    /// 路径状态。
    /// </summary>
    public PathState State { get; private set; } = PathState.Idle;

    /// <summary>
    /// 本次路径的目标坐标。
    /// </summary>
    public Point? TargetPoint { get; private set; }

    /// <summary>
    /// 出发时的起点坐标。
    /// </summary>
    public Point? StartPoint { get; private set; }

    /// <summary>
    /// 地图编号。
    /// </summary>
    public ushort MapNumber { get; private set; }

    public AiPathRecorder(string characterName)
    {
        this._characterName = characterName;
    }

    /// <summary>
    /// 开始记录一条新路径。
    /// </summary>
    public void StartRecording(ushort mapNum, Point start, Point target)
    {
        this._pathPoints.Clear();
        this._pathPoints.Add(start);
        this.StartPoint = start;
        this.TargetPoint = target;
        this.MapNumber = mapNum;
        this.State = PathState.Recording;
        this._isRecording = true;
    }

    /// <summary>
    /// 记录一步坐标。如果该坐标与上一步相同则跳过（去重）。
    /// </summary>
    public void RecordStep(Point currentPos)
    {
        if (!this._isRecording) return;

        var last = this._pathPoints.Count > 0 ? this._pathPoints[^1] : (Point?)null;
        if (last.HasValue && last.Value.X == currentPos.X && last.Value.Y == currentPos.Y)
        {
            return; // 没移动，跳过
        }

        // 限制最大路径长度，防止内存泄漏
        if (this._pathPoints.Count >= 1000)
        {
            this.State = PathState.Completed;
            return;
        }

        this._pathPoints.Add(currentPos);
    }

    /// <summary>
    /// 从当前位置开始续建路径（不跳跃）。当路径被中断（战斗/死亡）后调用。
    /// </summary>
    public void ResumeFrom(Point currentPos)
    {
        if (this._pathPoints.Count == 0)
        {
            this._pathPoints.Add(currentPos);
            return;
        }

        var last = this._pathPoints[^1];
        var dist = currentPos.EuclideanDistanceTo(last);

        // 如果当前位置离路径终点不远，直接续上
        if (dist <= 5.0f)
        {
            this._pathPoints.Add(currentPos);
            this.State = PathState.Recording;
        }
        else
        {
            // 距离太远（比如死亡重生后），从当前位置重建
            this._pathPoints.Clear();
            this._pathPoints.Add(currentPos);
            this.State = PathState.Recording;
        }
    }

    /// <summary>
    /// 完成路径记录。
    /// </summary>
    public void Complete()
    {
        this.State = PathState.Completed;
        this._isRecording = false;
    }

    /// <summary>
    /// 重置路径。
    /// </summary>
    public void Reset()
    {
        this._pathPoints.Clear();
        this.StartPoint = null;
        this.TargetPoint = null;
        this.State = PathState.Idle;
        this._isRecording = false;
    }

    /// <summary>
    /// 将记录路径转为 ShadowMapEntry 列表，保存到数字影子地图。
    /// </summary>
    public ShadowMapEntry ToShadowEntry(ushort mapNum)
    {
        return new ShadowMapEntry
        {
            CharacterName = this._characterName,
            EntryType = ShadowEntryType.QuestActivity,
            MapNumber = mapNum,
            Description = $"路径 {this.StartPoint} → {this.TargetPoint}, {this._pathPoints.Count} 步",
            CreatedAt = DateTime.UtcNow,
        };
    }
}

/// <summary>
/// 路径记录器状态。
/// </summary>
public enum PathState
{
    /// <summary>空闲。</summary>
    Idle,

    /// <summary>正在记录中。</summary>
    Recording,

    /// <summary>已到达目标/完成。</summary>
    Completed,

    /// <summary>中断（战斗/死亡）。</summary>
    Interrupted,
}
