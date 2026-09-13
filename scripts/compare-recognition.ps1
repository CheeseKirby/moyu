param(
    [Parameter(Mandatory = $true)][string]$Media,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'AI-Subtitle-Worker.exe'
if (-not (Test-Path -LiteralPath $Media -PathType Leaf)) { throw "找不到媒体文件：$Media" }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "找不到 $exe" }
if (-not $OutDir) { $OutDir = (Split-Path -Parent $Media) }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

Write-Host "当前使用翻译档位：" (Get-Content -Raw (Join-Path $root 'Config\settings.json') | ConvertFrom-Json).TranslationQuality
Write-Host "快速档：识别 + 基础质量清理"
Write-Host "质量档：识别 + 响度归一/高通 + 术语索引提示词与拼写校正"
Write-Host "开始对比识别（需要完整 ffmpeg / whisper / 模型 / API Key）……"

$Media = [IO.Path]::GetFullPath($Media)
$OutDir = [IO.Path]::GetFullPath($OutDir)
# Start-Process joins arguments; quote spaces and use slash paths to preserve trailing root separators.
$arguments = @('compare-recognition', '--media', ('"' + $Media.Replace('\', '/') + '"'), '--out', ('"' + $OutDir.Replace('\', '/') + '"'))
$process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) {
    Write-Error "对比识别失败，退出码 $($process.ExitCode)，请查看 Logs\worker.log"
    exit $process.ExitCode
}

$name = [IO.Path]::GetFileNameWithoutExtension($Media)
Write-Host "完成："
Write-Host "  $name-fast.srt / $name-quality.srt"
Write-Host "  汇总：$(Join-Path $OutDir ($name + '-compare.txt'))"
