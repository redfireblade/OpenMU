// <copyright file="NodeIdTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests.KnowledgeGraph;

using MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Unit tests for <see cref="NodeId"/> encoding and decoding.
/// Verifies bit-packing layout, factory methods, equality, and string representation.
/// </summary>
[TestFixture]
public class NodeIdTests
{
    /// <summary>
    /// Tests that <see cref="NodeId.ForMap"/> produces a <see cref="NodeType.Map"/> identifier.
    /// </summary>
    [Test]
    public void ForMap_TypeIsMap()
    {
        var id = NodeId.ForMap(0);
        Assert.That(id.Type, Is.EqualTo(NodeType.Map));
    }

    /// <summary>
    /// Tests that <see cref="NodeId.ForMap"/> packs the type in the upper 8 bits
    /// and the map number in the lower 56 bits.
    /// </summary>
    [Test]
    public void ForMap_ValueHasCorrectBitLayout()
    {
        var id = NodeId.ForMap(3);
        var expectedValue = ((long)NodeType.Map << 56) | 3L;
        Assert.That(id.Value, Is.EqualTo(expectedValue));
    }

    /// <summary>
    /// Tests that <see cref="NodeId.ForItem"/> packs group in the upper 32 bits and
    /// number in the lower 32 bits of the domain identifier.
    /// </summary>
    [Test]
    public void ForItem_DomainIdPacksGroupAndNumber()
    {
        var id = NodeId.ForItem(12, 15);
        var expectedDomainId = ((long)12 << 32) | 15;
        Assert.That(id.DomainId, Is.EqualTo(expectedDomainId));
    }

    /// <summary>
    /// Tests that <see cref="NodeId.ForItem"/> produces a <see cref="NodeType.Item"/> identifier.
    /// </summary>
    [Test]
    public void ForItem_TypeIsItem()
    {
        var id = NodeId.ForItem(12, 15);
        Assert.That(id.Type, Is.EqualTo(NodeType.Item));
    }

    /// <summary>
    /// Tests round-trip: map node created via factory reports correct type and domain id.
    /// </summary>
    [Test]
    public void ForMap_RoundTrip_TypeAndDomainMatch()
    {
        var id = NodeId.ForMap(7);
        Assert.Multiple(() =>
        {
            Assert.That(id.Type, Is.EqualTo(NodeType.Map));
            Assert.That(id.DomainId, Is.EqualTo(7));
        });
    }

    /// <summary>
    /// Tests round-trip: monster node created via factory reports correct type and domain id.
    /// </summary>
    [Test]
    public void ForMonster_RoundTrip_TypeAndDomainMatch()
    {
        var id = NodeId.ForMonster(25);
        Assert.Multiple(() =>
        {
            Assert.That(id.Type, Is.EqualTo(NodeType.Monster));
            Assert.That(id.DomainId, Is.EqualTo(25));
        });
    }

    /// <summary>
    /// Tests round-trip: NPC node created via factory reports correct type and domain id.
    /// </summary>
    [Test]
    public void ForNpc_RoundTrip_TypeAndDomainMatch()
    {
        var id = NodeId.ForNpc(100);
        Assert.Multiple(() =>
        {
            Assert.That(id.Type, Is.EqualTo(NodeType.Npc));
            Assert.That(id.DomainId, Is.EqualTo(100));
        });
    }

    /// <summary>
    /// Tests round-trip: quest node created via factory reports correct type and domain id.
    /// </summary>
    [Test]
    public void ForQuest_RoundTrip_TypeAndDomainMatch()
    {
        var id = NodeId.ForQuest(1, 5);
        Assert.Multiple(() =>
        {
            Assert.That(id.Type, Is.EqualTo(NodeType.Quest));
            Assert.That(id.DomainId, Is.EqualTo(((long)1 << 32) | 5));
        });
    }

