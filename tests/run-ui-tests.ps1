$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$qa = Join-Path ([IO.Path]::GetTempPath()) ('MoyuWorkspaceUiTest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $qa | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName
$exe = Join-Path $qa 'WorkspaceUiTests.exe'
& $csc /nologo /target:exe /main:WorkspaceUiTests "/out:$exe" "/win32icon:$root\Assets\Moyu.ico" "/resource:$root\Assets\Brand\Hero.png,Moyu.Brand.Hero.png" "/resource:$root\Assets\Brand\Logo.png,Moyu.Brand.Logo.png" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources (Join-Path $PSScriptRoot 'WorkspaceUiTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed.' }
$result = Join-Path $qa 'result.txt'
$process = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $result
Get-Content -LiteralPath $result
Write-Output "UI test output and window previews: $qa"
if ($process.ExitCode -ne 0) { throw 'UI workspace tests failed.' }

$dpiResult = Join-Path $qa "dpi-result.txt"
$dpiProcess = Start-Process -FilePath $exe -ArgumentList "--quality-dpi" -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $dpiResult
Get-Content -LiteralPath $dpiResult
if ($dpiProcess.ExitCode -ne 0) { throw "Quality DPI tests failed." }
