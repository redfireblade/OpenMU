// <copyright file="CashShopGroupHandlerPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler.CashShop;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Handler group for cash shop packets (0xD2).
/// </summary>
[PlugIn]
[Display(Name = "CashShopGroupHandlerPlugIn", Description = "Handles incoming cash shop packets of group 0xD2.")]
[System.Runtime.InteropServices.GuidAttribute("2404F696-8015-4728-9666-E493A3578BFA")]
internal class CashShopGroupHandlerPlugIn : GroupPacketHandlerPlugIn
{
    internal const byte GroupKey = (byte)PacketType.CashShopGroup;

    /// <summary>
    /// Initializes a new instance of the <see cref="CashShopGroupHandlerPlugIn"/> class.
    /// </summary>
    /// <param name="clientVersionProvider">The client version provider.</param>
    /// <param name="manager">The plugin manager.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public CashShopGroupHandlerPlugIn(IClientVersionProvider clientVersionProvider, PlugInManager manager, ILoggerFactory loggerFactory)
        : base(clientVersionProvider, manager, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool IsEncryptionExpected => true;

    /// <inheritdoc/>
    public override byte Key => GroupKey;
}
