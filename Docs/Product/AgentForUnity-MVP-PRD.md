# Agent for Unity - MVP 产品需求文档

## 1. 文档信息

| 项目 | 内容 |
| --- | --- |
| 文档状态 | Implemented M1 baseline; M2 quality work in progress |
| 更新日期 | 2026-09-09 |
| 产品名称 | Agent for Unity |
| 产品形态 | Unity Editor-only UPM Package |
| 首个支持版本 | Unity 2022.3 LTS |
| 首发平台 | macOS；Windows 在 MVP 后补齐验证 |
| Agent 内核 | 本地 `codex app-server` |
| 当前验证环境 | Unity 2022.3.62f2c1、Codex CLI 0.144.6 |

## 2. 产品定义

Agent for Unity 是一个运行在 Unity Editor 内的 Codex 富客户端。开发者无需打开独立 Codex App，即可在当前 Unity 工程中发起开发任务、附加 Unity 上下文、查看 Agent 执行过程、审批命令与文件修改，并在 Unity 编译后把结果反馈给同一会话。

产品不自行实现大模型对话和 Agent 循环。Unity Package 负责客户端体验及 Unity 集成；Codex App Server 负责认证、会话、模型选择、Agent 执行、文件修改、命令调用和事件流。

官方参考：

- [Codex App Server](https://developers.openai.com/codex/app-server)
- [Codex Authentication](https://developers.openai.com/codex/auth)

## 3. 背景与问题

Unity 开发同时涉及代码、Console、Scene、Prefab、资源导入、编译和 Play Mode。通用 IDE Agent 能修改代码，但通常不知道 Unity Editor 当前选择、编译状态和资源上下文；独立 AI App 又会打断 Unity 内的工作流。

本产品解决以下问题：

1. 开发者需要在 Unity 和独立 AI 客户端之间频繁切换。
2. Agent 缺少当前 Scene、Selection、Console 和 Unity 配置等上下文。
3. Agent 修改脚本后，Unity 编译结果无法自动回到原会话。
4. 命令、文件修改和资源操作缺少符合 Unity 工作方式的审批界面。
5. Unity Domain Reload 可能中断客户端状态和正在显示的任务。

## 4. 产品目标

### 4.1 MVP 目标

MVP 必须完成以下闭环：

```text
打开 Unity 面板
  -> 检测 Codex 与登录状态
  -> 新建或恢复会话
  -> 附加 Unity 上下文并提出任务
  -> 流式查看 Agent 工作
  -> 审批命令或文件修改
  -> Unity 导入并编译
  -> 收集 Console 结果
  -> 在原会话继续修复
  -> 查看本轮最终 Diff
```

### 4.2 成功指标

MVP 内测阶段使用以下指标判断是否可用：

- 首次安装后，80% 以上开发者可在 5 分钟内完成连接和登录。
- 普通脚本修改任务可完全在 Unity 窗口内完成。
- Domain Reload 后，会话恢复成功率达到 95% 以上。
- 文件修改和命令执行不得绕过用户配置的审批策略。
- Agent 造成编译错误时，开发者可以在两次操作内将错误继续发送给原会话。
- Unity 主线程不得因 App Server 输出读取而出现可感知卡顿。

## 5. 非目标

以下内容不属于 MVP：

- 完整复制 Codex Desktop 的全部功能。
- Codex 云任务、跨设备接力和远程工作区。
- 多 Agent 并行调度。
- 自动创建或大规模修改 Scene、Prefab、Material、Animator 等序列化资源。
- 自动进入 Play Mode、自动保存 Scene 或自动切换 Build Target。
- 在 Unity 工程或版本库中保存 API Key、OAuth Token。
- 内置 Git 客户端或取代现有版本控制工具。
- 在最终 Player 包中运行 Agent。

## 6. 用户角色

### 6.1 主要用户

Unity 程序开发者：希望在 Unity 中完成脚本实现、错误修复、代码理解和测试。

### 6.2 次要用户

- 技术策划：希望分析配置、定位引用和生成小型 Editor 工具。
- TA/UI 开发：希望结合选中资源和 Console 描述问题。
- 项目负责人：希望对 Agent 操作保留明确审批和审计记录。

## 7. 核心用户故事

### US-01 首次连接

作为开发者，我希望打开 Agent for Unity 后立即看到 Codex CLI、版本、登录和工程路径状态，从而知道当前环境是否可用。

验收标准：

- 能显示 Codex 可执行文件路径和版本。
- 未找到 CLI 时提供明确安装说明和重新检测按钮。
- 未登录时提供 ChatGPT 登录与 API Key 登录入口。
- 登录凭证由 Codex 管理，Unity 仅展示状态。
- 连接失败时展示可复制的诊断信息。

### US-02 发起开发任务

作为开发者，我希望在 Unity 中输入需求并看到 Agent 持续输出，而不是等待一个无状态的最终回答。

验收标准：

- 可以新建会话并发送文本消息。
- 支持流式显示 Agent 消息、计划、工具状态和命令输出。
- 任务运行中可以停止。
- 任务运行中可以追加纠正信息；不支持时明确降级为下一轮消息。
- 界面清晰区分“正在工作”“等待审批”“完成”“失败”“已取消”。

### US-03 附加 Unity 上下文

作为开发者，我希望把当前选择、Console 或文件附加给 Agent，而不需要手工复制大量内容。

验收标准：

- 支持附加当前选中对象摘要。
- 支持附加选中脚本或资源路径。
- 支持附加 Console Error/Warning 及堆栈。
- 支持附加项目环境摘要：Unity 版本、Build Target、关键 Packages。
- 发送前能预览并移除任意上下文项。
- 每项上下文显示来源、大小和采集时间。

### US-04 审批命令执行

作为开发者，我希望在 Agent 执行本地命令前看到命令、目录和风险信息，并决定是否允许。

验收标准：

- 展示命令、工作目录、Agent 给出的原因和网络访问提示。
- 支持允许一次、会话内允许、拒绝和取消。
- 未获得决定时，界面持续显示等待审批状态。
- 审批响应与对应的 thread、turn、item 严格关联。
- 过期审批不能错误应用到后续请求。

### US-05 审批文件修改

作为开发者，我希望在文件写入前查看拟修改文件和 Diff，再决定是否允许。

验收标准：

- 展示变更文件列表和文本 Diff。
- 工程外文件使用更高风险样式提示。
- 支持允许一次、会话内允许、拒绝和取消。
- 修改完成后展示最终状态：completed、failed 或 declined。
- Unity 重新导入期间保持界面状态可恢复。

### US-06 编译结果回传

作为开发者，我希望脚本修改后自动看到 Unity 编译结果，并可把错误继续交给同一 Agent 修复。

验收标准：

- 监控 Unity 编译开始与结束。
- 编译结束后生成本轮 Console 摘要。
- 无错误时显示“Unity 编译通过”，但不声称 Play Mode 已验证。
- 有错误时提供“继续修复”按钮，将错误发送到原 thread。
- 可选“自动继续修复”，默认关闭，并限制最大连续次数。

### US-07 Domain Reload 恢复

作为开发者，我希望脚本编译触发 Domain Reload 后，窗口仍能恢复到原会话。

验收标准：

- Reload 前持久化当前 threadId、turnId、窗口选择和待恢复标记。
- Reload 后重新连接 App Server，并通过 thread read/resume 恢复。
- 不重复提交上一条消息，不重复执行工具。
- 无法恢复时保留诊断信息，并允许用户手动重试。
- 不残留失控的 Codex 子进程。

### US-08 查看历史会话

作为开发者，我希望只查看当前 Unity 工程相关的历史会话。

验收标准：

- 默认使用工程绝对路径过滤 thread 列表。
- 支持新建、恢复和归档会话。
- 支持分页或渐进加载，避免打开窗口时加载全部历史。
- MVP 不提供永久删除入口。

## 8. 窗口信息架构

MVP 使用一个可停靠的 EditorWindow。

```text
┌────────────────────────────────────────────────────────────────────┐
│ Agent for Unity  [Connected]  Project: AgentForUnity               │
├──────────────┬───────────────────────────────────┬─────────────────┤
│ Sessions     │ Model / Reasoning / Sandbox       │ Activity / Diff │
│              ├───────────────────────────────────┤                 │
│ + New        │ User message                      │ Command         │
│ Today        │ Agent streamed response           │ File changes    │
│ Yesterday    │ Tool and compile status cards     │ Unity compile   │
│ Archived     │ Approval cards                    │ Diagnostics     │
│              │                                   │                 │
│              ├───────────────────────────────────┤                 │
│              │ Context chips                     │                 │
│              │ [Selection] [Console] [File.cs]   │                 │
│              │ Prompt...            [Stop/Send]  │                 │
└──────────────┴───────────────────────────────────┴─────────────────┘
```

窄窗口下，Sessions 和 Activity/DIff 以可折叠面板呈现，确保输入框和会话内容始终可用。

## 9. 功能需求

### FR-01 App Server 生命周期

- Unity 启动本地 `codex app-server --listen stdio://` 子进程。
- App Server 的 stdin、stdout、stderr 必须异步读取。
- 每个 Unity 工程默认使用一个 App Server 进程和多个 thread。
- Unity 退出、程序集重载或 Package 禁用时，应有序停止或解除连接。
- 异常退出后允许指数退避重启，连续失败后停止自动重试。
- 必须检测并清理本插件产生的孤儿进程，但不能结束其他 Codex 客户端进程。

### FR-02 协议层

- 使用 stdio newline-delimited JSON。
- 建立连接后必须先发送 `initialize`，成功后发送 `initialized`。
- 请求 ID 在单连接内唯一。
- 请求、响应、通知和 server-initiated request 分开建模。
- 未识别通知写入诊断日志，但不得导致连接崩溃。
- 协议 DTO 与业务 UI 解耦，便于适配不同 Codex CLI 版本。
- MVP 默认不启用 `experimentalApi`；需要实验接口的功能必须单独标注。

### FR-03 认证

- 通过 `account/read` 获取状态。
- 通过 `account/login/start` 发起 ChatGPT、Device Code 或 API Key 登录。
- 处理 `account/login/completed` 和 `account/updated`。
- 提供退出登录入口，但必须二次确认。
- API Key 输入框使用密码样式，不写入 Unity Assets、EditorPrefs 或日志。
- 可显示 `account/rateLimits/read` 返回的可用信息；缺失值显示未知，不显示为零。

### FR-04 模型选择

- 通过 `model/list` 动态获取模型。
- 只显示当前账号可见且未隐藏的模型。
- reasoning effort 选项来自模型返回的能力，不硬编码。
- 默认使用 App Server 标记的推荐默认模型。
- 模型列表获取失败时，允许沿用 thread 已记录的模型，但不提供猜测列表。

### FR-05 会话管理

- 使用 `thread/start` 创建会话，并固定 `cwd` 为 Unity 工程根目录。
- 使用 `thread/list` 按 `cwd` 获取历史会话。
- 使用 `thread/read` 读取摘要或历史。
- 使用 `thread/resume` 恢复工作会话。
- 使用 `thread/archive` 归档。
- MVP 不使用实验性的 turns/items 分页接口作为关键依赖。

### FR-06 Turn 管理

- 使用 `turn/start` 提交用户输入和上下文。
- 处理 `turn/started`、`turn/completed` 和 error。
- 使用 `turn/interrupt` 停止当前任务。
- 能力可用时使用 `turn/steer` 追加运行中指令。
- UI 不把“已发送停止请求”错误展示成“已停止”，必须等待最终状态。

### FR-07 事件流渲染

至少支持以下事件：

| App Server 事件 | Unity 表现 |
| --- | --- |
| `item/started` | 创建对应活动卡片 |
| `item/completed` | 完成活动卡片并显示结果 |
| `item/agentMessage/delta` | 追加 Agent 消息文本 |
| `item/plan/delta` | 更新计划区域 |
| `item/reasoning/summaryTextDelta` | 更新可读思考摘要 |
| `item/commandExecution/outputDelta` | 追加 stdout/stderr |
| `turn/diff/updated` | 更新 Diff 面板 |
| `thread/status/changed` | 更新会话状态 |
| `error` | 展示分类后的错误和重试建议 |

原始 reasoning 文本不是 MVP 必须展示的内容；界面优先展示可读摘要、工具动作和结果。

### FR-08 Unity 上下文构建

每个上下文提供者实现统一概念接口：是否可用、采集、预览、估算大小、失效检测。

MVP 提供：

| 上下文 | 内容 | 默认行为 |
| --- | --- | --- |
| Project | Unity 版本、工程路径、Build Target、关键包 | 新 thread 首次自动附加 |
| Selection | 对象路径、类型、组件、资源 GUID/路径 | 用户手动添加 |
| Console | 用户选择的日志与堆栈 | 用户手动添加 |
| File | 文件路径及必要内容 | 用户手动添加 |
| Scene | 当前场景名、路径、是否 dirty | 仅摘要 |
| Git Diff | 可用时附加目标文件 Diff | 用户手动添加 |

上下文规则：

- 不默认发送完整 Scene YAML、Prefab YAML 或整个 Console。
- 所有路径标准化为工程根目录下的相对路径，协议要求绝对路径时再转换。
- 采集时隐藏疑似 Token、Key 和密码值。
- 对相同内容做哈希去重。
- 超过大小预算时必须让用户选择精简，不静默截断关键堆栈。

### FR-09 Unity 编译闭环

- 记录引发 Asset 导入或编译的 turnId。
- 通过 Unity 编译事件观察结果，不轮询阻塞主线程。
- 编译开始后显示独立状态卡片。
- 编译结束后按 Error、Warning、Info 分类汇总。
- “继续修复”创建同一 thread 的新 turn，并附加错误、相关文件和前一轮摘要。
- 自动继续模式最多连续 3 次，可由用户取消。
- 编译通过仅代表静态编译通过；Play Mode 与视觉表现必须单独验证。

### FR-10 Diff 与导航

- 展示当前 turn 修改的文件列表。
- 文本文件展示 unified 或 split Diff。
- 点击路径可在 Unity/外部脚本编辑器中定位。
- `.unity`、`.prefab`、`.asset` 在 MVP 中只展示文件变化警告和原始 Diff，不承诺语义化 Diff。
- 没有 Git 仓库时仍应展示 App Server 提供的 turn diff。

### FR-11 设置

项目级设置：

- 默认 sandbox/approval policy。
- 默认自动附加的上下文。
- 自动编译反馈开关。
- 自动继续修复开关和次数上限。

用户级设置：

- Codex 可执行文件覆盖路径。
- 默认模型与 reasoning effort。
- 诊断日志级别。
- 窗口显示偏好。

设置不得包含凭证。项目级设置是否纳入版本控制必须由用户明确选择。

## 10. App Server 消息映射

| 产品动作 | App Server 方法/事件 | MVP |
| --- | --- | --- |
| 建立连接 | `initialize` -> `initialized` | 必须 |
| 查询账号 | `account/read` | 必须 |
| 登录 | `account/login/start` | 必须 |
| 登录完成 | `account/login/completed`、`account/updated` | 必须 |
| 查询限额 | `account/rateLimits/read` | 应有 |
| 模型列表 | `model/list` | 必须 |
| 会话列表 | `thread/list` | 必须 |
| 新建会话 | `thread/start` | 必须 |
| 读取会话 | `thread/read` | 必须 |
| 恢复会话 | `thread/resume` | 必须 |
| 归档会话 | `thread/archive` | 应有 |
| 发起任务 | `turn/start` | 必须 |
| 追加指令 | `turn/steer` | 应有 |
| 停止任务 | `turn/interrupt` | 必须 |
| 命令审批 | `item/commandExecution/requestApproval` | 必须 |
| 文件审批 | `item/fileChange/requestApproval` | 必须 |
| 用户输入请求 | `item/tool/requestUserInput` | 必须 |
| Diff 更新 | `turn/diff/updated` | 必须 |
| Codex Review | `review/start` | MVP 后 |

## 11. 审批矩阵

| 操作 | 默认策略 | Unity UI 要求 |
| --- | --- | --- |
| 读取工程内文件 | 自动允许 | 活动记录中可见 |
| 搜索工程内容 | 自动允许 | 显示范围和结果摘要 |
| 修改工程内文本文件 | 每次审批 | 文件列表和 Diff |
| 修改工程外文件 | 强制每次审批 | 高风险提示和绝对路径 |
| 执行只读命令 | 按 Codex 策略 | 命令、cwd、原因 |
| 执行写入命令 | 每次审批 | 高亮副作用 |
| 网络访问 | 每个目标审批 | 主机、协议、端口 |
| 删除文件 | 强制每次审批 | 不提供默认确认焦点 |
| 安装或更新依赖 | 强制每次审批 | 展示包和命令 |
| 进入 Play Mode | MVP 不开放自动调用 | 用户手动操作 |
| 保存 Scene/Prefab | MVP 不开放自动调用 | 用户手动操作 |

审批卡片不得使用倒计时自动同意。会话级授权只对 App Server 返回的明确可持久化决策生效。

## 12. Unity 工具规划

MVP 先通过 Context Provider 将 Unity 状态附加到 turn，不要求 Agent 主动调用 Unity Editor API。

### 12.1 MVP 上下文能力

- 获取 Unity 版本和工程根目录。
- 获取当前 Build Target。
- 获取当前 Scene 摘要和 dirty 状态。
- 获取当前 Selection 摘要。
- 获取 Console 日志。
- 获取编译状态和最后一次编译结果。
- 定位脚本、资源和 GUID。

### 12.2 MVP 后的只读工具

- `unity.get_project_info`
- `unity.get_console_logs`
- `unity.get_selection`
- `unity.inspect_hierarchy`
- `unity.find_assets`
- `unity.inspect_asset`
- `unity.inspect_prefab`
- `unity.get_playmode_state`
- `unity.list_tests`

### 12.3 后续可写工具

- `unity.create_game_object`
- `unity.add_component`
- `unity.set_serialized_property`
- `unity.create_scriptable_object`
- `unity.modify_prefab`
- `unity.run_tests`
- `unity.enter_playmode`
- `unity.capture_game_view`

可写工具必须统一经过主线程调度、Undo、dirty 状态检查、审批和审计。Unity MCP Bridge 的具体传输方案在 MVP 技术验证完成后单独立项。

## 13. 数据与持久化

### 13.1 Codex 拥有的数据

- 认证凭证。
- thread/turn 历史。
- 模型和账号能力。
- Codex 自身配置。

### 13.2 Unity Package 拥有的数据

- 当前工程对应的 threadId。
- 当前窗口选择状态。
- 待恢复 turnId。
- 上下文标签草稿。
- UI 偏好和诊断日志。

### 13.3 存储原则

- 临时状态存放在 `Library/AgentForUnity/` 或 `SessionState`。
- 用户偏好存放在 Unity 用户级设置中。
- 不把会话正文复制进 Assets。
- 不把凭证写入工程、日志或崩溃报告。
- 所有持久化结构带 schemaVersion。

## 14. 状态模型

### 14.1 连接状态

```text
NotChecked -> CliMissing
           -> SignedOut
           -> Connecting
           -> Ready
           -> Recovering
           -> Faulted
```

### 14.2 Turn 状态

```text
Draft -> Starting -> Running -> WaitingForApproval
                           \-> WaitingForUserInput
                           \-> Interrupting
                           \-> Completed
                           \-> Failed
                           \-> Interrupted
```

状态只能由 App Server 响应、通知或明确的 Unity 生命周期事件推进，UI 点击本身不能直接伪造完成状态。

## 15. Domain Reload 策略

Unity 脚本修改会触发程序集重载，这是本产品的核心可靠性场景。

Reload 前：

1. 停止向 UI 派发新事件。
2. 保存 threadId、turnId、最后处理事件标识和窗口状态。
3. 关闭 stdin/stdout 读取任务。
4. 请求结束本插件启动的 App Server 子进程；超时后仅终止该 PID。

Reload 后：

1. 重新定位 Codex CLI 并启动 App Server。
2. 重新执行 initialize/account/read。
3. 使用 thread/read 确认历史状态。
4. 必要时 thread/resume。
5. 对比 turn 最终状态，恢复 UI，不重放请求。
6. 收集 Unity 编译结果并提示继续。

## 16. 错误处理与诊断

必须分类处理：

- CLI 未安装或版本不兼容。
- App Server 无法启动或意外退出。
- JSON 解析错误和未知协议字段。
- 未登录、Token 过期或账号无权限。
- 用量限制。
- 网络连接失败。
- Sandbox 拒绝。
- 命令或文件修改失败。
- Context window 超限。
- Unity 正在编译、退出或 Domain Reload。

诊断包包含：

- Unity 和插件版本。
- Codex CLI 路径与版本。
- 最近协议事件名称、请求 ID 和状态。
- App Server stderr。
- Unity Console 中与本插件相关的日志。

诊断包必须清理消息正文、文件内容、环境变量和凭证。

## 17. 非功能需求

### 性能

- App Server I/O 不得阻塞 Unity 主线程。
- 单帧 UI 事件处理设置上限，剩余事件下一帧继续。
- 长命令输出使用虚拟化列表和长度限制。
- Console 上下文按需采集，不持续复制全部日志。

### 兼容性

- MVP 基线为 Unity 2022.3 LTS。
- Editor-only assembly，不进入 Player 编译。
- macOS 首发；Windows 使用相同协议层，只替换进程和路径适配。
- Codex CLI 版本不满足兼容范围时阻止启动，并给出升级或降级建议。

### 安全与隐私

- 工作目录默认限制为当前 Unity 工程。
- 日志默认不记录完整 prompt 和文件内容。
- 所有审批均展示真实目标，不使用笼统的“允许 Agent”按钮。
- 网络、工程外写入和删除操作不得被低风险策略覆盖。

## 18. 推荐程序集边界

```text
AgentForUnity.Editor
  UI
  Application
  Unity Context
  Settings

AgentForUnity.Codex
  Process lifecycle
  JSON-RPC protocol
  DTOs
  Session/Auth clients

AgentForUnity.Editor.Tests
  Protocol parser
  State machines
  Context builders
  Approval routing
  Domain reload recovery
```

如果 MVP 希望降低程序集数量，可先把前两个合并为 `AgentForUnity.Editor`，但命名空间和目录边界仍按上述结构保持，以便后续拆分。

## 19. 测试与验收

### 19.1 EditMode 测试

- JSON-RPC 请求 ID 和响应匹配。
- 分段 stdout/JSONL 解析。
- 未知事件兼容。
- Agent 文本和命令输出增量拼接。
- 审批与 thread/turn/item 关联。
- Context 去重、脱敏和大小预算。
- 连接与 Turn 状态机。
- Reload 状态序列化和恢复决策。

### 19.2 集成测试

- 启动真实 App Server 并 initialize。
- 登录状态读取。
- 新建 thread，发送 turn，收到完成事件。
- 中断正在运行的 turn。
- 模拟命令审批和文件审批。
- App Server 异常退出后的恢复。

真实账号集成测试必须手动或在受控环境运行，不进入默认 CI。

### 19.3 Unity 手动验证

- 窗口停靠、缩放和重开。
- 修改脚本触发导入、编译和 Domain Reload。
- 编译错误回传和继续修复。
- Unity 退出时无孤儿进程。
- 没有 Git 仓库时仍能完成基础工作流。
- 大量 Console 输出时 Editor 保持响应。

## 20. 里程碑

### M0 协议可行性验证

- App Server 生命周期。
- initialize、account/read、model/list。
- thread/start、turn/start。
- 流式 Agent 文本。
- turn/interrupt。

完成标准：Unity 内可以稳定完成一个只读问答 turn，重开窗口后能恢复 thread。

### M1 MVP 核心闭环

- 完整对话窗口。
- Unity 上下文标签。
- 命令与文件审批。
- Diff 面板。
- Unity 编译结果卡片。
- Domain Reload 恢复。
- EditMode 测试。

完成标准：在 Unity 内完成一次真实 C# 修改、审批、编译、错误修复和最终 Diff 查看。

### M2 内测质量

- 安装与连接向导。
- 错误分类与诊断包。
- macOS 稳定性验证。
- Windows 兼容验证。
- 文档、Samples 和升级说明。

完成标准：新用户可根据引导独立完成安装和首个任务。

当前进展：M1 基线已随 `0.2.0` 发布，M2 可用性与诊断质量工作已随 `0.3.0` 发布，包括会话列表与切换、Markdown 消息渲染、按消息归属的活动展示、CLI 多路径探测，以及完成 turn 后的脚本编译请求。这些能力不改变 M1 的权限和工作区边界。

编译策略：Agent 在运行中的 turn 不应自行触发 Unity 编译或声称完成编译验证。对于带有文件 Diff 的已完成 turn，插件随后请求 Unity 脚本编译并记录 Unity 返回的状态；结果只代表编译反馈，Play Mode 和视觉验证仍需单独执行。

### M3 Unity 深度工具

- Unity MCP Bridge 方案。
- 只读 Scene/Prefab/Asset 工具。
- Test Runner 工具。
- Play Mode 和截图反馈。
- 经审批的序列化资源修改。

## 21. 风险与应对

| 风险 | 影响 | 应对 |
| --- | --- | --- |
| App Server/协议仍在演进 | CLI 更新导致客户端失效 | 独立协议适配层、版本探测、兼容矩阵、固定集成测试 |
| Domain Reload 中断事件流 | UI 丢失状态或重复提交 | Reload 前持久化、基于服务端 thread 状态恢复、禁止自动重放 |
| Unity 主线程被 I/O 阻塞 | Editor 卡顿 | 后台读取、主线程事件队列、单帧处理预算 |
| 文件修改触发连续编译 | Agent 流程抖动 | turn 与编译批次关联、去抖、自动修复次数上限 |
| 凭证进入工程或日志 | 安全事件 | 凭证归 Codex、统一脱敏、禁止持久化敏感字段 |
| Scene/Prefab 修改不可逆 | 用户资源损坏 | MVP 不开放；后续必须使用 Undo、审批和 dirty 检查 |
| 无 Git 仓库无法回滚 | 修改恢复困难 | 修改前审批；后续增加文本快照/补丁回滚，不伪装成 Git 回滚 |

## 22. 待确认决策

这些问题不阻塞 M0，但应在 M1 开始前确定：

1. 正式包名已确定为 `com.zlr.agentforunity`，发行主体为 ZLR。
2. UI 已确定使用 UI Toolkit；后续复杂 Diff 区域保留自定义实现空间。
3. macOS 首发后，Windows 是否必须与 MVP 同期发布。
4. 是否允许插件引导安装 Codex CLI，还是只提供检测和外部安装说明。
5. 自动继续修复是否进入默认 MVP，建议实现但默认关闭。
6. 项目级设置是否生成可提交的配置文件，还是全部保持本地。
7. Unity MCP Bridge 采用 Package 内服务还是独立 Sidecar，该决策延后到 M3。

## 23. MVP Definition of Done

只有同时满足以下条件，MVP 才算完成：

- Unity 内完成登录、模型选择、会话创建与恢复。
- Agent 消息、命令输出和文件变化能够流式呈现。
- 命令、文件和用户输入请求都有可用审批 UI。
- 当前 Selection、Console、文件和工程摘要可作为上下文发送。
- C# 修改后能够正确跟踪 Unity 编译，并把错误继续发送到原会话。
- Domain Reload 后不会重复执行任务或丢失 thread。
- 用户可以停止任务、查看 Diff 和导出脱敏诊断。
- Editor-only 程序集不会进入 Player。
- 关键协议、状态机、审批和上下文逻辑具备 EditMode 测试。
- 已完成 macOS 真实 Unity Editor 验证，并明确记录尚未验证的平台。
