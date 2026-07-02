# 危险区清单（Dangerous Zones）

本文件列出项目中禁止修改或需极高谨慎操作的文件和代码区域。未经明确的架构评审（Architecture Review），不得修改以下区域。

## 禁止修改清单

以下文件/目录列为**永久危险区**，除非有强制的架构升级需求并经完整评审：

| 区域 | 风险等级 | 原因 |
|------|----------|------|
| `Network/Packets/` | **禁止修改** | 协议生成文件，由 XML + XSLT 模板控制。手动修改会导致协议版本偏移，后续无法通过模板重建。如需变更协议，必须修改上游 XML/XSLT 定义后重新生成。 |
| `Startup/` 中的硬编码服务注册 | **禁止修改** | 服务注册顺序和容器配置直接影响整个服务器启动链路。除非新增独立插件（通过 PlugIns 接口注册），否则不得改动现有注册逻辑。 |
| `Migrations/` | **禁止修改** | 数据库迁移文件是已执行的历史记录。如需修改数据模型，必须创建新的迁移文件（`dotnet ef migrations add`），不得编辑或删除现有迁移。 |
| `GameLogic/` 中的核心状态机逻辑 | **极高谨慎** | `PlayerState` 状态转换路径定义了游戏合法性（如 LoginScreen → Authenticated → CharacterSelection 等）。任意状态转换的修改可能导致客户端/服务器不同步、角色卡死或利用漏洞。 |

## 已修复危险区记录表

以下是在近期开发周期中发现的危险区，**已修复**。任何对上述区域的再次修改必须经过 Code Review，防止回退。

| 修复项 | 文件 | 说明 |
|--------|------|------|
| IP 解析器崩溃 | `ConfigurableIpResolver.cs` | `Custom` 模式下 `_parsedAddress` 为 `null` 时直接抛出 `ArgumentNullException`，导致服务器启动失败。修复为 `null` 时降级为 `Loopback` 地址，不再 throw。 |
| IP 配置覆盖 | `Program.cs:1143-1149` | `_systemConfiguration` 参数在解析时覆盖了 `-resolveIP:local` 命令行传入的 IP 配置，导致 `local` 参数失效。已移除该参数的干扰路径。 |
| TypeScript 编译 | `Web.Map.csproj` | 引用了 `Microsoft.TypeScript.MSBuild`，但项目无 TypeScript 源码，构建时产生不可恢复错误。已移除该 NuGet 依赖。 |
| PetBehaviour 反射 | `PetHandlerModule.cs` | `PetBehaviour` 为内部枚举，外部模块无法直接引用。修复为通过反射调用，避免编译错误。 |
| LocalizedString.Contains | 多处 | `LocalizedString` 类型重写了 `Contains` 方法，当用于字符串过滤时实际检查的是键名而非文本内容。已全部改为 `.ToString().Contains()` 确保按文本内容匹配。 |
| 拾取覆盖补齐 | `ValueAssessmentService.cs` | AI 拾取门控缺少 `PickAllItems`/`PickZen`/`ExtraItemNames` 配置，导致非宝石物品不会被拾取。已补齐三项拾取覆盖配置。 |

## 注意事项

1. **Code Review 硬性要求：** 任何对上述已修复区域的再次修改，必须至少有 1 名架构师级别的 Code Review，并在 PR 描述中明确引用本文件的对应条目。
2. **WIKI 先行原则：** 如需扩展或修改危险区逻辑，必须先更新本文件，再修改代码。禁止跳过文档步骤直接改代码。
3. **测试覆盖：** 每次修改危险区后，至少运行以下验证：
   - `dotnet build` — 0 error
   - `dotnet test --filter "AiPlayer"` — 全部通过
   - 服务器 Demo 模式启动 — 无 Exception/Error
4. **回退计划：** 危险区修改必须附带回退方案，确保在 5 分钟内可以 `git revert` 恢复到修改前状态。
