param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [string]$UnityVersion = '6000.2.6f2',
    [string[]]$FbxPaths = @()
)
$ErrorActionPreference = 'Stop'
$benchPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$benchProject = [System.IO.Path]::GetFullPath($ProjectPath)
if ((Test-Path -LiteralPath $benchProject) -or $benchProject.StartsWith($benchPackage + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a new directory outside the package; existing projects are never replaced.'
}
$benchPackageInfo = Get-Content -LiteralPath (Join-Path $benchPackage 'package.json') -Raw | ConvertFrom-Json
if ($benchPackageInfo.name -ne 'com.sasharx.unitymeshlab') { throw 'PackagePath must point to UnityMeshLab.' }
if ($UnityVersion -notmatch '^6000\.\d+\.\d+[abfp]\d+$') { throw 'Specify a Unity 6 editor version, for example 6000.2.6f2.' }
$benchSources = @()
$benchNames = @{}
foreach ($benchFbx in $FbxPaths) {
    $benchSource = Get-Item -LiteralPath (Resolve-Path -LiteralPath $benchFbx).Path
    if ($benchSource.Extension -ne '.fbx' -or $benchSource.Length -gt 512MB -or $benchSource.PSIsContainer) { throw 'Each input must be an FBX file up to 512 MiB.' }
    if ($benchNames.ContainsKey($benchSource.Name)) { throw "Duplicate FBX filename: $($benchSource.Name)" }
    $benchNames[$benchSource.Name] = $true
    $benchSources += $benchSource
}
foreach ($benchFolder in @('Assets/TransferBenchmarkInputs', 'Packages', 'ProjectSettings', 'BenchmarkReports')) {
    New-Item -ItemType Directory -Path (Join-Path $benchProject $benchFolder) -Force | Out-Null
}
$benchManifest = @{
    dependencies = @{
        'com.sasharx.unitymeshlab' = 'file:' + $benchPackage.Replace('\', '/')
        'com.unity.test-framework' = '1.6.0'
        'com.unity.modules.imgui' = '1.0.0'
        'com.unity.modules.physics' = '1.0.0'
        'com.unity.modules.ui' = '1.0.0'
        'com.unity.modules.jsonserialize' = '1.0.0'
        'com.unity.modules.imageconversion' = '1.0.0'
        'com.unity.modules.animation' = '1.0.0'
        'com.unity.modules.audio' = '1.0.0'
        'com.unity.modules.assetbundle' = '1.0.0'
        'com.unity.modules.unitywebrequest' = '1.0.0'
    }
    testables = @('com.sasharx.unitymeshlab')
}
$benchManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $benchProject 'Packages/manifest.json') -Encoding UTF8
"m_EditorVersion: $UnityVersion" | Set-Content -LiteralPath (Join-Path $benchProject 'ProjectSettings/ProjectVersion.txt') -Encoding UTF8
$benchInputs = @()
foreach ($benchSource in $benchSources) {
    $benchDestination = Join-Path $benchProject "Assets/TransferBenchmarkInputs/$($benchSource.Name)"
    Copy-Item -LiteralPath $benchSource.FullName -Destination $benchDestination
    if (Test-Path -LiteralPath ($benchSource.FullName + '.meta')) {
        Copy-Item -LiteralPath ($benchSource.FullName + '.meta') -Destination ($benchDestination + '.meta')
    }
    $benchInputs += @{source = $benchSource.FullName; asset = "Assets/TransferBenchmarkInputs/$($benchSource.Name)";
        sha256 = (Get-FileHash -LiteralPath $benchSource.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
ConvertTo-Json -InputObject @($benchInputs) -Depth 3 | Set-Content -LiteralPath (Join-Path $benchProject 'source-inputs.json') -Encoding UTF8
$benchConfig = Get-Content -LiteralPath (Join-Path $benchPackage 'Tools~/transfer-benchmark.example.json') -Raw | ConvertFrom-Json
$benchConfig.outputRoot = 'BenchmarkReports'
$benchConfig | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $benchProject 'transfer-benchmark.json') -Encoding UTF8
Write-Output "Prepared isolated benchmark project: $benchProject"
Write-Output 'Configure captures/assetCases in transfer-benchmark.json, then run run-transfer-benchmark.ps1.'
