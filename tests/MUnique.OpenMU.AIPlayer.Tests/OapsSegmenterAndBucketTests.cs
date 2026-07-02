// <copyright file="OapsSegmenterAndBucketTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using NUnit.Framework;
using OAPS.Evolution;

[TestFixture]
public class OapsSegmenterAndBucketTests
{
    private static readonly DateTime T0 = new(2026, 6, 28, 10, 0, 0, DateTimeKind.Utc);

    [Test]
    public void Segmentate_Kills_SingleSegment()
    {
        var segs = new BehaviorSegmenter().Segmentate(new List<BehaviorEvent>
        {
            Evt.MKill("p", 3, 100, 200, 67, 10, T0),
            Evt.MKill("p", 3, 101, 201, 67, 10, T0.AddSeconds(5)),
        });
        Assert.That(segs.Count, Is.EqualTo(1));
        Assert.That(segs[0].SegmentType, Is.EqualTo(SegmentType.Hunting));
        Assert.That(segs[0].KillCount, Is.EqualTo(2));
    }

    [Test]
    public void Segmentate_MapChange_Split()
    {
        var segs = new BehaviorSegmenter().Segmentate(new List<BehaviorEvent>
        {
            Evt.MKill("p", 0, 100, 200, 67, 10, T0),
            Evt.MKill("p", 0, 101, 201, 67, 10, T0.AddSeconds(5)),
            Evt.MEnter("p", 3, 150, 150, T0.AddSeconds(30)),
            Evt.MKill("p", 3, 160, 160, 68, 10, T0.AddSeconds(35)),
            Evt.MKill("p", 3, 161, 161, 68, 10, T0.AddSeconds(40)),
        });
        Assert.That(segs.Count, Is.EqualTo(3));
        Assert.That(segs[0].MapNumber, Is.EqualTo(0));
        Assert.That(segs[2].MapNumber, Is.EqualTo(3));
    }

    [Test]
    public void Segmentate_Restock_Split()
    {
        var segs = new BehaviorSegmenter().Segmentate(new List<BehaviorEvent>
        {
            Evt.MKill("p", 3, 100, 200, 67, 10, T0),
            Evt.MKill("p", 3, 101, 201, 67, 10, T0.AddSeconds(5)),
            Evt.MPotion("p", 3, 130, 120, 100, T0.AddSeconds(10)),
            Evt.MPotion("p", 3, 131, 121, 100, T0.AddSeconds(12)),
        });
        Assert.That(segs.Count, Is.EqualTo(2));
        Assert.That(segs[0].SegmentType, Is.EqualTo(SegmentType.Hunting));
        Assert.That(segs[1].SegmentType, Is.EqualTo(SegmentType.Restock));
    }

    [Test]
    public void Segmentate_Empty_Empty()
    {
        Assert.That(new BehaviorSegmenter().Segmentate(new List<BehaviorEvent>()), Is.Empty);
    }

    [Test]
    public void Bucket_DifferentMap_DifferentBucket()
    {
        var s = new CapabilityBucketStore();
        s.BucketSegments(new List<BehaviorSegment>
        {
            MkSeg("s1", 3, "hunt"), MkSeg("s2", 3, "hunt"), MkSeg("s3", 0, "hunt"),
        });
        Assert.That(s.GetAllBuckets().Count, Is.EqualTo(2));
    }

    [Test]
    public void Bucket_DistillThreshold_Triggers()
    {
        var s = new CapabilityBucketStore();
        s.BucketSegments(Enumerable.Range(0, 3).Select(i => MkSeg($"s{i}", 3, "hunt")).ToList());
        Assert.That(s.GetBucketsReadyForDistill().Count, Is.EqualTo(1));
    }

    [Test]
    public void FullPipeline_EndToEnd()
    {
        var events = new List<BehaviorEvent>();
        for (int s = 0; s < 3; s++)
            for (int k = 0; k < 3; k++)
                events.Add(Evt.MKill("p", 3, 100 + k, 200, 67, 10, T0.AddSeconds(s * 120 + k)));
        var segs = new BehaviorSegmenter().Segmentate(events);
        Assert.That(segs.Count, Is.EqualTo(3));
        foreach (var s in segs) s.CapacityLabel = "hunt";
        var store = new CapabilityBucketStore();
        store.BucketSegments(segs);
        Assert.That(store.GetBucketsReadyForDistill(), Is.Not.Empty);
    }

    private static BehaviorSegment MkSeg(string id, int map, string cap) => new()
    {
        SegmentId = id, MapNumber = map, CapacityLabel = cap,
        SegmentType = SegmentType.Hunting, Events = Array.Empty<BehaviorEvent>(),
    };
}

/// <summary>帮助方法，集中处理 byte 转换。</summary>
file static class Evt
{
    internal static BehaviorEvent MKill(string n, int m, int x, int y, short mob, int lv, DateTime t) => new()
    {
        EventType = BehaviorEventType.MonsterKilled, CharacterName = n, MapNumber = m,
        X = (byte)x, Y = (byte)y, MonsterNumber = mob, MonsterName = $"#{mob}",
        MonsterLevel = (short)lv, Level = lv, Timestamp = t, Summary = $"kill {mob}",
    };
    internal static BehaviorEvent MEnter(string n, int m, int x, int y, DateTime t) => new()
    {
        EventType = BehaviorEventType.MapEntered, CharacterName = n, MapNumber = m,
        X = (byte)x, Y = (byte)y, Level = 10, Timestamp = t, Summary = $"enter {m}",
    };
    internal static BehaviorEvent MPotion(string n, int m, int x, int y, int price, DateTime t) => new()
    {
        EventType = BehaviorEventType.PotionBought, CharacterName = n, MapNumber = m,
        X = (byte)x, Y = (byte)y, ItemPrice = price, Level = 10, Timestamp = t, Summary = "potion",
    };
}
