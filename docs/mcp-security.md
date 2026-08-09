# MCP 安全模型

## 1. 目的

TermSquared 同时作为 MCP Client 和 MCP Server：

- 作为 Client 时，TermSquared 会启动本地 `stdio` MCP Server，或连接远程 Streamable HTTP MCP Server。
- 作为 Server 时，TermSquared 会向 Claude Desktop、OpenCode 等 AI 客户端暴露受控的 SSH/SFTP 能力。

两种方向具有不同信任边界，不能因为某个 MCP Server 或 Client 可以建立协议连接，就默认信任其工具、参数、身份声明或返回内容。

本文是首期安全基线。实现不得降低这些默认规则；任何放宽都需要独立 ADR、威胁分析和测试。

## 2. 安全目标

1. 未经明确发布的连接、目录和能力对 MCP 不可见。
2. 读取操作限制在预先配置的连接和远端 root 内。
3. 任意命令和写操作必须由本地可信用户逐次批准。
4. AI Client、MCP Server 和工具描述不能自行提升权限。
5. 凭据、Token 和私钥不能进入模型上下文、普通配置或日志。
6. 重试、断线和重复请求不能造成高风险操作重复执行。
7. TermSquared UI 或 Broker 不可用时，高风险操作安全失败。
8. 所有决策和执行结果可审计，但审计内容必须脱敏。

## 3. 信任边界

```text
Untrusted / External
├─ AI Client
├─ Outbound MCP Server
├─ MCP Tool annotations and schemas
├─ Remote SSH/SFTP/FTP Server
├─ Remote file names and contents
└─ OAuth and HTTP metadata endpoints

Trusted Application Boundary
├─ TermSquared Desktop Broker
├─ Local Policy Engine
├─ Approval Broker
├─ Credential Store Adapter
├─ Audit Sink
└─ Protocol Provider Adapters

Trusted User Interaction Boundary
└─ TermSquared local approval UI
```

重要规则：

- MCP `clientInfo` 和 `serverInfo` 仅用于显示，不能独立作为授权身份。
- Tool annotations 仅作为展示提示，不能决定本地风险等级。
- MCP elicitation 不能替代 TermSquared 本地审批。
- 远程返回的文件名、错误和文本都视为不可信数据。
- MCP Server 返回的 Tool Schema 不能触发任意本地类型加载或代码执行。

## 4. 身份模型

每个入站 MCP 请求必须产生内部 `CallerIdentity`：

```csharp
public sealed record CallerIdentity
{
    public required string Id { get; init; }
    public required string Transport { get; init; }
    public string? DisplayName { get; init; }
    public string? AuthenticatedSubject { get; init; }
    public bool IsAuthenticated { get; init; }
}
```

不同 transport 的身份来源：

| Transport | 身份依据 |
|---|---|
| stdio Bridge | 本地 IPC 对端身份、Bridge 实例 ID、启动配置 |
| Loopback HTTP | Bearer token 和连接实例 |
| Future remote HTTP | OAuth/JWT subject、issuer、audience 和 scopes |

显示名称不能替代可验证身份。无法验证的调用方必须明确标记为未认证，并受最严格策略限制。

## 5. 发布模型

TermSquared 不自动发布全部连接。用户必须显式创建 `McpPublishedScope`：

```csharp
public sealed record McpPublishedScope
{
    public required Guid ConnectionProfileId { get; init; }
    public required string PublicAlias { get; init; }
    public IReadOnlyList<string> ReadableRoots { get; init; } = [];
    public bool AllowRead { get; init; } = true;
    public bool AllowCommandWithApproval { get; init; }
    public bool AllowWriteWithApproval { get; init; }
}
```

发布规则：

- 默认没有任何连接被发布。
- 发布时使用 opaque ID 或别名，不暴露内部 Profile ID。
- 每个连接必须单独发布。
- SFTP 读取必须配置至少一个允许 root。
- `AllowCommandWithApproval=false` 时，即使用户在 Dialog 中操作，也不能执行 MCP 命令调用。
- `AllowWriteWithApproval=false` 时，不创建任何 MCP 写操作审批请求，直接拒绝。
- 删除发布配置立即阻止新请求；正在执行的只读请求按取消策略终止。
- 凭据、私钥路径和内部配置路径不属于可发布数据。

## 6. MCP 资源 URI

资源使用 TermSquared 自定义 scheme，不把真实主机名和用户名放进 URI：

```text
termsquared-ssh:///connections/{opaque-id}/status
termsquared-sftp:///connections/{opaque-id}/files/{encoded-path}
```

