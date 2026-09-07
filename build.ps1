param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'src'
if (-not $OutputDirectory) { $OutputDirectory = $root }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) { throw "找不到 .NET Framework C# 编译器：$csc" }

$sources = Get-ChildItem -LiteralPath $source -File -Filter '*.cs' | Sort-Object Name | Select-Object -ExpandProperty FullName
if ($sources.Count -eq 0) { throw '没有找到源代码。' }

$output = Join-Path $OutputDirectory 'AI-Subtitle-Worker.exe'
$icon = Join-Path $root 'Assets\Moyu.ico'
$args = @(
  '/nologo',
  '/target:winexe',
  '/optimize+',
  '/platform:anycpu',
  '/debug:pdbonly',
  "/out:$output",
  "/win32icon:$icon",
  '/reference:System.dll',
  '/reference:System.Core.dll',
  '/reference:System.Drawing.dll',
  '/reference:System.Management.dll',
  '/reference:System.Windows.Forms.dll',
  '/reference:System.Web.Extensions.dll'
) + $sources

& $csc @args
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码：$LASTEXITCODE" }
Get-Item -LiteralPath $output | Select-Object FullName,Length,LastWriteTime

$watcherSources = @((Join-Path $source 'WatcherContract.cs')) + @(Get-ChildItem -LiteralPath (Join-Path $root 'watcher') -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
$watcherOutput = Join-Path $OutputDirectory 'Moyu-Watcher.exe'
& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$watcherOutput" /reference:System.dll /reference:System.Core.dll $watcherSources
if ($LASTEXITCODE -ne 0) { throw "检测器编译失败，退出码：$LASTEXITCODE" }
Get-Item -LiteralPath $watcherOutput | Select-Object FullName,Length,LastWriteTime
