# 魔芋

“魔芋”是一个运行在 PotPlayer 进程之外的 AI 双语字幕工具。它负责检测正在播放的视频、提取或识别源语言字幕、调用模型整段翻译，并保存双语字幕和归档副本。

> 本仓库只包含“魔芋”自身的源代码、构建脚本和图标，不包含 PotPlayer、视频文件、用户字幕、API Key、识别模型或第三方可执行文件。

![魔芋主界面](docs/moyu.png)

## 主要功能

- 打开 PotPlayer 后自动启动后台监控，发现新视频时在工具窗口内询问是否生成字幕。
- 视频可以正常播放，识别和翻译工作在独立进程中进行，不阻塞播放器。
- 优先读取现有源语言字幕，其次尝试内嵌字幕，没有字幕时使用本地 Whisper。
- 先生成完整源语言字幕，再按场景调用兼容 OpenAI Chat Completions 格式的模型接口翻译。
- 默认模型为 `deepseek-v4-flash-vision-exp`，API 地址和模型名称可在界面中修改。
- API Key 只保存在 Windows 凭据管理器，不写入配置文件或日志。
- 使用内容指纹匹配缓存；视频改名或移动后仍能复用已完成字幕。
- 视频旁保存一份默认双语字幕；字幕库中同时保存双语、源语言和中文三个版本。
- 字幕加载交给 PotPlayer 自身，避免工具和播放器重复加载。
- 完成后只播放一次提示音，不使用 Windows 系统通知弹窗。

## 识别质量控制

- Whisper 分段识别时保留少量边界重叠，但每段清空文字上下文，降低幻觉连续扩散。
- 检测异常长时间轴、低信息文本、严重重复和时间轴重叠，并对问题区域进行有限次数的局部重识别。
- 识别结果先写入候选字幕和质量报告；严重错误未修复时不会替换正式字幕，也不会继续调用翻译接口。
- 分段结果带版本标记，模型、VAD、音频或原始结果变化时自动失效。

## 字幕保存规则

视频旁只生成：

```text
视频名.srt                 双语字幕，供 PotPlayer 自动发现
```

字幕库默认位于工具目录的 `hub`，结构如下：

```text
hub/
└─ 视频名字幕/
   ├─ 视频名-双语.srt
   ├─ 视频名-源语言.srt
   └─ 视频名-中文.srt
```

字幕库路径可以在“模型设置”中修改，保存后持久生效。

## 构建

系统要求：

- Windows 10 或 Windows 11
- .NET Framework 4.8
- PowerShell

运行：

```powershell
.\build.ps1
```

构建脚本使用 Windows 自带的 .NET Framework C# 编译器，输出 `AI-Subtitle-Worker.exe`。程序对外显示名称为“魔芋”；保留该内部文件名是为了兼容现有启动项和缓存机制。

## 本地运行依赖

以下文件不进入 Git 仓库，需要在本地准备：

```text
Tools/ffmpeg.exe
Tools/Whisper/Vulkan/whisper-cli.exe
Tools/Whisper/Models/ggml-silero-v6.2.0.bin
Whisper 主识别模型
```

首次运行后，程序会自动创建并维护以下本地目录，它们也不会提交到仓库：

```text
Config/    非敏感设置和运行状态
Cache/     内容指纹缓存、识别断点和质量报告
Queue/     临时任务
Logs/      运行日志
hub/       用户字幕库
```

API Key 通过界面的“模型设置”页写入 Windows 凭据管理器。请勿把密钥写进源码或 `settings.json`。

## 源码结构

```text
Assets/Moyu.ico             程序图标
src/MainForm.cs             主窗口与交互逻辑
src/MoyuTheme.cs            集中的界面主题和绘制逻辑
src/PotPlayerMonitor.cs     PotPlayer 监控
src/Recognition.cs          音频提取与 Whisper 识别
src/SubtitleQuality.cs      字幕质量检测和修复
src/DeepSeek.cs             模型请求
src/Pipeline.cs             完整处理流程
src/SubtitlePublisher.cs    字幕发布与归档
build.ps1                   构建脚本
```