要求：

- `opaque-id` 不能由内部数据库 ID 直接推断。
- URI 中的路径必须规范化和解码一次，禁止重复解码绕过检查。
- 资源内容必须经过大小、类型和输出限制。
- 资源 URI 不包含密码、Token、私钥信息或临时 Approval Grant。

## 7. 工具分类

### 7.1 预授权只读工具

首期计划提供：

```text
connections.list
connections.get_status
sftp.list_directory
sftp.stat
sftp.read_text
sftp.hash
```

这些工具可以在发布范围内无逐次 Dialog 调用，但仍必须通过：

- 调用方认证或本地 transport 策略
- 已发布连接检查
- 已发布 root 检查
- 参数 Schema 校验
- 并发、速率和输出上限
- 审计

`connections.list` 只返回发布别名、协议、能力和脱敏状态，不返回：

- 密码或凭据引用
- 私钥或私钥路径
- 未发布连接
- 内部数据库主键
- 不必要的用户名和网络拓扑

### 7.2 逐次批准工具

首期计划提供：

```text
connections.connect
connections.disconnect
ssh.exec
sftp.write_text
sftp.upload
sftp.mkdir
sftp.rename
sftp.remove
sftp.chmod
```

所有这些调用必须逐次本地批准。

即使 `ssh.exec` 命令看起来是 `ls`、`cat` 或 `git status`，也不能降级为只读，因为 Shell 命令可能包含：

- 命令替换
- 重定向
- Alias 或 Function
- Shell startup hook
- 环境依赖副作用
- 远程程序自身副作用

### 7.3 首期禁止的工具

```text
ssh.forward_port
ssh.reverse_forward
ssh.dynamic_proxy
sftp.sync
ftp.sync
rdp.control
vnc.control
credentials.get
credentials.export
mcp.servers.add
mcp.servers.update
```

特别禁止模型通过 MCP 创建或修改 `stdio` MCP Server 配置，因为该能力等价于以当前用户权限执行任意本地程序。

## 8. 路径安全

SFTP/FTP 读取和写入必须执行路径规范化及 root containment。

检查顺序：

1. 按远端协议语义解析输入路径。
2. 拒绝 NUL 和非法编码。
3. 规范化 `.`、`..` 和重复分隔符。
4. 获取或解析最终目标路径。
5. 处理符号链接。
6. 验证最终路径仍位于允许 root 内。
7. 拒绝不允许的特殊文件类型。
8. 执行操作前再次验证关键前置条件，降低 TOCTOU 风险。

默认拒绝：

- 逃出允许 root 的路径
- 指向 root 外部的符号链接
- device、socket、FIFO 等特殊文件
- 超过最大深度的路径
- 超过最大长度的路径
- 无法可靠规范化的路径

不能只用字符串 `StartsWith` 判断路径包含关系。

## 9. 读取限制

首期默认限制应通过配置集中管理，具体数值在性能验证后确定。必须存在以下上限：

- 单次目录最大条目数
- 单次目录分页大小
- 单文件文本读取最大字节数
- 单次 Tool Result 最大字节数
- Hash 操作最大并发数和超时
- 单调用最大执行时间
- 单调用方并发请求数
- 全局并发 SSH/SFTP 操作数

首期不提供任意大文件的 `read_binary`。后续如果支持二进制资源，应使用流式或资源下载机制，不能把无限制 Base64 放进 JSON 响应。

## 10. Approval Broker

高风险调用统一经过：

```text
MCP Tool Call
  → Caller Identity
  → Tool Registry
  → Local Risk Classification
  → Policy Evaluation
  → Approval Queue
  → Trusted TermSquared Dialog
  → One-Time Grant
  → Remote Execution
  → Audit Event
  → MCP Result
```

### 10.1 审批窗口内容

审批窗口必须完整显示：

- transport：stdio 或 HTTP
- 可验证的调用方身份
- MCP Client 显示名，并注明其不是授权依据
- Tool 名称
- 目标连接别名、主机和用户
- 工作目录
- 原始命令或完整文件操作参数
- 是否覆盖、删除、递归、创建链接或修改权限
- timeout
- 最大输出或写入大小
- `operationId`

命令和路径不能只显示截断摘要。可以提供摘要，但用户必须能查看完整原文。

### 10.2 审批交互

