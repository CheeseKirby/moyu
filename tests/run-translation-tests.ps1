$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$qa = Join-Path ([IO.Path]::GetTempPath()) ('MoyuTranslationClientTest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $qa | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName
$exe = Join-Path $qa 'TranslationClientTests.exe'
& $csc /nologo /target:exe /main:TranslationClientTests "/out:$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources (Join-Path $PSScriptRoot 'TranslationClientTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Translation test compilation failed.' }
$result = Join-Path $qa 'result.txt'
$process = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $result
Get-Content -LiteralPath $result
Write-Output "Translation test output: $qa"
if ($process.ExitCode -ne 0) { throw 'Translation client tests failed.' }
