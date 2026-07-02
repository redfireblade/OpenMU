// <copyright file="EnvironmentProbe.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.AccessLayer;

/// <summary>
/// 环境探测与自动降级机制。
/// 自动检测当前运行环境，选择最优接入模式。
/// 内部集成于 OpenMU 时固定为 <see cref="AccessMode.OfficialApi"/>。
/// 参考 OAPS v3.0 2.2。
/// </summary>
public sealed class EnvironmentProbe
{
    /// <summary>
    /// 检测并选择最优接入模式。
    /// </summary>
    public AccessMode DetectBestMode()
    {
        // 集成于 OpenMU 服务器内部时，直接使用官方 API 模式
        if (IsOpenMuIntegrated()) return AccessMode.OfficialApi;

        if (CheckPacketConnection()) return AccessMode.PacketClient;

        return AccessMode.VisionAndRpa;
    }

    private static bool IsOpenMuIntegrated()
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name?.Contains("OpenMU") == true);
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckPacketConnection()
    {
        // 预留：检测网络连接可用性
        return false;
    }
}