- `Escape` 只能拒绝或取消。
- 关闭窗口只能拒绝或取消。
- `CloseOnBackdropClick=false`。
- 同一时刻只显示一个安全审批 Dialog，其余请求排队。
- 批准按钮不能作为默认焦点，避免误按 Enter。
- 禁止“永久允许所有命令”。
- 首期禁止记住命令和写操作批准。
- 请求在排队或显示期间取消后，审批立即失效。
- UI 不可用、Broker 正在关闭或桌面锁定策略不满足时，默认拒绝。

### 10.3 一次性 Grant

Grant 必须绑定：

```text
caller identity
request ID
operation ID
tool name
canonical arguments hash
target profile
expiration
single-use state
```

Grant：

- 不返回给 MCP Client。
- 不写入普通配置。
- 只能消费一次。
- 参数任何变化都会导致校验失败。
- 过期或取消后不能恢复。
- 应用重启后全部失效。

## 11. 防重放和结果不确定性

高风险工具必须携带或由 Server 生成唯一 `operationId`。

状态机：

```text
Received
  → PendingApproval
  → Declined
  → Approved
  → Executing
  → Completed
  → Failed
  → ResultUnknown
```

规则：

- 只读请求可以按明确策略重试。
- 命令和写操作不得自动重试。
- 相同 `operationId` 和参数不能执行两次。
- 相同 `operationId` 但参数不同，直接拒绝并记录安全事件。
- 网络断线或进程崩溃发生在执行后，不能假定操作未执行。
- 无法确认结果时返回 `ResultUnknown`，由用户人工处理。
- 去重记录需要保留一段有限时间，但不能包含 Secret。

## 12. stdio MCP Client 安全

TermSquared 作为 MCP Client 启动本地 MCP Server 时：

- `Command` 和 `Arguments` 分开保存和传递。
- 禁止通过 `cmd.exe /c`、`powershell -Command` 或 `/bin/sh -c` 拼接未信任参数。
- 创建配置时显示可执行文件绝对路径、参数、工作目录和环境变量名称。
- 命令、参数、工作目录或环境变量变化后重新请求信任。
- 环境变量中的 Secret 使用凭据引用，UI 和日志不显示值。
- stdout 专用于协议，出现非协议输出时标记服务异常。
- stderr 单独采集，并设置速率和容量上限。
- 每条消息和总响应设置大小上限。
- 关闭时先正常结束，超时后终止进程树。
- Windows 使用 Job Object 或等效方式避免孙进程残留。
- MCP Server 的工作目录不默认为 TermSquared 安装目录或凭据目录。

模型不能通过 MCP 调用增删改本地 MCP Server 配置。

## 13. Streamable HTTP Client 安全

TermSquared 作为 Client 时：

- 生产地址默认只允许 HTTPS。
- HTTP 仅允许显式批准的 loopback 开发地址。
- 使用注入的 `HttpClient` 和明确 timeout。
- 限制响应头和响应体大小。
- 对重定向逐跳检查协议、地址和端口。
- 默认拒绝 link-local、云 metadata 和未批准的私网目标。
- Token 只通过 Authorization Header 发送。
- Token 不进入 URL、普通日志或异常文本。
- OAuth metadata 和 discovery endpoint 同样执行 SSRF 防护。
- 校验 issuer、audience、state 和 PKCE。
- Refresh Token 只存入 OS Credential Store。

## 14. MCP Server Transport 安全

### 14.1 stdio Bridge

- Bridge 不持有 SSH 凭据。
- Bridge 通过受当前用户权限保护的 IPC 连接 Desktop Broker。
- Bridge 必须验证连接到预期 Broker 实例。
- stdout 只输出协议消息。
- 日志写 stderr 或文件。
- Broker 不可用时返回可理解的服务不可用错误。
- 不允许 Bridge 在无 UI 状态下自行执行高风险操作。

### 14.2 Loopback Streamable HTTP

首期只绑定：

```text
127.0.0.1
::1（完成独立验证后启用）
```

要求：

- 随机高熵 Bearer Token。
- Token 使用安全存储或仅存在于当前会话。
- 校验 Origin；未知浏览器 Origin 默认拒绝。
- 限制请求体、并发数和调用速率。
- 每个请求有 timeout 和 cancellation。
- 外部错误只返回稳定错误码、脱敏消息和 correlation ID。
- 不返回原始异常、栈、内部路径或网络拓扑。
- 服务关闭后 Token 失效。

### 14.3 远程 HTTP

首期不支持。未来启用前必须至少具备：

- HTTPS
- OAuth/JWT
- issuer 和 audience 验证
- scopes
- Token 轮换和撤销
- 可信代理配置
- 速率限制
- 远程调用方审计
- 审批 UI 不可达时的默认拒绝
- 独立安全评审

