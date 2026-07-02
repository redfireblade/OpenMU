// <copyright file="CashShopStorageListHandlerPlugIn.cs" company="MUnique">
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
/// Handler for cash shop storage list requests (0xD2 0x05).
/// Responds with an empty storage list.
/// </summary>
[PlugIn]
[Display(Name = "CashShopStorageListHandlerPlugIn", Description = "Responds to cash shop storage list requests with empty storage.")]
[System.Runtime.InteropServices.GuidAttribute("59EC9EC9-A74D-47BC-A8B9-0333BF6C4A09")]
[BelongsToGroup(CashShopGroupHandlerPlugIn.GroupKey)]
internal class CashShopStorageListHandlerPlugIn : ISubPacketHandlerPlugIn
{
    /// <inheritdoc/>
    public bool IsEncryptionExpected => true;

    /// <inheritdoc/>
    public byte Key => CashShopStorageListRequest.SubCode;

    /// <inheritdoc/>
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        // Respond with empty storage count (0xD2 0x06):
        // C1 0C D2 06 [wTotalItemCount=0] [wCurrentItemCount=0] [wPageIndex=0] [wTotalPage=0]
        // Total: 1+1+1+1+2+2+2+2 = 12 bytes = 0x0C
        const int length = 12;
        var connection = ((RemotePlayer)player).Connection;
        if (connection is null) return;

        await connection.SendAsync(() =>
        {
            var span = connection.Output.GetSpan(length)[..length];
            span[0] = 0xC1;
            span[1] = length;
            span[2] = 0xD2;
            span[3] = 0x06;
            // wTotalItemCount = 0 (offset 4-5, little endian)
            // wCurrentItemCount = 0 (offset 6-7)
            // wPageIndex = 0 (offset 8-9)
            // wTotalPage = 0 (offset 10-11)
            // All zeros by default
            return length;
        }).ConfigureAwait(false);
    }
}
