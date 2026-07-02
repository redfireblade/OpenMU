// <copyright file="CashShopPointInfoHandlerPlugIn.cs" company="MUnique">
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
/// Handler for cash shop point info requests (0xD2 0x01).
/// Responds with the player's cash shop points (all zeros).
/// </summary>
[PlugIn]
[Display(Name = "CashShopPointInfoHandlerPlugIn", Description = "Responds to cash shop point info requests.")]
[System.Runtime.InteropServices.GuidAttribute("CDA8CBF7-4D3B-4148-B8AA-B3B473C92601")]
[BelongsToGroup(CashShopGroupHandlerPlugIn.GroupKey)]
internal class CashShopPointInfoHandlerPlugIn : ISubPacketHandlerPlugIn
{
    /// <inheritdoc/>
    public bool IsEncryptionExpected => true;

    /// <inheritdoc/>
    public byte Key => CashShopPointInfoRequest.SubCode;

    /// <inheritdoc/>
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        // Response: C1 2D D2 01 [btViewType=0] [double TotalCash=0] [double CashCredit=0] [double CashPrepaid=0] [double TotalPoint=0] [double TotalMileage=0]
        // Total: 1+1+1+1+1+8+8+8+8+8 = 45 bytes = 0x2D
        const int length = 45;
        var connection = ((RemotePlayer)player).Connection;
        if (connection is null) return;

        await connection.SendAsync(() =>
        {
            var span = connection.Output.GetSpan(length)[..length];
            span[0] = 0xC1;
            span[1] = length;
            span[2] = 0xD2;
            span[3] = 0x01;
            span[4] = 0; // btViewType
            // 5 doubles, all zero (span is zero-initialized by GetSpan)
            return length;
        }).ConfigureAwait(false);
    }
}
