$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
Import-Module Microsoft.PowerShell.Management -ErrorAction Stop
$root = Split-Path -Parent $PSScriptRoot
$qa = Join-Path ([IO.Path]::GetTempPath()) ('MoyuUpdate-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $qa | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'install-update.ps1') -Destination $qa
$package = Join-Path $qa 'package'
New-Item -ItemType Directory -Path $package | Out-Null
New-Item -ItemType Directory -Path (Join-Path $qa 'Config') | Out-Null
[IO.File]::WriteAllText((Join-Path $qa 'Config/settings.json'), 'preserve-settings')
$names = @('AI-Subtitle-Worker.exe', 'AI-Subtitle-Worker.pdb')
$hashes = @{}
foreach ($name in $names) {
    [IO.File]::WriteAllText((Join-Path $qa $name), 'old-' + $name)
    [IO.File]::WriteAllText((Join-Path $package $name), 'new-' + $name)
    $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $package $name)).Hash
}
@{ Version = 'test'; Files = $hashes } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $package 'package.json') -Encoding UTF8
# Mock only inside this test script. The copied installer targets the isolated temporary directory.
function Get-Process { param($Name, $ErrorAction); [pscustomobject]@{ Id = 1 } }
$refused = $false
try { & (Join-Path $qa 'install-update.ps1') -PackageDirectory $package } catch { $refused = $true }
if (-not $refused -or (Get-Content -LiteralPath (Join-Path $qa $names[0]) -Raw) -ne ('old-' + $names[0])) { throw 'Running app protection failed' }
Write-Output 'PASS running application refuses installation without replacing files'
function Get-Process { param($Name, $ErrorAction); return $null }
& (Join-Path $qa 'install-update.ps1') -PackageDirectory $package
foreach ($name in $names) { if ((Get-FileHash -LiteralPath (Join-Path $qa $name)).Hash -ne $hashes[$name]) { throw 'Installed hash mismatch' } }
if ((Get-Content -LiteralPath (Join-Path $qa 'Config/settings.json') -Raw) -ne 'preserve-settings') { throw 'Settings changed' }
$backup = Get-ChildItem -LiteralPath (Join-Path $qa 'Cache') -Directory | Select-Object -First 1
if ((Get-Content -LiteralPath (Join-Path $backup.FullName $names[0]) -Raw) -ne ('old-' + $names[0])) { throw 'Rollback copy missing' }
Write-Output 'PASS hash-verified installation, original backup and settings preservation'
# Force the second replacement to fail after the first one succeeded.
foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $qa $name), 'rollback-' + $name) }
$pdbTarget = Join-Path $qa $names[1]
[IO.File]::SetAttributes($pdbTarget, [IO.FileAttributes]::ReadOnly)
$rolledBack = $false
try { & (Join-Path $qa 'install-update.ps1') -PackageDirectory $package } catch { $rolledBack = $true }
finally { [IO.File]::SetAttributes($pdbTarget, [IO.FileAttributes]::Normal) }
if (-not $rolledBack) { throw 'Expected second-file installation failure' }
foreach ($name in $names) {
    if ((Get-Content -LiteralPath (Join-Path $qa $name) -Raw) -ne ('rollback-' + $name)) { throw 'Partial installation rollback failed' }
}
Write-Output 'PASS second-file failure rolls back the already replaced executable'
$beforeTamper = (Get-FileHash -LiteralPath (Join-Path $qa $names[0])).Hash
[IO.File]::WriteAllText((Join-Path $package $names[0]), 'tampered')
$refused = $false
try { & (Join-Path $qa 'install-update.ps1') -PackageDirectory $package } catch { $refused = $true }
if (-not $refused -or (Get-FileHash -LiteralPath (Join-Path $qa $names[0])).Hash -ne $beforeTamper) { throw 'Tampered package accepted' }
Write-Output 'PASS corrupted package refused before replacement'
Write-Output "Isolated update tests: $qa; no production files or processes changed."