    /// <summary>
    /// Tests round-trip: skill node created via factory reports correct type and domain id.
    /// </summary>
    [Test]
    public void ForSkill_RoundTrip_TypeAndDomainMatch()
    {
        var id = NodeId.ForSkill(33);
        Assert.Multiple(() =>
        {
            Assert.That(id.Type, Is.EqualTo(NodeType.Skill));
            Assert.That(id.DomainId, Is.EqualTo(33));
        });
    }

    /// <summary>
    /// Tests round-trip: player class node created via factory reports correct type and domain id.
    /// </summary>
    [Test]
    public void ForPlayerClass_RoundTrip_TypeAndDomainMatch()
    {
        var id = NodeId.ForPlayerClass(1);
        Assert.Multiple(() =>
        {
            Assert.That(id.Type, Is.EqualTo(NodeType.PlayerClass));
            Assert.That(id.DomainId, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Tests round-trip: mini-game event node created via factory reports correct type and domain id.
    /// </summary>
    [Test]
    public void ForMiniGameEvent_RoundTrip_TypeAndDomainMatch()
    {
        var id = NodeId.ForMiniGameEvent(10);
        Assert.Multiple(() =>
        {
            Assert.That(id.Type, Is.EqualTo(NodeType.MiniGameEvent));
            Assert.That(id.DomainId, Is.EqualTo(10));
        });
    }

    /// <summary>
    /// Tests round-trip: crafting recipe node created via factory reports correct type and domain id.
    /// </summary>
    [Test]
    public void ForCraftingRecipe_RoundTrip_TypeAndDomainMatch()
    {
        var id = NodeId.ForCraftingRecipe(5);
        Assert.Multiple(() =>
        {
            Assert.That(id.Type, Is.EqualTo(NodeType.CraftingRecipe));
            Assert.That(id.DomainId, Is.EqualTo(5));
        });
    }

    /// <summary>
    /// Tests that two node identifiers created with the same parameters are equal.
    /// </summary>
    [Test]
    public void Equality_SameValues_AreEqual()
    {
        var id1 = NodeId.ForMap(3);
        var id2 = NodeId.ForMap(3);
        Assert.That(id1, Is.EqualTo(id2));
        Assert.That(id1.GetHashCode(), Is.EqualTo(id2.GetHashCode()));
    }

    /// <summary>
    /// Tests that two node identifiers created with different values are not equal.
    /// </summary>
    [Test]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var id1 = NodeId.ForMap(3);
        var id2 = NodeId.ForMap(4);
        Assert.That(id1, Is.Not.EqualTo(id2));
    }

    /// <summary>
    /// Tests that the equality operator works correctly.
    /// </summary>
    [Test]
    public void Equality_Operator_Works()
    {
        var id1 = NodeId.ForMap(1);
        var id2 = NodeId.ForMap(1);
        var id3 = NodeId.ForMap(2);
        Assert.That(id1 == id2, Is.True);
        Assert.That(id1 == id3, Is.False);
        Assert.That(id1 != id3, Is.True);
    }

    /// <summary>
    /// Tests that <see cref="NodeId.ToString"/> produces the expected format "Type(DomainId)".
    /// </summary>
    [Test]
    public void ToString_FormatsCorrectly()
    {
        var id = NodeId.ForMap(5);
        Assert.That(id.ToString(), Is.EqualTo("Map(5)"));
    }

    /// <summary>
    /// Tests that large domain IDs are preserved correctly through the packing/unpacking cycle.
    /// </summary>
    [Test]
    public void LargeDomainId_IsPreserved()
    {
        var id = NodeId.ForItem(255, 65535);
        var expectedDomainId = ((long)255 << 32) | 65535;
        Assert.That(id.DomainId, Is.EqualTo(expectedDomainId));
        Assert.That(id.Type, Is.EqualTo(NodeType.Item));
    }

    /// <summary>
    /// Tests that items with zero group produce a correct identifier.
    /// </summary>
    [Test]
    public void ForItem_ZeroGroup_Works()
    {
        var id = NodeId.ForItem(0, 15);
        Assert.Multiple(() =>
        {
            Assert.That(id.Type, Is.EqualTo(NodeType.Item));
            Assert.That(id.DomainId, Is.EqualTo(15));
        });
    }
}
