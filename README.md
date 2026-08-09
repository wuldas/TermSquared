# TermSquared

TermSquared 是一个基于 [Square](https://github.com/wuldas/Square) UI 框架开发的跨平台远程连接管理工具。项目首先服务于 SSH/SFTP 场景，同时用于在真实桌面应用中验证和改进 Square。

> 当前状态：Windows x64 MVP 已可运行，核心协议和安全链路已通过单元测试及真实互操作测试。

## 产品目标

TermSquared 统一管理以下远程连接和扩展能力：

- SSH 交互式终端
- SFTP 文件管理与传输
- FTP/FTPS 文件管理与传输
- VNC 远程桌面
- RDP 远程桌面
- MCP Client：连接并调用 `stdio` 和 Streamable HTTP MCP Server
- MCP Server：向受信任的 AI 客户端暴露受控的 SSH/SFTP 能力

当前 MVP 以 Windows x64 为首发平台，已接入 SSH/SFTP、VNC、FTP/FTPS、Windows RDP 和双向 MCP 基础设施。

## 设计原则

1. **产品与框架分离**：TermSquared 保存远程协议、权限、凭据和产品逻辑；通用 UI 能力贡献回 Square。
2. **默认最小权限**：MCP Server 只暴露用户明确发布的连接和目录，默认只读。
3. **高风险操作逐次批准**：远程命令、上传、写入、删除、重命名和权限修改必须经过本地可信 UI 批准。
4. **凭据不进入业务配置**：配置只保存凭据引用，密码、私钥口令和 Token 交给操作系统安全存储。
5. **协议与 UI 解耦**：UI 依赖产品抽象，不直接依赖 SSH、FTP、MCP SDK 或平台特定实现。
6. **可取消、可限流、可审计**：所有网络和远程操作都必须支持取消、超时、资源上限及安全审计。
7. **先验证高风险能力**：优先验证终端渲染、SSH PTY、MCP 授权和长时间运行，再扩展产品范围。

## 仓库结构

```text
TermSquared/
├─ external/
│  └─ Square/                         # Git submodule
├─ src/
│  ├─ TermSquared.App/                # Square UI 和桌面入口
│  ├─ TermSquared.Core/               # 领域模型、会话和公共接口
│  ├─ TermSquared.Security/           # 凭据、策略、审批和审计
│  ├─ TermSquared.Protocols.Ssh/      # SSH/SFTP
│  ├─ TermSquared.Protocols.Ftp/      # FTP/FTPS
│  ├─ TermSquared.Protocols.Vnc/      # VNC/RFB
│  ├─ TermSquared.Protocols.Rdp/      # RDP 和平台适配
│  ├─ TermSquared.Mcp/                # MCP Client、Server 和 Broker
│  └─ TermSquared.Mcp.StdioBridge/    # AI 客户端启动的 stdio Bridge
├─ tests/
├─ docs/
├─ TermSquared.slnx
└─ README.md
```

Square 以 Git submodule 的方式固定到审核过的提交：

```text
external/Square -> https://github.com/wuldas/Square
```

TermSquared 不依赖开发机上 `E:\RiderProjects\Square` 的绝对路径。

## 文档

- [总体架构](docs/architecture.md)
- [MCP 安全模型](docs/mcp-security.md)
- [协议范围](docs/protocol-support.md)
- [开发路线图](docs/roadmap.md)
- [Square 验证与反馈](docs/framework-feedback.md)

## 已实现

- WindTerm 风格高密度工作台：菜单、连接资源、会话标签、终端、文件面板、发送面板和状态栏
- Square Git submodule 和 `Square.Extensions.Terminal`
- SSH 密码认证、严格 Host Key 验证、PTY、交互式终端、UTF-8 输入输出和 SFTP 根目录浏览
- SFTP list/stat/read/upload/download/mkdir/rename/remove 协议 API
- VNC RFB 3.8、None security、32bpp true-color 和 Raw framebuffer update
- FTP/Explicit FTPS Provider，FTPS 默认严格证书验证
- Windows RDP `.rdp` 安全配置和 `mstsc.exe` 启动，不传递密码
- MCP `stdio`/Streamable HTTP Client factory
- MCP stdio Bridge、仅回环地址的 HTTP Server 和 CurrentUserOnly Named Pipe Desktop Broker
- MCP 显式发布连接、只读远程 root、路径 containment、请求限长、Bearer/Origin 检查和脱敏 DTO
- DPAPI 用户作用域 Secret Store、JSON known-hosts、一次性授权模型、防重放和审计模型
- 43 个普通测试；真实 SSH/SFTP/PTY/VNC 测试按环境变量启用

## 后续范围

- SSH 私钥和 Agent 认证
- 多会话真正独立的运行时标签和分屏
- 完整 VNC framebuffer UI、输入和剪贴板
- FTP/FTPS 桌面配置及文件面板接线
- MCP 高风险命令和写操作的本地审批队列及 Dialog 执行器
- Windows Credential Manager、macOS Keychain 和 Linux Secret Service
- Linux/X11、macOS 和 NativeAOT 发布验收

首期不包括：

- 公网 MCP Server
- 无人值守的任意远程命令执行
- 通过 MCP 控制 RDP/VNC
- 自动接受未知或变化的 SSH Host Key
- 明文保存密码、私钥口令或 MCP Token
- 以 NativeAOT 作为首版发布硬性要求

## 技术栈

- .NET 10
- C# 14
- Square UI Framework
- Square UI、Software Renderer 和 `Square.Extensions.Terminal`
- 官方 Model Context Protocol C# SDK
- `Microsoft.Extensions.Hosting`、Dependency Injection 和 Logging
- SSH.NET 2025.1.0
- FluentFTP 54.2.0
- Windows `mstsc.exe` RDP launcher

## 构建

```powershell
git clone --recurse-submodules https://github.com/wuldas/TermSquared.git
cd TermSquared
dotnet restore TermSquared.slnx
dotnet build TermSquared.slnx --configuration Release --no-restore
dotnet test TermSquared.slnx --configuration Release --no-build
dotnet run --project src/TermSquared.App/TermSquared.App.csproj --configuration Release
```

真实 SSH/VNC 测试见 [Testing](docs/testing.md)。

## 开发状态约定

文档使用以下词语区分事实和计划：

- **已有**：已经在仓库中实现并通过基本验证。
- **决定**：架构方向已经确定，但可能尚未实现。
- **计划**：拟实现的能力，仍可能根据技术验证调整。
- **后置**：不属于当前里程碑，不能作为现有能力对外宣传。
