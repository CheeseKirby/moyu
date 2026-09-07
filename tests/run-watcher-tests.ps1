$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$qa = Join-Path ([IO.Path]::GetTempPath()) ('MoyuWatcherTest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $qa | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
# Isolate names in the shared contract only. All production watcher/controller logic is compiled unchanged.
# The real watcher never recognizes these fake players, and tests never touch the Run registry key.
$contract = [IO.File]::ReadAllText((Join-Path $root 'src\WatcherContract.cs'))
$contract = $contract.Replace('PotPlayerAiSubtitleApp-v2', ('MoyuWatcherTestMain-' + [guid]::NewGuid().ToString('N')))
$contract = $contract.Replace('"PotPlayerMini64"', '"MoyuTestPlayer"').Replace('"PotPlayerMini"', '"MoyuTestUnused1"').Replace('"PotPlayer64"', '"MoyuTestUnused2"').Replace('"PotPlayer"', '"MoyuTestUnused3"')
$contractFile = Join-Path $qa 'WatcherContract.cs'
[IO.File]::WriteAllText($contractFile, $contract, [Text.UTF8Encoding]::new($true))
$watcher = @(Get-ChildItem -LiteralPath (Join-Path $root 'watcher') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName)
$tests = Join-Path $PSScriptRoot 'WatcherTests.cs'
$startup = Join-Path $root 'src\StartupManager.cs'
$common = @('/nologo', '/optimize+', '/reference:System.dll', '/reference:System.Core.dll')
& $csc @common /target:winexe "/out:$qa\Moyu-Watcher.exe" $contractFile $watcher
if ($LASTEXITCODE -ne 0) { throw 'Watcher compile failed.' }
& $csc @common /target:exe /main:WatcherTests "/out:$qa\WatcherTests.exe" $contractFile $watcher $startup $tests
if ($LASTEXITCODE -ne 0) { throw 'Tests compile failed.' }
& $csc @common /target:winexe /main:WatcherTestStub "/out:$qa\AI-Subtitle-Worker.exe" $contractFile $watcher $startup $tests
if ($LASTEXITCODE -ne 0) { throw 'Stub compile failed.' }
Copy-Item -LiteralPath (Join-Path $qa 'AI-Subtitle-Worker.exe') -Destination (Join-Path $qa 'MoyuTestPlayer.exe')
$result = Join-Path $qa 'result.txt'
$process = Start-Process -FilePath (Join-Path $qa 'WatcherTests.exe') -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $result
Get-Content -LiteralPath $result
Write-Output "Isolated watcher test output: $qa"
if ($process.ExitCode -ne 0) { throw 'Watcher tests failed.' }
