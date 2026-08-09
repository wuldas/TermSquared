# TermSquared 总体架构

## 1. 文档状态

本文定义 TermSquared 的目标架构和模块边界，用于指导首期实现。除特别标记为“已有”的内容外，本文描述的是已经决定或计划中的结构，不表示功能已经完成。

## 2. 架构目标

TermSquared 需要同时处理四类复杂问题：

- 原生桌面 UI 和高频增量绘制
- SSH、SFTP、FTP、VNC、RDP 等远程协议
- MCP Client 和 MCP Server 双向集成
- 凭据、远程执行、文件写入和 AI 调用带来的安全边界

架构必须满足：

- UI、协议和安全策略可以独立测试。
- Square 可以作为源码子模块调试，但产品代码不进入 Square。
- 网络线程不能直接修改 Square Element Tree。
- MCP transport 不能直接获取 SSH/SFTP 实现对象。
- 高风险操作不能绕过统一策略和审批入口。
- 平台特定依赖不能污染跨平台核心项目。

## 3. 仓库和 Square 集成

Square 作为 Git submodule 放置于：

```text
external/Square
```

初始远端：

```text
https://github.com/wuldas/Square
```

TermSquared 通过 `ProjectReference` 引用所需 Square 项目。计划中的最小引用集合是：

```text
external/Square/src/Square/Square.csproj
external/Square/src/Square.Compiler/Square.Compiler.csproj
external/Square/src/Square.Platform.Win32/Square.Platform.Win32.csproj
external/Square/src/Square.Platform.X11/Square.Platform.X11.csproj
external/Square/src/Square.Platform.MacOS/Square.Platform.MacOS.csproj
```

约束：

- 子模块固定到审核过的 commit，不自动跟随 `main`。
- Square 修改先在 Square 仓库形成独立提交或 PR，再更新 TermSquared 的 gitlink。
- 不在 detached HEAD 的子模块中长期保存未提交补丁。
- TermSquared 维护自己的 `global.json`、`Directory.Build.props` 和包版本配置。
- 不直接整体导入 Square 的根级构建配置。
- TermSquared 解决方案只加入实际需要的 Square 项目，不加入 Square samples、tools 和全量 tests。

## 4. 逻辑分层

```text
┌─────────────────────────────────────────────────────────┐
│ TermSquared.App                                         │
│ Square UI、工作区、对话框、View State                   │
└───────────────────────┬─────────────────────────────────┘
                        │ application services
┌───────────────────────▼─────────────────────────────────┐
│ TermSquared.Core                                        │
│ 连接、会话、能力、操作和公共接口                        │
└──────────────┬───────────────────────┬──────────────────┘
               │                       │
┌──────────────▼────────────┐  ┌───────▼──────────────────┐
│ TermSquared.Security      │  │ TermSquared.Mcp          │
│ Policy、Approval、Audit   │  │ Client、Server、Broker   │
└──────────────┬────────────┘  └───────┬──────────────────┘
               │                       │ controlled request
┌──────────────▼───────────────────────▼──────────────────┐
│ Protocol Providers                                      │
│ SSH/SFTP、FTP/FTPS、VNC、RDP                            │
└─────────────────────────────────────────────────────────┘
```

依赖方向必须从外层指向内层抽象：

- `TermSquared.Core` 不依赖 Square、MCP SDK 或具体协议库。
- `TermSquared.App` 可以依赖 Core 和应用服务，但不直接操作底层 SSH Client。
- `TermSquared.Mcp` 通过 Core 定义的操作接口访问远程能力。
- `TermSquared.Security` 对 MCP 和普通 UI 操作提供同一套策略结果。
- 协议项目实现 Core 定义的 Provider 接口。

## 5. 项目职责

### 5.1 TermSquared.App

职责：

- 应用入口和 `AppWindow` 创建
- 自定义标题栏、菜单和状态栏
- 连接侧栏和搜索
- 会话标签和工作区
- Terminal、File Browser、Remote Desktop 等视图
- MCP Dashboard 和工具调用视图
- 高风险操作审批 Dialog
- 将后台状态通过 Square Dispatcher 合并更新到 UI

禁止：

- 直接保存密码、私钥口令或 Token
- 直接创建 SSH、FTP 或 MCP SDK Client
- 在 UI 线程执行网络或文件 IO
- 在 View 中实现权限规则

### 5.2 TermSquared.Core

职责：

- `ConnectionProfile`
- `ConnectionCapabilities`
- `IConnectionProvider`
- `IConnectionSession`
- `SessionDescriptor` 和生命周期状态
- `RemoteOperation` 和风险分类
- 与 UI 无关的应用接口

