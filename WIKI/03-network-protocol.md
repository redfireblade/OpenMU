# 网络协议

## 通信方式

OpenMU 使用基于 TCP 的自定义协议，遵循 **C1/C2 封包** 格式：

| 类型 | 结构 | 说明 |
|------|------|------|
| C1 封包 | `[1 byte 类型][1 byte 长度][N bytes 数据]` | 加密后不超过 255 字节 |
| C2 封包 | `[1 byte 类型][2 bytes 长度][N bytes 数据]` | 加密后超过 255 字节（最大 65535） |

### 协议号分类

协议号由 `[类型前缀][代码号]` 组成。常用示例：

| 协议号 | 方向 | 用途 |
|--------|------|------|
| `0xC1, 0xF1` | S→C | 登录响应 |
| `0xC1, 0xF3` | C→S | 角色选择操作 |
| `0xC2, 0x12` | S→C | 游戏地图完整数据 |
| `0xC1, 0xD0` | C→S | 聊天消息 |
| `0xC1, 0xB0` | C→S | 交易操作 |

完整的协议包定义位于 `src/Network/Packets/` 目录，按方向分为 `ClientToServer/` 和 `ServerToClient/`。

## 加密方式

OpenMU 实现了三种加密/解密机制：

### 1. XOR-3 加密（经典版本）

用于 v0.75 版本的简单逐字节 XOR。

```
默认密钥: { 0xFC, 0xCF, 0xAB }
```

算法：每字节数据与密钥的对应字节 XOR，密钥循环使用。

实现文件：`src/Network/Xor/Xor3Encryptor.cs`、`src/Network/Xor/Xor3Decryptor.cs`

### 2. XOR-32 加密（流水线版本）

用于 Season 6+ 版本，使用 32 字节密钥进行流水线式 XOR 加密。

```
默认密钥: { 0xAB, 0x11, 0xCD, 0xFE, ... 共 32 字节 }
```

实现文件：`src/Network/Xor/PipelinedXor32Encryptor.cs`、`src/Network/Xor/PipelinedXor32Decryptor.cs`

### 3. SimpleModulus 加密

使用模乘运算的加密方式，用于特定协议包。

实现文件：`src/Network/SimpleModulus/`

## 包定义生成

**重要：** `src/Network/Packets/` 下的协议生成文件由上游 XML 定义 + XSLT 转换生成。手动修改这些文件会导致协议版本偏移，后续无法通过模板重建。如需变更协议，必须修改上游 XML/XSLT 定义后重新生成。

## 网络架构层级

```
客户端 → ConnectServer (连接/版本验证)
     ↓ 成功
客户端 → LoginServer (账户认证)
     ↓ 认证通过
客户端 → GameServer (游戏逻辑，常连接)
     ↓
ChatServer / FriendServer (辅助服务)
```
