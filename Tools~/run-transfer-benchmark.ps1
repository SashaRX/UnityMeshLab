param(
    [Parameter(Mandatory = $true)][string]$UnityEditor,
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [string]$ConfigPath,
    [int]$TimeoutSeconds = 1800
)
$ErrorActionPreference = 'Stop'
if ($TimeoutSeconds -lt 30 -or $TimeoutSeconds -gt 14400) { throw 'TimeoutSeconds must be 30..14400.' }
$benchEditor = (Resolve-Path -LiteralPath $UnityEditor).Path
$benchProject = (Resolve-Path -LiteralPath $ProjectPath).Path
if (Test-Path -LiteralPath (Join-Path $benchProject 'Temp/UnityLockfile')) {
    throw 'The benchmark project is open. Use a separate project with a file: reference to this package.'
}
$benchOutput = Join-Path $benchProject 'BenchmarkReports'
New-Item -ItemType Directory -Path $benchOutput -Force | Out-Null
$benchStamp = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$benchLog = Join-Path $benchOutput "transfer_batch_$benchStamp.log"
$benchArgs = @('-batchmode', '-force-d3d11', '-projectPath', $benchProject,
    '-executeMethod', 'SashaRX.UnityMeshLab.TransferBenchmarkCommands.RunBatch', '-logFile', $benchLog)
if ($ConfigPath) {
    $benchConfig = (Resolve-Path -LiteralPath $ConfigPath).Path
    $benchArgs += @('-meshLabTransferBench', $benchConfig)
}
# Windows PowerShell's Start-Process joins ArgumentList; quote paths with spaces explicitly.
$benchQuotedArgs = $benchArgs | ForEach-Object {
    if ($_ -match '["\r\n]') { throw 'Arguments cannot contain quotes or line breaks.' }
    # A trailing backslash escapes the closing quote in Windows argv parsing.
    '"' + ([regex]::Replace($_, '(\\+)$', '$1$1')) + '"'
}
$benchProcess = Start-Process -FilePath $benchEditor -ArgumentList $benchQuotedArgs -WindowStyle Hidden -PassThru
Write-Output "Benchmark PID $($benchProcess.Id); log: $benchLog"
if (-not $benchProcess.WaitForExit($TimeoutSeconds * 1000)) {
    # Only the process this script owns is terminated. Existing editors are untouched.
    Stop-Process -Id $benchProcess.Id -Force
    throw "Benchmark timed out; completed rows remain in the report. Log: $benchLog"
}
$benchProcess.Refresh()
if ($benchProcess.ExitCode -ne 0) { throw "Benchmark failed (exit $($benchProcess.ExitCode)). Log: $benchLog" }
Write-Output "Benchmark completed. Reports are listed in $benchLog"
