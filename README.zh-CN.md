# Agent for Unity

[English](README.md)

Agent for Unity 是一个仅限 Editor 使用的 Unity 包，可将项目连接到本地 Codex 智能体。你可以在 Unity 内讨论需求、发送项目资料、查看变更并处理授权请求。

## 要求

- Unity 2022.3 LTS 或更高版本
- macOS 或 Windows
- Codex CLI `0.144.0` 或更高版本，且已登录 Codex 账号
- 正式包会按平台内置 Agent Bridge 运行时；源码检出版本在没有对应运行时时需要安装 .NET 9 SDK

当前仓库已提供 macOS Apple Silicon（`osx-arm64`）Bridge。若安装包没有当前平台的 Bridge，插件会自动回退到原版 App Server，并在窗口诊断中明确提示。回退模式下，Unity 重载脚本域可能中断正在进行的对话。

## 安装

安装本地检出版本：选择 **Window > Package Manager > Add package from disk**，然后选取本仓库的 `package.json`。

或通过 **Add package from git URL** 安装最新 `main` 分支：

```text
https://github.com/lfzl000/AgentForUnity.git#main
```

该包依赖 `com.unity.nuget.newtonsoft-json` `3.2.1`。

### 首次使用

连接前请确认 `codex --version` 为 `0.144.0` 或更高版本，并已通过 Codex CLI 登录账号。源码检出版本在没有内置 Bridge 时还需要 .NET 9 SDK。打开窗口后点击 **连接**。如果需要查询场景、对象或执行 Unity Editor 操作，请完成 **Unity 工具** 面板中的配置；纯文本和文件对话可以不配置 Unity Tooling。

Bridge 使用本机回环端口，并在 `Library/AgentForUnity/` 下保存临时状态文件。如果防火墙或安全软件拦截本机连接，需要允许该连接。

## 快速开始

1. 打开 **Window > Agent for Unity**。
2. 完成窗口中显示的 Unity 工具配置。Unity 2022.3 到 Unity 6 之前使用 Unity CLI Loop；Unity 6 也可以使用官方 Unity CLI 后端。
3. 选择模型、推理强度和权限模式。
4. 新建对话并发送需求。

窗口会流式显示智能体回复及关联活动。使用 **新建对话** 新建对话，或选择当前项目之前的对话继续。已在其他 Codex 客户端中活动的对话会以只读方式打开。

## 上下文和媒体

发送前可附加项目、所选对象、控制台日志、文件、场景或 Git 差异等资料。在已选对象、组件或控制台日志上右键，选择 **添加到 AgentForUnity**，即可加入输入框。

所选对象上下文包含一份有大小限制的 Unity 对象 Snapshot，包括稳定对象 ID、场景和层级路径、组件类型、序列化字段、Prefab 信息、Missing Script 标记和对象引用。点击上下文卡片上的预览按钮，可以查看实际发送的脱敏内容。从控制台右键添加的 Smart Context 还会在堆栈可解析时附加源码片段和匹配的场景组件 Snapshot。

Snapshot 有意设置了大小限制。当请求的字段未包含在 Snapshot 中时，Agent for Unity 可以通过已激活的 Unity Tooling，使用 Global Object ID 或项目路径按需查询。如果 Unity Tooling 未启用并连接，则无法按需查询对象、组件、Prefab 和场景状态，也无法执行 Unity Editor 操作和验证。Tooling 面板和回合结束消息都会明确提示 **请启用并连接 Unity Tooling**。文本和文件处理仍可继续。

使用 **+ 图片/录屏** 附加剪贴板图片或捕获 Game 视图。输入框获得焦点时，macOS 按 **Cmd+V**、Windows 按 **Ctrl+V** 可附加剪贴板图片。媒体附件不会进入 `Assets`，发送前可移除。

录屏：复制本地视频文件后在输入框粘贴，或选择 **+ 图片/录屏 → 剪贴板录屏**。支持 macOS/Windows 文件剪贴板，macOS 也支持剪贴板中的原始视频数据。支持 `.mp4`、`.mov`、`.m4v`、`.webm`、`.mkv`、`.avi`，每个录屏最多 512 MiB；复制多个文件时附加第一个视频。可以只发送录屏、不输入文字。点击附件卡片可使用系统默认应用播放。移除草稿只删除托管副本，不删除原文件；已发送副本保留供历史消息使用。

录屏以本地文件上下文发送路径、格式和大小。当前 App Server 输入协议没有原生视频类型，Agent 需要使用可用的媒体工具读取或抽帧，并受文件访问权限限制；不会自动上传视频帧或转写音频。

回合运行时，**发送** 会变为 **引导**，可继续补充要求；使用 **停止** 中断当前回合。

## 权限

| 模式 | 行为 |
| --- | --- |
| **请求批准** | 由你批准所请求的访问权限。 |
| **帮我批准** | 在项目沙箱内自动审查请求；默认禁用网络访问。 |
| **完全访问权限** | 提供不受限制的 Codex 访问权限。 |

当需要额外文件系统或网络权限时，窗口会显示批准卡片。你可以选择 **仅允许一次**、**本次会话允许** 或 **拒绝**。

## Git 和编译

**当前变更** 面板提供当前 Unity 项目所属仓库的 **拉取**、**推送** 和 **提交全部**。使用前请审查变更；提交不会自动推送。

使用 Agent Bridge 时，可以在当前对话进行中刷新并编译。回合记录了文件变更后，Agent for Unity 也可以请求脚本编译并展示结果。编译反馈不等同于 Play Mode 或视觉验证。没有 Agent Bridge 时，编译会等到回合结束后再进行，以免 Domain Reload 中断会话。

## Bridge 行为

检测到当前平台的内置 Bridge 时，Bridge 会在 Unity 进程外托管 Codex App Server，使对话可以跨越脚本重新编译、Domain Reload 和 Play Mode。Bridge 工作时，窗口会隐藏 Enter Play Mode 设置区域。

没有对应平台的 Bridge 时，插件会使用旧版 App Server 进程。此时 Enter Play Mode 设置区域会保留，并提示启用 Reload Domain 可能中断当前对话。

## 隐私和存储

Codex 管理身份验证和对话历史。Agent for Unity 不会将 API 密钥、OAuth 令牌或消息历史写入 `Assets`。临时状态和媒体附加文件存储在 `Library/AgentForUnity/` 下。

## 包结构

```text
Editor/
  Application/   状态、Unity 上下文、编译和视图模型
  Process/       Codex 进程生命周期和 CLI 检测
  Protocol/      JSON-RPC 和 App Server 协议处理
  UI/            UI Toolkit 窗口和 Markdown 渲染器
```
