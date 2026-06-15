// <copyright file="NewPlayersInScopeExtendedPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.RemoteView.World;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.World;
using MUnique.OpenMU.GameServer.RemoteView.Character;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// The extended implementation of the <see cref="INewPlayersInScopePlugIn"/> which is forwarding everything to the game client with specific data packets.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.NewPlayersInScopeExtendedPlugIn_Name), Description = nameof(PlugInResources.NewPlayersInScopeExtendedPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("83E30752-501E-4A40-9698-9A5097825C30")]
[MinimumClient(106, 3, ClientLanguage.Invariant)]
public class NewPlayersInScopeExtendedPlugIn : NewPlayersInScopePlugIn, INewPlayersInScopePlugIn
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NewPlayersInScopeExtendedPlugIn"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public NewPlayersInScopeExtendedPlugIn(RemotePlayer player)
        : base(player)
    {
    }

    /// <inheritdoc />
    protected override async ValueTask SendCharacterAsync(Player newPlayer, bool isSpawned)
    {
        var playerLogger = this.Player.Logger;
        var observerName = this.Player.SelectedCharacter?.Name ?? "?";
        var newPlayerName = newPlayer.SelectedCharacter?.Name ?? "?";

        if (playerLogger.IsEnabled(LogLevel.Debug))
        {
            playerLogger.LogDebug("[TRACE] Extended SendCharacterAsync CALLED: observer={Observer}, newPlayer={NewPlayer}, hasConnection={HasConn}",
                observerName, newPlayerName, this.Player.Connection is not null);
        }

        var connection = this.Player.Connection;
        if (connection is null)
        {
            return;
        }

        var selectedCharacter = newPlayer.SelectedCharacter;
        if (selectedCharacter is null)
        {
            return;
        }

        if (playerLogger.IsEnabled(LogLevel.Debug))
        {
            playerLogger.LogDebug("[DEBUG] Extended SendCharacterAsync: Sending {Name} (Id={Id}) to {Observer} at {Pos}",
                selectedCharacter.Name,
                newPlayer.GetId(this.Player),
                observerName,
                this.Player.Position);
        }

        int Write()
        {
            var appearanceSerializer = this.Player.AppearanceSerializer;
            Span<byte> activeEffects = stackalloc byte[newPlayer.MagicEffectList.VisibleEffects.Count];
            for (int i = 0; i < activeEffects.Length && i < newPlayer.MagicEffectList.VisibleEffects.Count; i++)
            {
                activeEffects[i] = (byte)newPlayer.MagicEffectList.VisibleEffects[i].Id;
            }

            var requiredSize = AddCharacterToScopeExtendedRef.GetRequiredSize(appearanceSerializer.NeededSpace + activeEffects.Length + 1);

            var span = connection.Output.GetSpan(requiredSize)[..requiredSize];
            var packet = new AddCharacterToScopeExtendedRef(span);

            packet.Id = newPlayer.GetId(this.Player);
            if (isSpawned)
            {
                packet.Id |= 0x8000;
            }

            packet.CurrentPositionX = newPlayer.Position.X;
            packet.CurrentPositionY = newPlayer.Position.Y;

            packet.Name = selectedCharacter.Name;
            if (newPlayer.IsWalking)
            {
                packet.TargetPositionX = newPlayer.WalkTarget.X;
                packet.TargetPositionY = newPlayer.WalkTarget.Y;
            }
            else
            {
                packet.TargetPositionX = newPlayer.Position.X;
                packet.TargetPositionY = newPlayer.Position.Y;
            }

            packet.Rotation = newPlayer.Rotation.ToPacketByte();
            packet.HeroState = selectedCharacter.State.Convert();
            packet.AttackSpeed = (ushort)(newPlayer.Attributes?[Stats.AttackSpeed] ?? 0);
            packet.MagicSpeed = (ushort)(newPlayer.Attributes?[Stats.MagicSpeed] ?? 0);

            appearanceSerializer.WriteAppearanceData(packet.AppearanceAndEffects, newPlayer.AppearanceData, true);

            if (playerLogger.IsEnabled(LogLevel.Debug))
            {
                var appearanceBytes = packet.AppearanceAndEffects.ToArray();
                playerLogger.LogDebug("[DEBUG] Extended SendCharacterAsync: Appearance bytes for {Name} (len={Len}): {Bytes}",
                    selectedCharacter.Name,
                    appearanceBytes.Length,
                    BitConverter.ToString(appearanceBytes));
            }

            var effectsStartIndex = appearanceSerializer.NeededSpace;
            packet.AppearanceAndEffects[effectsStartIndex] = (byte)activeEffects.Length;
            for (int e = 0; e < activeEffects.Length; ++e)
            {
                packet.AppearanceAndEffects[effectsStartIndex + 1 + e] = activeEffects[e];
            }

            return span.Length;
        }

        await connection.SendAsync(Write).ConfigureAwait(false);

        if (playerLogger.IsEnabled(LogLevel.Debug))
        {
            playerLogger.LogDebug("[DEBUG] Extended SendCharacterAsync: Successfully sent {Name} to {Observer}",
                selectedCharacter.Name,
                this.Player.SelectedCharacter?.Name ?? "?");
        }
    }
}