# 开发路线图

## 1. 路线图原则

- 每个阶段都有明确退出标准。
- 未满足退出标准前，不并行扩展更多协议。
- 优先验证 Square 和第三方依赖的高风险边界。
- 安全能力与功能同步实现，不作为发布前补丁。
- 普通 Release 先于 NativeAOT。
- Windows x64 先于三平台同时发布。

## 2. Phase 0：仓库与构建基线

### 交付

- 初始化 TermSquared Git 仓库并配置远端
- 添加 `external/Square` Git submodule
- 固定 Square commit
- 创建 `TermSquared.slnx`
- 创建根级 `global.json`
- 创建根级构建和包版本配置
- 创建最小 `TermSquared.App`
- 引用 Square Core、Compiler 和 Win32 Platform
- 创建基础 CI，递归 checkout submodule

### 验收

- 全新 `git clone --recurse-submodules` 后可以 restore/build。
- 不依赖开发机上的绝对路径。
- Rider 可以同时浏览 TermSquared 和 Square 源码。
- Windows 上可以显示最小窗口并正常退出。
- CI 可以识别缺失或错误的 submodule commit。

## 3. Phase 1：假数据产品外壳

### 交付

- 自定义标题栏和菜单
- 暗色主题变量
- 连接侧栏、搜索和分组
- Session Tabs
- Terminal、File Browser、Remote Desktop 占位视图
- MCP Dashboard
- Outbound MCP Server 列表
- Inbound AI Client 列表
- Pending Approvals 面板
- 假 Provider 和可控状态机
- 非敏感配置持久化

### Square 验证

- 自定义标题栏
- `VirtualTree`/`VirtualList`
- `Splitter`
- `Dialog`
- 动态 Tab
- Store、Signal 和 Dispatcher

### 验收

- 无真实网络依赖即可演示完整导航。
- 可以创建、切换和关闭多种 Session Tab。
- 连接、错误、断开和待审批状态可观察。
- 大量假连接不会导致明显卡顿。
- UI 不引用具体 SSH 或 MCP SDK 类型。

## 4. Phase 2：SSH 与终端尖峰

### 交付

- SSH 客户端库 ADR
- 密码和私钥认证
- Host Key 验证及确认 UI
- PTY 和 Shell Channel
- 最小 VT/ANSI Parser
- Terminal Screen Buffer
- 自定义 `TerminalView`
- 输入、复制、粘贴和 resize
- 输出批处理和 Scrollback 上限
- Session 取消和释放
- Software/Skia 性能对比

### 验收

- 可以连接真实或容器化 SSH Server。
- `bash`、`less`、`top`、`vim` 基本可用。
- 未知 Host Key 需要确认，变化默认拒绝。
- 高频输出不冻结 UI。
- 关闭标签会终止 Shell Channel 和后台读取循环。
- 长时间输出内存保持在配置上限内。
- 普通 self-contained 发布可以运行。

### 停止条件

如果终端性能、输入或 SSH 库兼容性无法达到最低要求，应先修复或调整技术路线，不进入更多协议开发。

## 5. Phase 3：SFTP 与传输队列

### 交付

- 复用 SSH 会话的 SFTP Service
- 远程目录导航
- Virtual File List
- 上传、下载、新建目录、重命名和删除
- Transfer Queue
- 进度、取消、失败和手动重试
- 路径规范化基础
- 文本文件读取

### 验收

- 大目录浏览不阻塞 UI。
- 大文件传输可取消。
- 断线后任务状态正确。
- 不在 UI 线程执行网络或文件 IO。
- 标签关闭和应用退出不会遗留传输任务。

## 6. Phase 4：MCP Client

### 交付

- 官方 MCP C# SDK Adapter
- `stdio` Client
- Streamable HTTP Client
- MCP Server 配置和凭据引用
- Tools、Resources、Prompts 浏览
- Tool 参数表单和 JSON 模式
- 调用、取消、timeout 和结果查看
- stderr 和 Protocol Trace
- stdio 配置首次信任和变更重新确认
- HTTP Token/OAuth 基础能力

### 验收

