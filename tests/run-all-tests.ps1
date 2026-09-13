$ErrorActionPreference = 'Stop'
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
Import-Module Microsoft.PowerShell.Management -ErrorAction Stop
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path ([IO.Path]::GetTempPath()) ('MoyuValidation-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $output | Out-Null
$timer = [Diagnostics.Stopwatch]::StartNew()
Start-Transcript -LiteralPath (Join-Path $output 'validation.txt') | Out-Null
try {
    & (Join-Path $root 'build.ps1') -OutputDirectory (Join-Path $output 'build')
    $exe = Join-Path $output 'build/AI-Subtitle-Worker.exe'
    foreach ($mode in @('self-test', 'ui-self-test')) {
        $process = Start-Process -FilePath $exe -ArgumentList $mode -WindowStyle Hidden -Wait -PassThru
        $result = Join-Path $output ('build/Logs/' + $mode + '-result.txt')
        if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result)) { throw "$mode failed (exit $($process.ExitCode))." }
    }
    foreach ($suite in @('translation', 'quality', 'evolution', 'ui', 'watcher', 'update')) {
        Write-Output "=== $suite ==="
        & (Join-Path $PSScriptRoot ("run-$suite-tests.ps1"))
    }
    Write-Output ('PASS all offline suites; elapsed {0:N1}s' -f $timer.Elapsed.TotalSeconds)
} finally {
    Stop-Transcript | Out-Null
    Write-Output "Validation output: $output"
}
