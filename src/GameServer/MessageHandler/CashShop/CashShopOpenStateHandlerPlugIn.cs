// <copyright file="CashShopOpenStateHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.CashShop;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.Packets.ClientToServer;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Handler for cash shop open/close state packets (0xD2 0x02).
/// Responds that the cash shop is available to open.
/// </summary>
[PlugIn]
[Display(Name = "CashShopOpenStateHandlerPlugIn", Description = "Responds to cash shop open/close requests.")]
[System.Runtime.InteropServices.GuidAttribute("B5AE2DA7-9D8C-435C-B9C9-9DC30325CB13")]
[BelongsToGroup(CashShopGroupHandlerPlugIn.GroupKey)]
internal class CashShopOpenStateHandlerPlugIn : ISubPacketHandlerPlugIn
{
    /// <inheritdoc/>
    public bool IsEncryptionExpected => true;

    /// <inheritdoc/>
    public byte Key => CashShopOpenState.SubCode;

    /// <inheritdoc/>
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        var isClosed = packet.Span[4] > 0;

        if (isClosed)
        {
            // Client is closing the shop, no response needed
            return;
        }

        // Response: C1 05 D2 02 [byShopOpenResult]
        // The client opens the shop when byShopOpenResult != 0
        // When 0, the client treats it as "shop not available"
        const int length = 5;
        var connection = ((RemotePlayer)player).Connection;
        if (connection is null) return;

        await connection.SendAsync(() =>
        {
            var span = connection.Output.GetSpan(length)[..length];
            span[0] = 0xC1;
            span[1] = length;
            span[2] = 0xD2;
            span[3] = 0x02;
            span[4] = 1; // non-zero = shop available
            return length;
        }).ConfigureAwait(false);
    }
}
