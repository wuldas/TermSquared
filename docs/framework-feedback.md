# Square 验证与反馈

## 1. 目标

TermSquared 是 Square UI 框架的真实应用验证项目。目标不是在 TermSquared 中长期维护框架补丁，而是：

1. 用 SSH、SFTP、MCP、VNC 和 RDP 的实际负载发现 Square 的能力缺口。
2. 在产品层先验证需求和 API 形态。
3. 把与 TermSquared 品牌和远程协议无关的通用能力贡献回 Square。
4. Square 合并修复后，更新 TermSquared 的 submodule commit 并执行产品回归。

Square 仓库：

```text
https://github.com/wuldas/Square
```

计划中的子模块路径：

```text
external/Square
```

## 2. 代码归属原则

判断一个能力是否应进入 Square，可以依次询问：

1. 它是否引用 SSH、SFTP、FTP、VNC、RDP 或 MCP 概念？
2. 它是否包含 TermSquared 的产品策略、菜单、主题或业务状态？
3. 它是否只能服务于远程连接管理工具？
4. 它能否独立测试和演示？
5. 它是否适合成为 UI、平台或渲染框架的稳定公共 API？

如果前三项任一为“是”，通常应留在 TermSquared。如果后两项均为“是”，可以考虑贡献 Square。

## 3. 保留在 TermSquared 的能力

- SSH/SFTP/FTP/FTPS 协议和客户端库适配
- VNC/RFB 和 RDP 协议适配
- SSH Host Key 和 known_hosts 策略
- 连接配置、会话和重连策略
- 凭据引用和操作系统安全存储策略
- MCP Client、Server、Tool 和 Resource
- stdio MCP Server 进程管理
- OAuth 和 MCP HTTP 安全策略
- Approval Broker 和远程操作审批规则
- 安全审计
- Transfer Queue 的远程协议业务逻辑
- TermSquared 菜单、主题和页面

## 4. Square 候选贡献

### 4.1 TabStrip/TabControl

TermSquared 需要：

- 动态添加和删除
- 稳定 key
- 选中状态
- 关闭按钮
- 异步关闭拦截
- Dirty、Connecting、Error 状态
- 键盘切换
- 右键菜单
- 溢出滚动
- 后续拖动重排

可贡献 Square 的条件：

- API 不含 Session 或 SSH 概念。
- 有独立 Sample。
- 有选中、关闭、溢出和键盘测试。
- 可以被编辑器、浏览器式工作台和普通设置页复用。

### 4.2 异步 VirtualTree

TermSquared 场景：

- SFTP 目录按需展开
- MCP Resources 分层加载
- 大量连接分组

候选通用能力：

- 异步 child provider
- `IsLoading`
- cancellation
- loading/error placeholder
- 展开状态恢复
- 节点刷新

协议路径、SFTP 错误和 MCP Resource URI 映射仍留在 TermSquared。

### 4.3 Task-based DialogHost

TermSquared 场景：

- Host Key 确认
- 高风险 MCP 操作审批
- 删除和覆盖确认
- 凭据输入

候选通用能力：

- 同窗口 `Task<T?>` Dialog
- 焦点陷阱和恢复
- Escape/关闭语义
- 不可 backdrop 关闭
- Dialog 队列
- cancellation

权限规则、风险展示和 Grant 仍留在 TermSquared。

### 4.4 External URI Launcher

TermSquared 场景：

- MCP OAuth 登录
- URL elicitation
- 打开文档和问题页面

候选通用接口：

```text
IExternalUriLauncher
LaunchResult
```

OAuth callback、PKCE、state 和 URL 安全验证仍留在 TermSquared。

### 4.5 NativeViewHost

TermSquared 场景：

- Windows RDP ActiveX/COM 控件
- 未来其他平台原生子视图

候选通用能力：

- 原生子窗口或控件句柄托管
- 布局和 DPI 同步
- 可见性和裁剪
- 焦点和输入转移
- 生命周期和释放

RDP 连接参数和协议控制仍留在 TermSquared。

### 4.6 高频自定义绘制

TermSquared 场景：

- Terminal cell grid
- VNC framebuffer
- 大量日志和传输进度

候选通用改进：

- 局部失效 API
- 脏区域合并
- 批量字形绘制
- 可复用行/纹理缓存
- 隐藏元素暂停绘制
- 性能计数器

终端 VT Parser 和 VNC 协议仍留在 TermSquared。

### 4.7 平台和输入改进

TermSquared 将验证：

- CJK IME
- 组合键和功能键
- 终端文本选择
- 剪贴板
- DPI 变化
- 鼠标捕获
- 自定义标题栏
- 长时间焦点切换

如果问题可以在不引用产品概念的前提下修复，应贡献 Square 平台层并补充跨平台测试。

## 5. 验证矩阵