核心能力模型计划为：

```csharp
[Flags]
public enum ConnectionCapabilities
{
    None = 0,
    Terminal = 1,
    FileBrowser = 2,
    RemoteDesktop = 4,
    PortForwarding = 8
}
```

协议和能力映射：

| 协议 | 能力 |
|---|---|
| SSH | Terminal、PortForwarding，可附加 FileBrowser |
| SFTP | FileBrowser，复用 SSH 认证和会话 |
| FTP/FTPS | FileBrowser |
| VNC | RemoteDesktop |
| RDP | RemoteDesktop |

### 5.3 TermSquared.Security

职责：

- OS Credential Store 适配
- Secret reference 和敏感字段脱敏
- SSH Host Key 策略
- MCP 发布范围策略
- `ApprovalBroker`
- 一次性操作授权
- 结构化安全审计
- 路径、URI、目标地址和重定向安全校验

该项目不能依赖 Square Dialog。它只发布审批请求并等待结果，由 App 提供 UI 实现。

### 5.4 TermSquared.Protocols.Ssh

职责：

- SSH 客户端库适配
- 密码、私钥和 SSH Agent 认证
- known_hosts 和 Host Key 校验
- PTY 和 Shell Channel
- SFTP 目录与文件操作
- Keepalive、超时、取消和重连
- 受限的远程命令执行
- 会话复用和资源释放

终端 VT/ANSI 模型应与 SSH transport 解耦，使其未来可以服务 Telnet、串口或本地 Shell。

### 5.5 TermSquared.Protocols.Ftp

职责：

- FTP 和 FTPS
- 主动/被动模式
- TLS 证书校验
- 与通用 File Browser 和 Transfer Queue 对接

### 5.6 TermSquared.Protocols.Vnc

职责：

- RFB 协议或第三方 VNC 引擎适配
- framebuffer 增量更新
- 键盘、鼠标和剪贴板事件映射
- 缩放和远程分辨率处理

### 5.7 TermSquared.Protocols.Rdp

职责：

- Windows 原生 RDP 控件托管或 FreeRDP 适配
- 平台能力隔离
- 远程画面、输入和剪贴板桥接

RDP 的最终实现路线必须经过独立技术验证。Windows 原生控件方案可能要求 Square 新增通用 `NativeViewHost`。

### 5.8 TermSquared.Mcp

职责：

- 官方 MCP C# SDK 适配
- `stdio` MCP Client
- Streamable HTTP MCP Client
- Streamable HTTP MCP Server
- Tools、Resources、Prompts 发现和调用
- MCP Tool/Resource 注册
- OAuth 和 HTTP 身份适配
- 请求超时、取消、进度和输出上限
- 入站调用到 Policy/Approval/Remote Operation 的映射

MCP SDK 类型必须限制在该项目边界内。

### 5.9 TermSquared.Mcp.StdioBridge

这是供 Claude Desktop、OpenCode 等 AI 客户端启动的轻量 Console 程序。

职责：

- 通过 stdin/stdout 提供 MCP Server
- stdout 只输出 MCP 协议消息
- 日志只写 stderr 或文件
- 通过本地 IPC 转发请求到运行中的 TermSquared Desktop Broker
- 在 Broker 不可用时安全失败

禁止：

- 保存或读取 SSH 凭据
- 直接建立 SSH 连接
- 绕过 Desktop Broker 的权限策略
- 在本地 UI 不可用时自动批准高风险操作

## 6. 运行时进程拓扑

### 6.1 MCP stdio Server

```text
Claude Desktop / OpenCode
        │ stdio
        ▼
TermSquared.Mcp.StdioBridge
        │ Named Pipe / Unix Domain Socket
        ▼
TermSquared Desktop Broker
        ├─ MCP Registry
        ├─ Policy Engine
        ├─ Approval Broker
        ├─ Audit Sink
        └─ Session Manager
                  │
                  ▼
             SSH/SFTP Server
```

Windows 首期使用带当前用户 ACL 的 Named Pipe。Linux/macOS 后续使用仅当前用户可访问的 Unix Domain Socket。

### 6.2 MCP HTTP Server

首期只监听回环地址：

```text
AI Client ── HTTP ──> 127.0.0.1:<port>/mcp
                           │
                           ▼
                  TermSquared Desktop Broker
```

远程监听、公网暴露和无人值守模式均为后置能力。

## 7. 统一连接与会话模型

每种协议由 Provider 创建统一会话：

```csharp
public interface IConnectionProvider
{
    string Protocol { get; }
    ConnectionCapabilities Capabilities { get; }

    Task<IConnectionSession> ConnectAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken);
}
```

