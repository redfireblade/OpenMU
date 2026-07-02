// <copyright file="AccessMode.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.AccessLayer;

/// <summary>
/// 多模态接入模式枚举 — Layer 1 底层通信通道选择。
/// 参考 OAPS v3.0 2.2 环境探测与自动降级机制。
/// </summary>
public enum AccessMode
{
    /// <summary>官方 API：直接读取服务端内存，100% 精确，<5ms 延迟。</summary>
    OfficialApi,

    /// <summary>内存/DB 注入：通过共享内存或数据库直读，95% 精确。</summary>
    MemoryInjection,

    /// <summary>封包解析：基于 System.IO.Pipelines 的双层加密管道，20-50ms 延迟。</summary>
    PacketClient,

    /// <summary>CLI 微服务：通过命令行接口间接控制，100-300ms 延迟。</summary>
    CliMicroservice,

    /// <summary>CV+RPA：截图识别 + 硬件模拟，100-300ms 延迟，可穿透反作弊。</summary>
    VisionAndRpa,
}
