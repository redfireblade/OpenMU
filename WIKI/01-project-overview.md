# 项目概述

## 引擎类型

OpenMU 是一款开源的 MU Online 服务器引擎，兼容 MU Online v0.75 至 Season 6 版本协议。项目采用 **Clean Room 原则** 开发——所有代码基于协议逆向工程和公开文档，不包含任何来自 Deathway/zTeam/IGCN 等泄露源码的代码。

## 技术栈

| 技术 | 版本 | 用途 |
|------|------|------|
| .NET | 10 | 运行时和基础类库 |
| C# | 13 | 主开发语言 |
| Entity Framework Core | 最新 | 数据库持久化（PostgreSQL / InMemory） |
| Blazor Server | .NET 10 | AdminPanel 管理面板 |
| SignalR | 内置 | 实时地图 WebSocket 通信 |
| Dapr | 可选 | 分布式微服务部署 |

## 核心玩法

OpenMU 完整实现了 MMORPG 的核心游戏循环：

- **PVE 战斗** — 玩家在地图中击杀怪物，获取经验和掉落物品
- **PVP 战斗** — 玩家间的竞技场和野战
- **物品合成** — 通过 NPC 合成高级装备（翅膀、武器等）
- **任务系统** — 主线/支线/每日任务
- **事件系统** — 恶魔广场、血色城堡等定时活动
- **交易系统** — 玩家间交易、个人商店
- **战盟系统** — 公会创建、管理、战争
- **MU Helper** — 官方挂机辅助系统

## AI Player 系统

AI Player 系统是 OpenMU 的扩展层，位于 `src/AIPlayer/` 目录。它允许在服务器中运行由 AI 控制的虚拟玩家角色，用于：

- **负载测试** — 模拟大量玩家连接，测试服务器承受能力
- **行为验证** — 验证游戏逻辑的正确性和完整性
- **自动化运营** — AI 角色可以参与正常游戏循环（打怪、合成、交易）

AI Player 继承自标准 `Player` 类（`src/AIPlayer/AiPlayer.cs`），通过 `IAiService` 接口管理生命周期。详情见 `16-ai-player-system-design.md`。

## 版本信息

| 项目 | 版本 |
|------|------|
| 解决方案 | MUnique.OpenMU.sln |
| 包管理 | Directory.Packages.props（集中式） |
| 命名规范 | StyleCop 规则校验（.editorconfig） |
