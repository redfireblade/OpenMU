# B3-S3: 组队 API（Party API）

## 目标
在 `IGameAdapter` 中添加三个组队操作方法，在 `GameAdapter` 中实现，
使 AI 脚本和模块可以通过抽象接口操作组队逻辑。

---

## 接口变更

### IGameAdapter 新增

```csharp
/// <summary>加入指定玩家的队伍。</summary>
/// <param name="targetPlayerName">目标玩家名称。</param>
/// <returns>是否成功发出邀请或加入。</returns>
ValueTask<bool> PartyJoinAsync(string targetPlayerName);

/// <summary>离开当前队伍。</summary>
ValueTask PartyLeaveAsync();

/// <summary>跟随队长（走向 PartyMaster 位置）。</summary>
/// <param name="followDistance">保持在队长周围的距离（默认 3 格）。</param>
ValueTask PartyFollowAsync(float followDistance = 3f);
```

### 设计决策
- `PartyJoinAsync` 包装 `PartyRequestAction` + 自动同意（`PartyResponseAction`）
  — AI 不需要等待玩家手动同意，直接通过自己的 LastPartyRequester 流程完成。
- `PartyLeaveAsync` 调用 `Party.KickMySelfAsync`。
- `PartyFollowAsync` 提取 ScriptExecutor.FollowLeaderAsync 中的逻辑到 GameAdapter。

---

## 实现细节

### GameAdapter.PartyJoinAsync
1. 通过 `GameContext.GetPlayerByCharacterName` 查找目标玩家
2. 调用 `PartyRequestAction.HandlePartyRequestAsync(this._player, targetPlayer)`
3. 目标 AI 自动接受：`PartyResponseAction.HandleResponseAsync(targetPlayer, true)`
4. 如果目标不是 AI 玩家，只发送组队请求，等待服务器处理

### GameAdapter.PartyLeaveAsync
1. 检查 player.Party 不为 null
2. 调用 `player.Party.KickMySelfAsync(player)`

### GameAdapter.PartyFollowAsync
1. 获取 PartyMaster 位置
2. 检查是否同地图
3. 计算距离，如果 > followDistance 则寻路过去

---

## 测试场景

- AI → AI 组队：两个 AI 玩家，一个创建队伍并邀请另一个加入
- AI 离队：AI 从队伍中自行离开
- AI 跟随队长：AI 自动走向队长位置

---

## 依赖

- `src/GameLogic/PlayerActions/Party/PartyRequestAction.cs`
- `src/GameLogic/PlayerActions/Party/PartyResponseAction.cs`
- `src/GameLogic/Party.cs`（`KickMySelfAsync`）
