# AOI 广播系统与 NPC/Monster 系统

## 桶（Bucket）系统

### 参数配置
- `ChunkSize = 8` (MapInitializer.cs)
- 地图 256×256 被分为 **32×32 = 1024 个桶**
- 每个桶覆盖 8×8 区域

### 桶索引计算
```csharp
index = (point.X / 8) + ((point.Y / 8) * 32)
```

## 进入视野广播链路

```
对象加入桶 → Bucket.ItemAdded 事件
  → 订阅事件的玩家收到 LocateableAddedAsync
    → ObserverToWorldViewAdapter 按类型分发:
      - Player → INewPlayersInScopePlugIn → AddCharactersToScope
      - NPC → NewNpcsInScopePlugIn → AddNpcsToScope
      - Drop → IShowDroppedItemsPlugIn → DroppedItem
    → 对 IObservable: AddObserverAsync(当前玩家)
```

## 移出视野广播链路

```
LocateablesOutOfScopeAsync:
  → 从 _observingObjects 移除
  → RemoveObserverAsync
  → 非掉落: IObjectsOutOfScopePlugIn → MapObjectOutOfScope
  → 掉落: IDroppedItemsDisappearedPlugIn → 消失包
```

## 移动广播链路

```
MoveAsync(obj, target, moveLock, moveType):
  1. MoveObjectOnMapAsync:
     - 跨桶? → 旧桶.Remove + 新桶.Add(触发观察者事件)
     - 不跨桶? → 只更新Position
  2. ForEachWorldObserverAsync<IObjectMovedPlugIn> → ObjectMoved广播
  3. 跨桶且Player → UpdateObservingBucketsAsync(重算观察桶)
```

## Monster vs Player 核心差异

| 维度 | Monster | Player |
|------|---------|--------|
| AOI角色 | 仅被观察(IObservable) | 观察+被观察(IBucketMapObserver) |
| 观察桶管理 | 无 | ObserverToWorldViewAdapter |
| 移动触发AOI | MoveAsync广播MoveType.Walk | 同上+跨桶触发桶重算 |
| AI驱动 | BasicMonsterIntelligence Timer | 客户端输入 |
| 路径计算 | 服务端A*(对象池) | 客户端自行寻路 |
| 生命周期 | 有观察者→Start()，无→Pause() | 进地图即激活 |
| 目标选择 | SearchNextTargetAsync(最近) | 玩家自行选择 |

## 怪物 NPC 继承链

```
NonPlayerCharacter (IObservable, IRotatable, ILocateable)
  → AttackableNpcBase (IAttackable: Attributes, IsAlive, AttackByAsync)
    → Monster (IAttackable+IAttacker+ISupportWalk: _intelligence, _walker, _pathFinderPool)
    → Destructible
    → TrapIntelligenceBase → (4种陷阱AI)
```

## 怪物 AI 主循环

```
Timer (间隔 = AttackDelay + 随机偏移):
  → SafeTick (catch-all异常)
    → TickAsync:
      1. 是否存活? → 否: 清除目标
      2. 正在行走? → 是: 等待
      3. 眩晕/睡眠? → 是: 返回
      4. ResolveTargetAsync → 保持或搜索目标
      5. 无目标? → TickWithoutTargetAsync(随机移动)
      6. 范围+非安全区? → AttackAsync
      7. 视野内? → WalkToAsync
      8. 兜底 → RandomMoveAsync
```

## 关键文件

| 文件 | 行号 |
|------|------|
| `src/GameLogic/GameMap.cs` | 全文件(287行) |
| `src/GameLogic/BucketAreaOfInterestManager.cs` | 全文件(205行) |
| `src/GameLogic/BucketMap{T}.cs` | 全文件(129行) |
| `src/GameLogic/ObserverToWorldViewAdapter.cs` | 全文件(293行) |
| `src/GameLogic/ObservableExtensions.cs ForEachWorldObserverAsync` | 21 |
| `src/GameLogic/NPC/Monster.cs` | 全文件(404行) |
| `src/GameLogic/NPC/AttackableNpcBase.cs` | 全文件(420行) |
| `src/GameLogic/NPC/NonPlayerCharacter.cs` | 全文件(233行) |
| `src/GameLogic/NPC/BasicMonsterIntelligence.cs` | 全文件(297行) |
| `src/GameServer/RemoteView/World/NewPlayersInScopePlugIn.cs` | 全文件 |
| `src/GameServer/RemoteView/World/NewNpcsInScopePlugIn.cs` | 全文件 |
| `src/GameServer/RemoteView/World/ObjectMovedPlugIn.cs` | 全文件 |
