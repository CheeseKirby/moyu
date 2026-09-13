$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$qa = Join-Path ([IO.Path]::GetTempPath()) ('MoyuQ-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $qa | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName
$exe = Join-Path $qa 'QualityTierTests.exe'
& $csc /nologo /target:exe /main:QualityTierTests "/out:$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources (Join-Path $PSScriptRoot 'TranslationClientTests.cs') (Join-Path $PSScriptRoot 'QualityTierTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Quality tier test compilation failed.' }
$result = Join-Path $qa 'result.txt'
$process = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $result
Get-Content -LiteralPath $result
Write-Output "Quality tier test output: $qa"
if ($process.ExitCode -ne 0) { throw 'Quality tier tests failed.' }
