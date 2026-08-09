# 协议支持范围

## 1. 文档目的

本文定义 TermSquared 各协议的当前实现范围、后续优先级和明确非目标。实现状态以测试和本文说明为准。

当前 Windows MVP 已验证：

- SSH password authentication、Host Key、exec、PTY 和 SFTP
- VNC RFB 3.8 None security 与 Raw framebuffer
- FTP/Explicit FTPS Provider
- Windows RDP launcher
- MCP stdio/Streamable HTTP Client、stdio Bridge、loopback HTTP Server 和 Named Pipe Broker

## 2. 支持矩阵

| 能力 | 首期 | 后续 | 说明 |
|---|---:|---:|---|
| SSH 密码认证 | 是 |  | 不保存明文密码 |
| SSH 私钥认证 | 是 |  | 支持加密私钥口令引用 |
| SSH Agent | 评估 | 是 | 按平台分别验证 |
| SSH Host Key 验证 | 是 |  | 未知需确认，变化默认拒绝 |
| SSH PTY/Shell | 是 |  | 终端 MVP 核心 |
| SSH 命令执行 | 是 |  | UI 可直接执行；MCP 调用逐次审批 |
| SSH Keepalive | 是 |  | 可配置，避免无界自动重连 |
| SSH Jump Host |  | 是 | 会话和凭据链复杂，后置 |
| SSH Local Forward |  | 是 | MCP 首期不暴露 |
| SSH Remote Forward |  | 是 | MCP 首期不暴露 |
| SSH Dynamic SOCKS |  | 是 | MCP 首期不暴露 |
| SFTP 浏览 | 是 |  | 复用 SSH 会话 |
| SFTP 上传/下载 | 是 |  | 进度、取消和错误恢复 |
| SFTP 重命名/删除 | 是 |  | MCP 调用逐次审批 |
| SFTP 权限修改 | 评估 | 是 | 平台和服务器差异 |
| SFTP 双栏同步 |  | 是 | 不属于 MVP |
| FTP |  | 是 | 复用文件工作区 |
| FTPS |  | 是 | 证书验证不可跳过 |
| VNC/RFB |  | 是 | 优先验证 framebuffer 和输入 |
| RDP |  | 是 | Windows 原生托管或 FreeRDP 待验证 |
| MCP stdio Client | 是 |  | 管理本地 MCP Server 子进程 |
| MCP Streamable HTTP Client | 是 |  | 远程默认 HTTPS |
| MCP Tools/Resources/Prompts | 是 |  | 发现、查看、调用和取消 |
| MCP stdio Server | 是 |  | 通过轻量 Bridge 接入 Desktop Broker |
| MCP Loopback HTTP Server | 是 |  | 只监听回环地址 |
| MCP Remote HTTP Server |  | 评估 | 需要 OAuth、TLS 和独立安全评审 |
| MCP 只读 SSH/SFTP 工具 | 是 |  | 仅发布范围内访问 |
| MCP SSH 命令/写工具 | 是 |  | 每次本地批准 |
| MCP 控制 RDP/VNC | 否 | 否 | 当前明确非目标 |

## 3. SSH

### 首期目标

- 密码认证
- OpenSSH 私钥认证
- 私钥口令
- 严格 Host Key 校验
- PTY 请求
- 交互式 Shell
- 终端 resize
- stdout/stderr 数据流
- Keepalive
- 连接 timeout 和 cancellation
- 手动断开和重连
- 会话状态和错误分类

### 终端 MVP

终端至少需要：

- ANSI/VT 状态机
- 主屏和 alternate screen
- 光标位置和形状
- 清屏、清行、插入和删除
- 滚动区域
- 16 色、256 色和 true color
- SGR 文本属性
- Scrollback 上限
- 可见行绘制
- 文本选择和复制
- 粘贴
- PTY 行列同步
- 常见功能键和组合键
- CJK 宽字符基础支持

验收程序：

```text
bash/sh
less
top
vim
tmux（基础场景）
```

首期可以记录但不一定完全解决：

- 完整 BiDi
- 所有 emoji 序列
- 极复杂组合字符
- 全部 xterm 扩展
- 完整终端鼠标协议

### 明确非目标

- 自研 SSH 加密协议栈
- 自动接受 Host Key
- 无限制 Scrollback
- 把 CodeEditor 当作终端模型
- 在 UI 线程直接读取网络流

## 4. SFTP

SFTP 复用现有 SSH 认证和连接，不作为独立 Profile 类型重复保存密码。

### 首期目标

- 路径导航
- 上级目录
- 刷新
- 文件和目录列表
- 名称、类型、大小和修改时间
- 上传和下载
- 新建目录
- 重命名
- 删除
- 传输进度
- 取消
- 失败状态和手动重试
- MCP 发布 root 限制

