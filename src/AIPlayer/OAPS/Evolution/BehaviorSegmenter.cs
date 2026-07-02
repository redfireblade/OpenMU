// <copyright file="BehaviorSegmenter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using MUnique.OpenMU.AIPlayer;

/// <summary>
/// 行为轨迹分段器 — 将原始 BehaviorEvent 流拆分为有边界的 Segment。
/// 参考 Browser-BC (Journey Forge Local) 的 Atomizer 设计。
/// </summary>
public sealed class BehaviorSegmenter
{
    private const double MaxGap = 60.0;
    private const int MinEvents = 2;

    public List<BehaviorSegment> Segmentate(IEnumerable<BehaviorEvent> events)
    {
        var sorted = events.OrderBy(e => e.Timestamp).ToList();
        if (sorted.Count == 0) return new();
        var segs = new List<BehaviorSegment>();
        var cur = new List<BehaviorEvent>();
        BehaviorEvent? last = null;
        foreach (var ev in sorted)
        {
            if (ev.EventType == BehaviorEventType.MapEntered)
            {
                if (cur.Count > 0) { DoAdd(segs, cur); cur = new(); }
                cur.Add(ev); last = ev; continue;
            }
            if (last != null && ShouldSplit(last, ev))
                { if (cur.Count > 0) { DoAdd(segs, cur); cur = new(); } }
            cur.Add(ev); last = ev;
        }
        if (cur.Count > 0) DoAdd(segs, cur);
        return Compact(segs);
    }

    private static bool ShouldSplit(BehaviorEvent last, BehaviorEvent cur)
    {
        if ((cur.Timestamp - last.Timestamp).TotalSeconds > MaxGap) return true;
        if (IsCombat(last) != IsCombat(cur)) return true;
        if (IsShop(cur)) return true;
        return false;
    }

    private static bool IsCombat(BehaviorEvent e) => e.EventType
        is BehaviorEventType.MonsterKilled or BehaviorEventType.ItemPickedUp;

    private static bool IsShop(BehaviorEvent e) => e.EventType
        is BehaviorEventType.PotionBought or BehaviorEventType.ItemBought or BehaviorEventType.EquipmentBought;

    private static void DoAdd(List<BehaviorSegment> segs, List<BehaviorEvent> evs)
    {
        if (evs.Count == 0) return;
        var f = evs[0]; var l = evs[^1];
        var t = CType(evs);
        var mn = evs.FirstOrDefault(e => e.MonsterNumber.HasValue)?.MonsterNumber;
        segs.Add(new BehaviorSegment
        {
            SegmentId = Guid.NewGuid().ToString("N"), CharacterName = f.CharacterName,
            SegmentType = t, Name = $"{t} @{f.MapNumber}",
            Events = evs.AsReadOnly(),
            StartTime = f.Timestamp, EndTime = l.Timestamp,
            DurationMs = (int)(l.Timestamp - f.Timestamp).TotalMilliseconds,
            MapNumber = f.MapNumber,
            StartX = f.X, StartY = f.Y, EndX = l.X, EndY = l.Y,
            KillCount = evs.Count(e => e.EventType == BehaviorEventType.MonsterKilled),
            MonsterNumber = mn,
            EventSummary = Summary(evs),
        });
    }

    private static SegmentType CType(List<BehaviorEvent> evs)
    {
        var t = evs.Select(e => e.EventType).ToHashSet();
        if (t.Overlaps(new[] { BehaviorEventType.PotionBought, BehaviorEventType.ItemBought, BehaviorEventType.EquipmentBought })) return SegmentType.Restock;
        if (t.Overlaps(new[] { BehaviorEventType.NpcTalk, BehaviorEventType.NpcDialogChoice, BehaviorEventType.BuffReceived })) return SegmentType.Quest;
        if (t.Contains(BehaviorEventType.MonsterKilled)) return SegmentType.Hunting;
        if (t.Overlaps(new[] { BehaviorEventType.StatAllocated, BehaviorEventType.SkillLearned })) return SegmentType.LevelUp;
        if (t.Contains(BehaviorEventType.MapEntered)) return SegmentType.Navigation;
        return SegmentType.Unknown;
    }

    private static string Summary(List<BehaviorEvent> evs)
    {
        var p = new List<string>();
        var k = evs.Count(e => e.EventType == BehaviorEventType.MonsterKilled);
        var b = evs.Count(e => e.EventType is BehaviorEventType.PotionBought or BehaviorEventType.ItemBought);
        var n = evs.Count(e => e.EventType == BehaviorEventType.NpcTalk);
        if (k > 0) p.Add($"击杀{k}"); if (b > 0) p.Add($"购买{b}"); if (n > 0) p.Add($"对话{n}");
        if (evs.Any(e => e.EventType == BehaviorEventType.LevelUp)) p.Add("升级");
        return p.Count > 0 ? string.Join(", ", p) : $"{evs.Count}事件";
    }

    private static List<BehaviorSegment> Compact(List<BehaviorSegment> segs)
    {
        if (segs.Count <= 1) return segs;
        var r = new List<BehaviorSegment>();
        foreach (var s in segs)
        {
            if (r.Count > 0 && s.Events.Count < MinEvents && s.SegmentType == r[^1].SegmentType)
            {
                var all = new List<BehaviorEvent>(r[^1].Events);
                all.AddRange(s.Events); all = all.OrderBy(e => e.Timestamp).ToList();
                r.RemoveAt(r.Count - 1);
                var tmp = new List<BehaviorSegment>(); DoAdd(tmp, all);
                if (tmp.Count > 0) r.Add(tmp[0]);
            }
            else r.Add(s);
        }
        return r;
    }
}

/// <summary>行为轨迹片段。</summary>
public sealed class BehaviorSegment
{
    public string SegmentId { get; init; } = "";
    public string CharacterName { get; init; } = "";
    public SegmentType SegmentType { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<BehaviorEvent> Events { get; init; } = Array.Empty<BehaviorEvent>();
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public int DurationMs { get; init; }
    public int MapNumber { get; init; }
    public byte StartX { get; init; }
    public byte StartY { get; init; }
    public byte EndX { get; init; }
    public byte EndY { get; init; }
    public int KillCount { get; init; }
    public short? MonsterNumber { get; init; }
    public string? EventSummary { get; init; }
    public string? CapacityLabel { get; set; }
}

/// <summary>行为片段类型。</summary>
public enum SegmentType
{
    Hunting, Restock, Quest, LevelUp, Navigation, Unknown,
}
