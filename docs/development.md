# 安装、开发与验证

## 本地安装

### 环境与依赖

在 Windows 10/11 可写目录使用 .NET Framework 4.8 与 Windows PowerShell 5.1 或 PowerShell 7。构建直接调用系统 Framework64 C# 编译器，无需 NuGet、Node.js 或前端打包工具。

首次默认布局假定项目目录位于播放器目录下：

~~~text
播放器目录/
  PotPlayerMini64.exe
  Model/ggml-large-v3-turbo.bin
  SubtitlePipeline/
    AI-Subtitle-Worker.exe        构建生成
    Moyu-Watcher.exe              构建生成
    Tools/
      ffmpeg.exe
      Whisper/
        Vulkan/whisper-cli.exe    连同该构建需要的配套运行库
        Models/ggml-silero-v6.2.0.bin
~~~

这只是默认布局，不是必须的安装路径。主识别模型与 GPU/CPU 后端需根据本机准备；Vulkan 构建还需兼容驱动。第三方文件不包含在仓库中，来源记录见 Tools/FFMPEG-NOTICE.txt 和 Tools/Whisper/Vulkan/source.json；这些是开发机使用记录，不是自动下载清单，也不保证未来同版本可用。

普通使用者在界面配置服务、模型、源语言和字幕库。若改变依赖布局，首次运行生成 Config/settings.json 后**完全退出程序**，再修改 FfmpegPath、WhisperPath、WhisperModelPath、WhisperVadModelPath、PotPlayerPath；路径字段目前没有图形选择器。JSON 中 Windows 反斜杠需转义，也可使用正斜杠。不要往该文件加入密钥。

源码的 Model 默认值为 deepseek-v4-flash-vision-exp，仅代表当前代码默认字符串；本版真实复核证据使用 deepseek-flash。务必填写账户实际可用的模型，连接测试通过也不等于整部媒体任务验收。思考选项使用服务端扩展参数，不能假设所有兼容接口都支持。

### 构建

首次安装、目标目录没有运行中的魔芋时，在仓库根目录：

~~~powershell
./build.ps1
~~~

输出主程序和独立检测器。程序以 exe 所在目录作为数据根目录；目录必须可写。模型和服务配置不嵌入 exe。

已有应用运行时，只构建到新的暂存目录：

~~~powershell
$out = Join-Path $env:TEMP ('MoyuBuild-' + [guid]::NewGuid().ToString('N'))
./build.ps1 -OutputDirectory $out
~~~

不要将“编译成功”当作“依赖齐全”或“真实接口可用”，也不要从暂存目录直接启动桌面版后误以为它在使用生产配置。

## 离线回归

统一入口：

~~~powershell
./tests/run-all-tests.ps1
~~~

入口先在临时目录构建主程序和检测器，执行核心/界面自检，再依次执行以下隔离测试。任一失败立即停止，输出目录与文本日志会保留用于排查。构建和回归不覆盖已安装程序，不加载生产配置，不需要真实模型密钥、媒体、播放器或识别依赖；界面测试需能创建 Windows 原生窗口。

| 脚本 | 主要覆盖 |
|---|---|
| tests/run-quality-tests.ps1 | 分阶段缓存、真实流水线调用、预算/超时/恢复、修改审计与源文疑义 |
| tests/run-translation-tests.ps1 | 本机模拟接口、思考与截断、空响应、取消和日志保护 |
| tests/run-ui-tests.ps1 | WinForms 页面、窗口行为、设置持久化、思考开关与截图 |
| tests/run-watcher-tests.ps1 | 模拟播放器进程、检测器启停/重试及主程序退出策略 |
| tests/run-update-tests.ps1 | 假程序文件与模拟进程、安装保护、哈希、回滚 |

测试有覆盖重叠，不用总和表示产品功能数。离线测试不证明真实翻译质量、显卡兼容性、复杂影片时长或所有 DPI 体验。当前没有已验证的云端 CI 运行记录；本版以本地隔离回归为准。

## 手动真实媒体评测

这些命令可能调用收费模型或长时间识别，不纳入离线测试。运行前自行确认媒体、模型、输出位置、配置和成本；不要直接反复测试超大长片。

- process-media：按 --media、--tier、--lang 处理实际媒体。
- translate-srt：对 --srt 源字幕进行基础翻译与相应后处理。
- review-srt：基于 --srt 和 --base 冻结译文，仅做复核；--out 必须与基线目录分开，--ctx 默认 8，可用 --lang 指定源语言。
- scripts/compare-recognition.ps1：手动比较识别路径；可能运行两次完整识别并调用术语接口，不是质量档有限复核的成本基准。

具体参数以 src/Program.cs 的入口解析为准。真实报告含字幕正文和路径，保存在本地，公开文档只记录脱敏汇总。

## 更新与回退

install-update.ps1 用于**已有本地安装**，不是通用首次安装器。它默认读取 Cache/Releases/1.1.0/package.json，包内必须包含主程序 exe、pdb 和对应 SHA-256。Git 仓库不附带该本地包；从源码首次安装请使用上面的构建方式。

已有本地验收包时，托盘 → 退出，再执行：

~~~powershell
./install-update.ps1
~~~

脚本拒绝替换任何运行中的同名主程序，先备份、再校验替换；中途失败恢复已替换文件。它不更新 watcher，不覆盖设置、字幕或缓存。要回退，完全退出后从对应 Cache/Rollback-* 目录恢复原 exe/pdb。不要删除仅存的一份回退文件来“清理仓库”。

旧缓存的首次迁移可能重新产生基础翻译费用，详见[质量档缓存说明](quality-tier.md)。

## 发布检查

- 确认版本、构建和全部隔离回归，使用新导出的源码复验，避免依赖未提交文件。
- 检查暂存文件清单、差异、文档链接及设计素材引用。
- 仅发布源码、脚本、原创素材、依赖来源与脱敏文档；不发布 Config/、Cache/、Queue/、Logs/、hub/、密钥、媒体、字幕、模型、第三方程序或个人机器路径。
- 公共源码发布不等于二进制 Release、云端 CI 通过或全片质量认证；分别记录实际完成状态。
- 本项目尚未选择开放源代码许可证。未获得作者授权前，不擅自添加 MIT 等授权声明或打包第三方组件。