### 首期 UI

```text
File Browser
├─ Toolbar
│  ├─ Back
│  ├─ Forward
│  ├─ Up
│  ├─ Refresh
│  ├─ Upload
│  └─ New Folder
├─ Path Input/Breadcrumb
├─ Virtual File List
└─ Transfer Queue
```

### 后置能力

- 双栏文件管理
- 拖放
- 目录比较
- 同步任务
- 远程搜索
- 文件预览
- 批量权限编辑
- 大文件断点续传

## 5. FTP/FTPS

FTP/FTPS 在 SFTP 文件工作区稳定后实现。

目标：

- FTP 主动和被动模式
- 显式/隐式 FTPS 的实际需求评估
- TLS 证书验证
- 目录浏览
- 上传、下载、重命名和删除
- 编码和路径差异适配
- 复用 Transfer Queue

约束：

- 不允许默认忽略无效证书。
- FTP 错误转换为统一文件操作错误模型。
- 协议特定命令不泄漏到通用 File Browser UI。

## 6. VNC

VNC 优先于 RDP 实施，因为它适合验证 Square 的自定义 framebuffer 绘制、增量刷新和输入系统。

目标：

- RFB 握手和认证适配
- framebuffer 增量矩形更新
- 鼠标移动和按键
- 键盘映射
- 画面缩放
- 全屏
- 剪贴板文本
- 断线和重连

Square 验证点：

- 脏矩形局部重绘
- 高频像素更新
- DPI 和坐标变换
- 隐藏标签暂停绘制
- 长时间运行内存稳定性

首期 VNC 不通过 MCP 暴露远程控制。

## 7. RDP

RDP 需要独立技术尖峰比较：

### 路线 A：Windows 原生 RDP 控件

优点：

- 成熟协议实现
- Windows MVP 成本较低

风险：

- 需要 COM/ActiveX 或原生子窗口托管
- 依赖 Windows
- 可能要求 Square 提供 `NativeViewHost`
- 焦点、输入、剪贴板和窗口层级需要平台适配

### 路线 B：FreeRDP 或其他跨平台引擎

优点：

- 跨平台潜力
- 可以输出 framebuffer 由 Square 绘制

风险：

- 本机依赖和发布复杂
- 输入、音频、剪贴板、通道和证书处理复杂
- NativeAOT 和 RID 发布风险高

正式实施前通过 ADR 选择路线。首期不通过 MCP 暴露 RDP 控制。

## 8. MCP Client

### stdio

首期目标：

- 配置 command、arguments、working directory 和环境变量引用
- 启动、停止和重启 Server
- stdout 协议读取
- stderr 诊断输出
- timeout 和 cancellation
- 进程退出状态
- 进程树清理
- Tools、Resources、Prompts 发现
- Tool 手工调用
- JSON 参数和结果查看

### Streamable HTTP

首期目标：

- HTTPS Endpoint
- Streamable HTTP transport
- Header 配置中的非敏感值
- Token 凭据引用
- OAuth 的技术验证和基础登录流程
- timeout、cancellation 和响应大小限制
- Tools、Resources、Prompts

### UI

```text
MCP Server Workspace
├─ Overview
├─ Tools
├─ Resources
├─ Prompts
├─ Invocations
├─ Protocol Trace
└─ Diagnostics
```

## 9. MCP Server

### 首期 transports

- `TermSquared.Mcp.StdioBridge`
- Desktop Broker 上的 loopback Streamable HTTP

### 首期只读工具

```text
connections.list
connections.get_status
sftp.list_directory
sftp.stat
sftp.read_text
sftp.hash
```

### 首期审批工具

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

详细安全要求见 [MCP 安全模型](mcp-security.md)。

## 10. 跨协议公共能力

以下能力应在产品层统一，不由各协议重复实现 UI：

- Connection Profile
- Connection Group 和 Tag
- Credential Reference
- Session Tabs
- 状态、错误和重连提示
- File Browser
- Transfer Queue
- 进度、取消和重试
- Audit Log
- Diagnostics
- 超时和限流设置

协议 Provider 负责将自己的能力映射到公共模型。

## 11. 错误模型

协议库异常不能直接泄漏到 UI 或 MCP。计划统一为：

```text
AuthenticationFailed
HostKeyUnknown
HostKeyChanged
ConnectionTimedOut
ConnectionRefused
ConnectionLost
PermissionDenied
PathNotFound
PathOutsideAllowedRoot
OperationCancelled
OperationTimedOut
ProtocolViolation
ResourceLimitExceeded
ResultUnknown
InternalError
```

每个错误包含：

- 稳定错误码
- 面向用户的脱敏消息
- 是否可以重试
- correlation ID
- 仅内部日志可见的异常信息