## 15. 凭据安全

普通配置只保存：

```text
CredentialReferenceId
```

不得保存：

- SSH 密码
- 私钥内容
- 私钥口令
- MCP Bearer Token
- OAuth Access/Refresh Token

首期 Windows 版本应使用 Windows Credential Manager 或 DPAPI 保护的本地存储。跨平台版本分别适配 macOS Keychain 和 Linux Secret Service。

Square 的 password input 只提供视觉掩码，底层仍是 .NET `string`。因此：

- 不把密码放入全局 Store。
- 不把密码绑定到长期存在的 ViewModel。
- 使用后尽快释放引用。
- 不在异常、日志、审计或 Tool Result 中回显。

## 16. SSH Host Key

- 未知 Host Key 必须展示算法和指纹，由用户确认。
- Host Key 变化必须强警告，默认拒绝。
- 不提供全局“自动接受所有 Host Key”。
- MCP Client 不能替本地用户接受 Host Key。
- Headless 或 UI 不可用时，未知或变化的 Host Key 连接失败。
- known_hosts 存储与连接配置分离。
- 审计记录 Host Key 决策，但不记录私钥或密码。

## 17. 日志和审计

诊断日志与安全审计分离。

### 17.1 诊断日志

记录：

- 服务启动和停止
- 协议错误
- timeout 和 cancellation
- 子进程退出码
- 网络错误类别
- correlation ID

不记录：

- 密码、Token 和私钥
- 完整 Authorization Header
- Secret 环境变量值
- 默认情况下的完整远程文件内容

### 17.2 安全审计

计划模型：

```csharp
public sealed record AuditEvent
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string CallerId { get; init; }
    public required string ToolName { get; init; }
    public required string TargetId { get; init; }
    public required string Decision { get; init; }
    public required string Outcome { get; init; }
    public string? OperationId { get; init; }
    public string? CorrelationId { get; init; }
}
```

审计状态至少覆盖：

- Received
- PolicyDenied
- PendingApproval
- UserDeclined
- UserApproved
- Started
- Completed
- Failed
- Cancelled
- ResultUnknown

命令审计可以保存经过明确脱敏的命令摘要或哈希；完整命令是否持久化需要单独设置和风险提示。

## 18. 安全失败策略

以下情况默认拒绝：

- 无法识别 Tool
- Schema 校验失败
- 调用方身份或 transport 不满足策略
- Connection/Profile 未发布
- 路径不在允许 root
- Host Key 未确认或发生变化
- Approval UI 不可用
- Grant 过期、重复消费或参数不匹配
- `operationId` 冲突
- 输出、输入或并发超过限制
- 应用正在关闭
- 无法保证写操作不会被重复执行

不能为了“保持 MCP Client 体验”自动放宽权限。

## 19. 必需安全测试

### Policy 和 Approval

- 所有 `ssh.exec` 均进入审批。
- 所有写操作均进入审批。
- `AllowCommandWithApproval=false` 时直接拒绝。
- `AllowWriteWithApproval=false` 时直接拒绝。
- Escape、关闭窗口和 backdrop 都不能批准。
- UI 不可用时拒绝。
- 请求取消会使审批和 Grant 失效。
- 参数变化后旧 Grant 不可用。
- Grant 不能消费两次。

### Path

- `..` 遍历
- 重复 URL decode
- Unicode 路径
- 大小写差异
- 符号链接逃逸
- 特殊文件
- 超长路径
- TOCTOU 场景

### stdio

- stdout 垃圾内容
- 超长 JSON 消息
- stderr 无限输出
- 进程和孙进程不退出
- 部分 JSON 后崩溃
- stdin 关闭后不退出
- command/arguments 变化触发重新信任

### HTTP

- 非 HTTPS 远程地址
- 非法 Origin
- DNS rebinding
- metadata SSRF
- 重定向到内网或 link-local
- 超大请求和响应
- 无效、过期或错误 audience Token
- timeout 和 cancellation

### 防重放

- 相同 operationId 相同参数不重复执行
- 相同 operationId 不同参数被拒绝
- 执行中断返回 ResultUnknown
- Client 自动重试不会造成二次写入

## 20. 后续评审

以下能力启用前必须重新进行安全评审：

- 公网或局域网 MCP Server
- 无 UI/headless 模式
- 可记忆的命令批准规则
- 端口转发和代理工具
- MCP 控制 VNC/RDP
- MCP Server 配置管理工具
- 任意二进制文件读取或上传
- 多用户共享 Broker
