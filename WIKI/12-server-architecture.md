# 服务器架构

## 启动顺序

OpenMU 服务器的启动入口为 `src/Startup/Program.cs`，执行以下流程：

```
1. 解析命令行参数
2. 加载游戏配置（GameConfiguration）
3. 创建 ConnectServer 容器并启动
4. 创建 GameServer 容器并启动
5. GameServer 内部自动启动：
   ├── ChatServer
   ├── FriendServer
   └── GuildServer
6. 等待客户端连接
```

| 步骤 | 组件 | 端口（默认） | 作用 |
|------|------|-------------|------|
| 1 | Program.cs | - | 参数解析、DI 配置 |
| 2 | GameConfiguration | - | 加载地图、怪物、物品、掉落等配置 |
| 3 | ConnectServer | 44405 | 客户端连接第一站，版本验证 |
| 4 | GameServer | 55901 | 游戏逻辑主服务器 |
| 5a | ChatServer | 55903 | 聊天服务 |
| 5b | FriendServer | 55905 | 好友服务 |
| 5c | GuildServer | 55906 | 战盟服务 |

## 配置方式

### 命令行参数

| 参数 | 示例 | 说明 |
|------|------|------|
| `-autostart` | `./OpenMU -autostart` | 自动启动所有服务器（无需控制台输入） |
| `-demo` | `./OpenMU -demo` | Demo 模式（使用 InMemory 持久化，数据不保存） |
| `-resolveIP:local` | `./OpenMU -resolveIP:local` | 使用本地回环 IP（127.0.0.1） |
| `-resolveIP:public` | `./OpenMU -resolveIP:public` | 使用公网 IP（自动检测） |
| `-resolveIP:custom:1.2.3.4` | `./OpenMU -resolveIP:custom:1.2.3.4` | 使用自定义 IP |
| `-nocache` | `./OpenMU -nocache` | 不缓存配置 |
| `-port XXXX` | `./OpenMU -port 55901` | 指定 GameServer 端口 |

### IP 解析器

`ConfigurableIpResolver`（`src/Network/ConfigurableIpResolver.cs`）管理 IP 解析逻辑：

- `Custom` 模式 — 使用自定义 IP 地址
- `Local` 模式 — 使用 127.0.0.1（回环地址）
- `Public` 模式 — 自动检测公网 IP

**已修复问题：** Custom 模式下当 `_parsedAddress` 为 `null` 时不再抛出 `ArgumentNullException`，降级为 Loopback 地址。

## Demo 模式

Demo 模式使用 `InMemory` 持久化（`src/Persistence/InMemory/`），所有数据保存在内存中。适用于：

- 开发和调试
- 自动化测试
- 功能演示

启动方式：`./OpenMU -demo -autostart -resolveIP:local`

## 服务容器架构

每个服务器类型有对应的容器类，管理其生命周期：

| 容器类 | 文件 | 职责 |
|--------|------|------|
| `ConnectServerContainer` | `src/Startup/ConnectServerContainer.cs` | 连接服务器安装和配置 |
| `GameServerContainer` | `src/Startup/GameServerContainer.cs` | 游戏服务器安装（含 AI Player 管理器的注册） |
| `ChatServerContainer` | `src/Startup/ChatServerContainer.cs` | 聊天服务器安装 |
| `ServerContainerBase` | `src/Startup/ServerContainerBase.cs` | 抽象基类，提供公共配置方法 |

所有容器继承自 `ServerContainerBase`，遵循相同的安装模板：

```
1. 设置 TCP 监听端口
2. 初始化持久化上下文（EF Core / InMemory）
3. 注册 DI 服务
4. 启动服务器
5. 注册服务器到管理控制台
```

## AI Player 注册

AI Player 系统通过 `GameServerContainer` 注册到 DI 容器：

```
DI 注册:
  IAiService → AiPlayerManager (Singleton)
  IAiDebugService → AiPlayerManager (Singleton)

AI 集成:
  AiPlayerManager 在构造时自动启动：
    - EventWatcherService（群体级事件监控）
    - Experience Aggregation Timer（经验聚合定时器）
    - Auto Event Trigger（自动活动触发）
```
