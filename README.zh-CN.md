# Agent for Unity

[English](README.md)

Agent for Unity 是一个仅限 Editor 使用的 Unity 包，可将项目连接到本地 Codex 智能体。你可以在 Unity 内讨论需求、发送项目资料、查看变更并处理授权请求。

## 要求

- Unity 2022.3 LTS 或更高版本
- macOS 或 Windows
- Codex CLI `0.144.0` 或更高版本，且已登录 Codex 账号

## 安装

安装本地检出版本：选择 **Window > Package Manager > Add package from disk**，然后选取本仓库的 `package.json`。

或通过 **Add package from git URL** 安装最新 `main` 分支：

```text
https://github.com/lfzl000/AgentForUnity.git#main
```

该包依赖 `com.unity.nuget.newtonsoft-json` `3.2.1`。

## 快速开始

1. 打开 **Window > Agent for Unity**。
2. 完成窗口中显示的 Unity 工具配置。Unity 2022.3 到 Unity 6 之前使用 Unity CLI Loop；Unity 6 也可以使用官方 Unity CLI 后端。
3. 选择模型、推理强度和权限模式。
4. 新建对话并发送需求。

窗口会流式显示智能体回复及关联活动。使用 **新建对话** 新建对话，或选择当前项目之前的对话继续。已在其他 Codex 客户端中活动的对话会以只读方式打开。

## 上下文和媒体

发送前可附加项目、所选对象、控制台日志、文件、场景或 Git 差异等资料。在已选对象、组件或控制台日志上右键，选择 **添加到 AgentForUnity**，即可加入输入框。

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

回合记录了文件变更后，Agent for Unity 可以请求脚本编译并展示结果。编译反馈不等同于 Play Mode 或视觉验证。

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
