# 编译系统

## 解决方案

OpenMU 使用单一的解决方案文件管理所有项目：

```
src/MUnique.OpenMU.sln
```

包含的主要项目：

| 项目文件 | 类型 | 用途 |
|----------|------|------|
| `src/AIPlayer/MUnique.OpenMU.AIPlayer.csproj` | 类库 | AI 玩家系统 |
| `src/AIPlayer.Tests/MUnique.OpenMU.AIPlayer.Tests.csproj` | 测试 | AI 单元测试 |
| `src/GameLogic/MUnique.OpenMU.GameLogic.csproj` | 类库 | 游戏逻辑核心 |
| `src/GameServer/MUnique.OpenMU.GameServer.csproj` | 类库 | 游戏服务器 |
| `src/ConnectServer/MUnique.OpenMU.ConnectServer.csproj` | 类库 | 连接服务器 |
| `src/LoginServer/MUnique.OpenMU.LoginServer.csproj` | 类库 | 登录服务器 |
| `src/ChatServer/MUnique.OpenMU.ChatServer.csproj` | 类库 | 聊天服务器 |
| `src/FriendServer/MUnique.OpenMU.FriendServer.csproj` | 类库 | 好友服务器 |
| `src/GuildServer/MUnique.OpenMU.GuildServer.csproj` | 类库 | 战盟服务器 |
| `src/Network/MUnique.OpenMU.Network.csproj` | 类库 | 网络层 |
| `src/Persistence/MUnique.OpenMU.Persistence.csproj` | 类库 | 持久化层 |
| `src/Persistence/EntityFramework/MUnique.OpenMU.Persistence.EntityFramework.csproj` | 类库 | EF Core 实现 |
| `src/Persistence/InMemory/MUnique.OpenMU.Persistence.InMemory.csproj` | 类库 | InMemory 实现 |
| `src/Startup/MUnique.OpenMU.Startup.csproj` | 启动项目 | 服务器启动入口 |
| `src/Pathfinding/MUnique.OpenMU.Pathfinding.csproj` | 类库 | 寻径算法 |
| `src/Orchestrator/MUnique.OpenMU.Orchestrator.csproj` | 类库 | DAG 编排库（零游戏依赖） |
| `src/Web/AdminPanel/MUnique.OpenMU.AdminPanel.csproj` | Web | 管理面板 |
| `src/Web/Map/MUnique.OpenMU.Web.Map.csproj` | Web | 实时地图 |
| `src/PlugIns/MUnique.OpenMU.PlugIns.csproj` | 类库 | 插件系统 |

## 项目引用关系

```
Startup (启动入口)
 ├── GameLogic
 │    └── PlugIns
 ├── GameServer
 │    ├── Network
 │    └── GameLogic
 ├── ConnectServer
 │    └── Network
 ├── ChatServer
 │    ├── Network
 │    └── GameLogic (部分)
 ├── AIPlayer
 │    ├── GameLogic
 │    ├── Pathfinding
 │    └── PlugIns
 ├── Persistence.EntityFramework
 │    └── Persistence
 └── Web.AdminPanel / Web.Map
      └── 前端 Blazor 渲染
```

## 编译配置

### 解决方案配置

| 配置 | 说明 |
|------|------|
| `Debug` | 调试模式，包含调试符号 |
| `Release` | 发布模式，开启优化 |

### 关键构建属性

`src/Directory.Build.props` 定义全局编译属性：

```xml
<!-- 使用最新的 C# 13 语言特性 -->
<LangVersion>13</LangVersion>
<!-- 启用可空引用类型 -->
<Nullable>enable</Nullable>
<!-- 使用集中式包管理 -->
<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
```

`src/Directory.Packages.props` 定义集中式 NuGet 包版本。

### 编译命令

```bash
# 标准编译（Release）
dotnet build src/MUnique.OpenMU.sln -c Release

# 调试编译
dotnet build src/MUnique.OpenMU.sln -c Debug

# 清理 + 编译（ci=true 禁用缓存）
dotnet build src/MUnique.OpenMU.sln -c Release -p:ci=true

# 运行测试
dotnet test tests/ --filter "Category=UnitTest"

# 仅编译 AI Player 项目
dotnet build src/AIPlayer/

# 编译并运行服务器（Demo 模式）
dotnet run --project src/Startup/ -- -demo -autostart -resolveIP:local
```

### ci=true 编译

设置 `ci=true` 时：
- 禁用 NuGet 缓存（清理 `~/.nuget/packages` 中的缓存文件）
- 执行完整的依赖解析
- 用于 CI/CD 流水线（`azure-pipelines.yml`）

## Dapr 部署

`src/Dapr/` 目录包含 Dapr 微服务部署的 Host 项目：

| 项目 | 用途 |
|------|------|
| `AdminPanel.Host` | Dapr 模式的管理面板 |
| `ChatServer.Host` | Dapr 模式的聊天服务器 |
| `ConnectServer.Host` | Dapr 模式的连接服务器 |
| `FriendServer.Host` | Dapr 模式的好友服务器 |
| `GameServer.Host` | Dapr 模式的游戏服务器 |
| `GuildServer.Host` | Dapr 模式的战盟服务器 |
| `LoginServer.Host` | Dapr 模式的登录服务器 |

Dapr 模式为可选部署方案，非必须。默认部署使用独立控制台进程。

## 代码规范强制

- **StyleCop** — 项目已配置 `.editorconfig` 和 `stylecop.json`
- **XML 文档注释** — 所有公共 API 必须有 XML 文档（SA1611 规则）
- **文件结构** — 每个文件一个 public 类型（SA1402 规则）
- **成员排序** — 构造函数在属性之前（SA1201 规则）