会话标签以稳定 `SessionId` 标识，不能使用标签索引作为身份。计划中的状态包括：

- Created
- Connecting
- Authenticating
- Connected
- Disconnecting
- Disconnected
- Failed
- Disposed

Session 负责拥有并释放：

- 网络连接
- Shell/SFTP/Remote Desktop channel
- CancellationTokenSource
- 后台读写循环
- 输出和传输缓冲区
- 与 UI 无关的运行时状态

## 8. UI 工作区

计划中的主界面：

```text
Custom Title Bar
├─ File
├─ Session
├─ View
├─ Transfer
├─ MCP
└─ Tools

Main Workspace
├─ Connection Sidebar
│  ├─ Search
│  ├─ Remote Connections
│  ├─ MCP Outbound Servers
│  └─ MCP Inbound Clients
├─ Splitter
└─ Session Workspace
   ├─ Session Tabs
   ├─ Active Session Content
   └─ Status Bar

Bottom Panel
├─ Transfer Queue
├─ Pending Approvals
├─ Audit Log
└─ Diagnostics
```

工作区内容类型：

- `TerminalWorkspace`
- `FileBrowserWorkspace`
- `RemoteDesktopWorkspace`
- `McpServerWorkspace`
- `McpInvocationWorkspace`
- `ConnectionErrorWorkspace`

## 9. 状态和线程模型

Square Element Tree 只能在 UI 线程更新。后台协议循环遵循：

```text
Network/Process Thread
    → Parse/Buffer
    → Batch/Throttle
    → Square Dispatcher
    → Store/Signal/View Update
```

要求：

- 不为每个终端字符调用一次 Dispatcher。
- 高频输出按时间片或批次提交。
- 终端 scrollback、日志和审计列表都有容量上限。
- 会话关闭时先取消后台任务，再释放 UI 绑定。
- 后台异常转换为稳定错误码和脱敏消息，不把原始异常直接展示给 MCP Client。

计划使用：

- `Store<AppState>` 管理应用级摘要状态。
- `ObservableValue<T>` 管理组件局部状态。
- `Signal<T>` 发布后台进度和通知。
- SessionManager 保存运行时会话对象，不把网络对象放进响应式状态。

## 10. 持久化边界

普通配置可以保存：

- 连接名称和分组
- Host、Port 和 Username
- 认证方式
- 私钥路径
- 凭据引用 ID
- MCP Server command、arguments、endpoint
- 已发布 MCP Profile 和允许读取的远端 roots
- UI 布局和主题设置

普通配置不得保存：

- 密码
- 私钥内容
- 私钥口令
- MCP Bearer Token
- OAuth Refresh Token
- Approval Grant

敏感数据交给操作系统安全存储，业务配置只保存 opaque reference。

## 11. 依赖和发布策略

首期目标：

- .NET 10
- Windows x64
- 普通 self-contained Release
- Software 和 Skia 后端进行终端性能对比

NativeAOT 不作为首期硬门槛。以下依赖确定后再进行 AOT/trim 验证：

- MCP C# SDK
- SSH 客户端库
- 私钥和密码算法
- FTP/VNC/RDP 库
- OS Credential Store 适配

## 12. 测试边界

### 单元测试

- Core 状态机和模型
- Policy 和风险分类
- 路径规范化和 root containment
- Approval Grant 的绑定、过期和单次消费
- MCP Tool 到 RemoteOperation 的映射
- 日志脱敏

### 集成测试

- 可控 SSH/SFTP Server
- stdio 假 MCP Server
- Streamable HTTP 假 MCP Server
- Desktop Broker 与 StdioBridge IPC
- 进程退出、超时和进程树清理
- 重复 `operationId` 防重放

### UI 测试

- 大量连接和资源节点
- 标签创建、关闭和状态恢复
- 审批 Dialog 的焦点和取消行为
- 高频终端输出
- MCP 调用进度和取消

### 互操作测试

- OpenCode
- Claude Desktop
- 官方 MCP Inspector
- 至少一个独立 stdio MCP Server
- 至少一个独立 Streamable HTTP MCP Server

内部 Client/Server 互测不能替代独立互操作测试，因为两端可能实现相同的协议错误。

## 13. 架构决策记录

重大变化应新增 ADR，至少包括：

- SSH 客户端库选型
- 终端解析和渲染方案
- MCP SDK 封装范围
- Desktop Broker IPC 方案
- OS Credential Store 方案
- RDP 实现路线
- NativeAOT 是否成为正式发布目标
- 是否以及何时允许远程 MCP HTTP 监听