- 能连接至少一个独立 stdio MCP Server。
- 能连接至少一个独立 Streamable HTTP MCP Server。
- Tool 调用可以取消并显示稳定错误。
- stdout 非协议内容不会污染内部状态。
- stderr 有容量和速率限制。
- Token 不出现在日志和普通配置。
- 进程关闭后没有残留子进程。

## 7. Phase 5：只读 MCP Server

### 交付

- Desktop Broker
- `TermSquared.Mcp.StdioBridge`
- 受保护的本地 IPC
- Loopback Streamable HTTP Server
- Published Profile/root 配置
- 只读 MCP Tools 和 Resources
- 路径 root containment
- 输出、并发和速率限制
- OpenCode、Claude Desktop、MCP Inspector 互操作测试

### 验收

- AI Client 只能看到显式发布的连接。
- AI Client 只能读取显式发布 root 内的文件。
- `..`、符号链接和编码绕过测试均被阻止。
- Bridge 不读取 SSH 凭据。
- Broker 不可用时安全失败。
- Loopback HTTP 未授权请求被拒绝。
- 只读操作有审计和 correlation ID。

## 8. Phase 6：审批和高风险 MCP 操作

### 交付

- Policy Engine
- Approval Broker
- 安全审批 Dialog
- 一次性 Grant
- `operationId` 和防重放
- `ssh.exec`
- SFTP 写入、上传、重命名、删除和权限修改
- ResultUnknown 状态
- 完整安全审计

### 验收

- 所有命令和写操作都进入本地审批。
- Escape、关闭和 backdrop 不能批准。
- UI 不可用时拒绝。
- 相同 operationId 不会重复执行。
- 参数变化不能复用旧批准。
- 断线后无法确认结果时返回 ResultUnknown。
- 凭据和 Token 不进入审批内容或审计。

## 9. Phase 7：FTP/FTPS

### 交付

- FTP Provider
- 主动/被动模式
- FTPS 和证书验证
- 复用 File Browser 和 Transfer Queue
- 编码和服务器路径兼容处理

### 验收

- FTP/SFTP 使用同一套文件工作区。
- 无效 TLS 证书默认拒绝。
- 传输任务可以跨协议统一显示和取消。

## 10. Phase 8：VNC

### 交付

- VNC/RFB Adapter
- `RemoteDesktopView`
- framebuffer 增量更新
- 鼠标、键盘和剪贴板
- 缩放和全屏
- 隐藏标签暂停无意义绘制

### 验收

- 脏矩形更新不会导致不必要的全窗口重绘。
- DPI 和缩放后的输入坐标准确。
- 长时间连接没有持续内存增长。
- 切换标签后后台流量和绘制符合策略。

## 11. Phase 9：RDP

### 交付

- 原生 RDP 控件与 FreeRDP 技术尖峰
- RDP ADR
- 选定路线的最小实现
- 焦点、输入、剪贴板和 resize
- 必要时向 Square 提交通用 `NativeViewHost`

### 验收

- Windows x64 可以稳定连接测试 RDP Server。
- 原生控件或 framebuffer 与 Square 窗口生命周期正确集成。
- 标签关闭后资源完整释放。
- 平台依赖不进入 Core 或其他协议项目。

## 12. Phase 10：跨平台与发布

### 交付

- Linux/X11 验证
- macOS 验证
- 各平台 Credential Store
- RID 发布矩阵
- 配置迁移
- 崩溃恢复
- 安装包或压缩包
- 第三方许可证清单
- NativeAOT 最终评估

### 验收

- 制品在目标干净环境启动。
- 每个制品可以追溯 TermSquared 和 Square commit。
- 凭据不进入制品、CI 日志或 artifact。
- 不支持的协议和平台组合在 UI 中明确标识。

## 13. 持续进行：Square 回馈

每个阶段发现的通用框架问题按 [Square 验证与反馈](framework-feedback.md) 处理。

候选回馈能力：

- `TabControl`/`TabStrip`
- 异步 `VirtualTree`
- Task-based `DialogHost`
- `IExternalUriLauncher`
- `NativeViewHost`
- 高频局部重绘优化
- 剪贴板、IME 和焦点修复

Square 修改必须先在 Square 仓库通过自身测试，再更新 TermSquared submodule commit。
