param([string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
Import-Module Microsoft.PowerShell.Management -ErrorAction Stop
$root = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $root 'Cache/Releases/1.2.0-rc.1' }
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $PackageDirectory 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$names = @('AI-Subtitle-Worker.exe', 'AI-Subtitle-Worker.pdb')
foreach ($name in $names) {
    $expected = $manifest.Files.$name
    if (-not $expected -or (Get-FileHash -LiteralPath (Join-Path $PackageDirectory $name) -Algorithm SHA256).Hash -ne $expected) {
        throw "发布包校验失败：$name。未更新任何程序。"
    }
}
# Never stop the user's application. Refuse even if process path is unavailable.
if (Get-Process -Name 'AI-Subtitle-Worker' -ErrorAction SilentlyContinue) {
    throw '魔芋仍在运行。请先通过托盘菜单完全退出，再重新运行安装器；不会强制关闭程序。'
}
$backup = Join-Path $root ('Cache/Rollback-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
if (-not ([IO.Path]::GetFullPath($backup)).StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '备份路径超出项目目录。' }
New-Item -ItemType Directory -Path $backup | Out-Null
$configPath = Join-Path $root 'Config/settings.json'
$configHash = if (Test-Path -LiteralPath $configPath) { (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash } else { $null }
$changed = @()
try {
    foreach ($name in $names) {
        $target = Join-Path $root $name
        if (-not (Test-Path -LiteralPath $target)) { throw "缺少已安装文件：$name；请先检查工作目录。" }
        Copy-Item -LiteralPath $target -Destination (Join-Path $backup $name)
    }
    foreach ($name in $names) {
        $target = Join-Path $root $name
        $temporary = Join-Path $root ($name + '.update-' + [guid]::NewGuid().ToString('N'))
        try {
            Copy-Item -LiteralPath (Join-Path $PackageDirectory $name) -Destination $temporary
            [IO.File]::Replace($temporary, $target, [NullString]::Value)
            $changed += $name
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $manifest.Files.$name) { throw "更新后校验失败：$name" }
        } finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
        }
    }
    if ($configHash -and (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -ne $configHash) { throw '设置文件发生变化，请检查；安装器不会覆盖设置。' }
    Write-Output "魔芋 $($manifest.Version) 已更新。旧程序备份：$backup"
    Write-Output '设置、密钥、字幕与识别缓存均未修改。现在可以正常打开魔芋。'
} catch {
    foreach ($name in $changed) { Copy-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $root $name) -Force }
    throw
}
