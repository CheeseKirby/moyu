param(
    [Parameter(Mandatory=$true)][string]$Config,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Baseline,
    [ValidateSet('base','refine','thinking')][string]$Mode='refine',
    [ValidateSet('ja','en','ko')][string]$Language='ja',
    [switch]$ConfirmPaid
)
$ErrorActionPreference='Stop'
if (-not $ConfirmPaid) { throw 'This manual validation sends subtitles to your model provider and can incur charges. Supply -ConfirmPaid explicitly.' }
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root ('Cache/LiveValidation/'+[guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$csc="$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
$sources=Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Sort-Object Name | Select-Object -ExpandProperty FullName
$exe=Join-Path $output 'LiveTranslationValidation.exe'
& $csc /nologo /target:exe /main:LiveTranslationValidation "/out:$exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources (Join-Path $root 'tests/LiveTranslationValidation.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
& $exe --confirm-paid ([IO.Path]::GetFullPath($Config)) $Mode ([IO.Path]::GetFullPath($Source)) ([IO.Path]::GetFullPath($Baseline)) $output $Language
$code=$LASTEXITCODE
Write-Output "Private validation output: $output"
if ($code -ne 0) { throw "Validation incomplete (exit $code); inspect report before any paid retry." }
