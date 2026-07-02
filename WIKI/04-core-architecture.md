# 核心架构

## 服务器分层

OpenMU 采用经典的 MU Online 多服务器架构：

```
ConnectServer (连接服务器)
    ↓ 版本验证、服务器列表
LoginServer (登录服务器)
    ↓ 账户认证
GameServer (游戏服务器)
    ├── ChatServer (聊天服务器)
    ├── FriendServer (好友服务器)
    └── GuildServer (战盟服务器)
```

## 启动流程

启动入口在 `src/Startup/Program.cs`，流程如下：

1. **命令行参数解析** — 支持以下参数：
   - `-autostart` — 自动启动所有服务器（无需控制台输入）
   - `-demo` — Demo 模式（使用 InMemory 持久化）
   - `-resolveIP:local` — 使用本地回环 IP
   - `-resolveIP:public` — 使用公网 IP
   - `-nocache` — 不缓存配置
   - `-port XXXX` — 指定端口
   - `-i InstallPath` — 指定安装路径

2. **配置加载** — 初始化 `GameConfiguration`，从 DB 或 JSON 加载

3. **服务容器创建** — 通过 `ServerContainerBase` 的派生类创建：
   - `ConnectServerContainer` — `src/Startup/ConnectServerContainer.cs`
   - `GameServerContainer` — `src/Startup/GameServerContainer.cs`
   - `ChatServerContainer` — `src/Startup/ChatServerContainer.cs`

4. **依赖注入** — 使用 ASP.NET Core DI 容器注册服务

5. **服务器启动** — 监听端口，等待客户端连接

## 关键文件

| 文件 | 职责 |
|------|------|
| `src/Startup/Program.cs` | 主入口：参数解析、启动协调 |
| `src/Startup/GameServerContainer.cs` | 游戏服务器安装和配置 |
| `src/Startup/ConnectServerContainer.cs` | 连接服务器安装和配置 |
| `src/GameLogic/` | 游戏规则和玩家行为 |
| `src/GameServer/` | 网络消息处理和远程视图 |
| `src/Network/` | 网络层（协议解析、加密） |

## 关键抽象

| 抽象 | 实现 | 用途 |
|------|------|------|
| `IGameContext` | `GameContext` | 游戏世界上下文（管理所有玩家、地图、怪物） |
| `IPlayer` | `Player` | 玩家实体接口 |
| `IGameServerContext` | `GameServerContext` | 游戏服务器上下文 |
| `IViewPlugIn` | 多种 | 客户端视图插件（登录、地图、背包等） |
| `ICustomPlugInContainer` | `PlugInContainer` | 插件容器 |