| Square 能力 | TermSquared 场景 | 关键指标 |
|---|---|---|
| Window/TitleBar | 桌面工作台 | DPI、拖动、最大化、焦点 |
| VirtualTree | 连接、SFTP、MCP Resources | 大数据量、异步展开、取消 |
| VirtualList | 文件、审计、Tool 列表 | 滚动、更新、内存 |
| Splitter | 侧栏、终端/SFTP 分屏 | 拖动、最小宽度、持久化 |
| Dialog | Host Key 和 MCP 审批 | 模态、焦点、取消、防误批 |
| Store/Signal | 会话和后台事件 | 线程安全、批量更新、释放 |
| Dispatcher | SSH/MCP 后台线程 | 高频调度、背压、延迟 |
| Custom Paint | Terminal/VNC | 局部刷新、CPU、内存 |
| Text/Input | Terminal 和表单 | IME、宽字符、组合键 |
| Clipboard | 终端、文件和 VNC | 文本正确性、平台一致性 |
| Native Host | RDP | 布局、焦点、DPI、释放 |

## 6. 问题记录模板

每个候选问题在本文件末尾或 issue tracker 中按以下模板记录：

```text
Title:
Area:
TermSquared commit:
Square commit:
Platform/RID:
Renderer:

Observed behavior:
Expected behavior:
Reproduction steps:
Minimal reproduction:
Performance data or screenshot:

Classification:
- Product issue
- Square framework issue
- Third-party dependency issue
- Unknown

Temporary workaround:
Proposed generic API/fix:
Square issue/PR:
Square merged commit:
TermSquared gitlink update:
Regression tests:
```

禁止只记录“卡顿”“焦点不对”等无法复现的描述。性能问题至少记录：

- 数据规模
- 更新频率
- CPU/内存
- 渲染后端
- 是否可见
- 是否使用 Dispatcher 批处理

## 7. 贡献流程

### Step 1：在 TermSquared 复现

- 保留真实产品场景。
- 进一步建立最小复现。
- 确认不是协议库、产品状态或调用方式错误。

### Step 2：判断边界

- 通用 UI/平台/渲染问题进入 Square 候选。
- 产品策略和协议逻辑留在 TermSquared。
- 边界不清时，先在 TermSquared 使用内部实验 API，不急于公开 Square API。

### Step 3：在 Square 建立分支

- 子模块切换到明确分支，不在 detached HEAD 直接提交。
- 改动保持通用，不包含 TermSquared 命名或资源。
- 添加 Square 自身单元测试或 UI Sample。
- 更新 Square 文档。

### Step 4：验证 Square

- 运行受影响项目测试。
- 运行 Square 解决方案构建和测试。
- 验证 Windows、X11、macOS 的适用范围。
- 对性能修复保留可重复基准。

### Step 5：提交 Square PR

- PR 描述通用问题，不以产品私有需求作为唯一理由。
- 链接最小复现和性能数据。
- 说明 API 兼容性和平台差异。

### Step 6：更新 TermSquared

- Square 合并后更新 submodule gitlink。
- 删除 TermSquared 临时 workaround。
- 运行 TermSquared 全量回归。
- 在问题记录中写入 Square 和 TermSquared commit。

## 8. 子模块工作规则

- TermSquared 提交只记录 Square gitlink，不复制 Square 源码。
- 不在 TermSquared commit 中混入未提交的 Square 工作区状态。
- 更新 gitlink 的 PR 必须说明旧 commit、新 commit 和包含的 Square 变更。
- gitlink 更新必须运行 TermSquared 构建和相关回归。
- 如果 Square commit 引入 breaking change，TermSquared 适配与 gitlink 更新放在同一个产品变更中。
- 不自动跟踪 Square `main`。
- 发布制品记录 TermSquared commit 和 Square commit。

## 9. CI 建议

日常 TermSquared CI：

```yaml
- uses: actions/checkout@<pinned-version-or-sha>
  with:
    submodules: recursive
    fetch-depth: 1
```

执行：

- TermSquared restore/build/test
- 产品 UI 和协议测试
- submodule 状态检查

更新 Square gitlink 时额外执行：

- Square 自身 restore/build/test，或验证对应上游 commit 的 CI 已成功
- 与改动相关的 TermSquared UI/性能回归
- Windows/Linux/macOS 编译矩阵

TermSquared 解决方案不需要日常包含 Square 全量 tests，以免扩大普通开发构建面。

## 10. 当前候选反馈列表

以下只是候选项，需先通过 TermSquared 实现验证：

| 能力 | 优先级 | 触发阶段 | 当前状态 |
|---|---:|---|---|
| TabStrip/TabControl | 高 | 产品外壳 | 待验证 |
| Task-based DialogHost | 高 | MCP 审批 | 待验证 |
| 异步 VirtualTree | 高 | SFTP/MCP Resources | 待验证 |
| 高频局部重绘 | 高 | Terminal/VNC | 待验证 |
| External URI Launcher | 中 | MCP OAuth | 待验证 |
| NativeViewHost | 中 | RDP | 待验证 |
| Clipboard/IME 增强 | 按问题 | Terminal/VNC | 待验证 |
| DevTools HTTP 加固 | 中 | 自动化测试 | 待评估 |

候选列表不能被理解为 Square 已承诺的功能路线图。
