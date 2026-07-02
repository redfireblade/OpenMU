# 目录结构

```
src/
  MUnique.OpenMU.sln               # 解决方案文件
  Directory.Build.props             # 全局 MSBuild 属性
  Directory.Packages.props          # 集中式 NuGet 包管理
  stylecop.json                     # StyleCop 配置

  AIPlayer/                         # AI 玩家系统（扩展层）
    AiPlayer.cs                     # AI 玩家实体（继承 Player）
    AiPlayerManager.cs              # AI 玩家管理器（IAiService + IAiDebugService）
    AiPlayerLogic.cs                # 行为逻辑驱动器（每 tick 循环）
    AiPlayerState.cs                # AI 状态快照
    IAiService.cs                   # AI 服务公共接口
    IAiDebugService.cs              # AI 调试接口
    PersonalityProfile.cs           # AI 人格配置文件
    KnowledgeAccessService.cs       # 知识库门控访问
    Knowledge/                      # 游戏世界知识库（GameKnowledgeService）
    Decision/                       # 决策子模块（规则引擎、任务、事件等）
    Map/                            # 数字地图和空间认知（AiMap、RouteFollower 等）
    World/                          # 世界状态管理
    Core/                           # 核心抽象（BehaviorContext、IGameAdapter 等）
    Scripting/                      # 脚本引擎（ScriptExecutor、BehaviorScript）
    Scripts/                        # JSON 行为脚本文件（5 个内置脚本）
    Config/                         # AI 配置

  GameLogic/                        # 游戏逻辑核心
    PlayerActions/                  # 玩家动作（移动、攻击、交易等）
    NPC/                            # NPC 和怪物逻辑
    PlugIns/                        # 插件注册（聊天命令、事件等）
    Views/                          # 视图层接口
    MuHelper/                       # 官方挂机辅助
    Pet/                            # 宠物系统
    Attributes/                     # 属性系统集成
    MiniGames/                      # 小游戏逻辑（恶魔广场等）

  GameServer/                       # 游戏服务器
    MessageHandler/                 # 客户端消息处理器
    RemoteView/                     # 远程视图实现

  ConnectServer/                    # 连接服务器
  LoginServer/                      # 登录服务器
  ChatServer/                       # 聊天服务器
  FriendServer/                     # 好友服务器
  GuildServer/                      # 战盟服务器

  Network/                          # 网络层
    Packets/                        # 协议包定义（由 XML + XSLT 生成，禁止手动修改）
    Xor/                            # XOR 加密/解密
    SimpleModulus/                  # SimpleModulus 加密
    PlugIns/                        # 网络插件

  DataModel/                        # 数据模型定义
    Entities/                       # 实体类
    Configuration/                  # 配置类
    Attributes/                     # 属性定义

  Persistence/                      # 持久化层
    EntityFramework/                # EF Core 实现（含 Migrations/）
    Initialization/                 # 初始数据（多版本支持）
    InMemory/                       # 内存实现（用于测试）

  Startup/                          # 服务器启动入口
    Program.cs                      # 主启动类
    GameServerContainer.cs          # 游戏服务器容器
    ConnectServerContainer.cs       # 连接服务器容器

  Web/                              # Web 前端
    AdminPanel/                     # 管理面板（Blazor Server）
    Map/                            # 实时地图（SignalR + Three.js）
    Shared/                         # 共享组件

  Pathfinding/                      # 寻径算法
    Algorithms/                     # A*、Weighted A*、Ant Colony、GA、Diffusion、Random Walk
    PreCalculation/                 # 预计算

  Orchestrator/                     # DAG 编排库（零游戏依赖）
    Core/                           # 执行图和拓扑排序
    Abstractions/                   # 抽象接口
    Context/                        # 执行上下文
    Config/                         # 图配置
    Telemetry/                      # 遥测

  PlugIns/                          # 插件系统基类
  Interfaces/                       # 公共接口定义
  AttributeSystem/                  # 属性系统
  Annotations/                      # 注解支持
  WorldState/                       # 世界状态同步

tests/                              # 测试项目
  AIPlayer.IntegrationTests/        # AI 集成测试
  ...

WIKI/                               # 规范文档目录（当前目录）
docs/                               # 历史文档
```
