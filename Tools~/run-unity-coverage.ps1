#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$TemplateProject,
    [Parameter(Mandatory)][string]$UnityPath,
    [Parameter(Mandatory)][string]$Output,
    [string]$ModelCases,
    [string]$ScreenInput,
    [int]$TimeoutSeconds = 1800
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path -LiteralPath $Root).Path
$TemplateProject = (Resolve-Path -LiteralPath $TemplateProject).Path
if ($ModelCases) { $ModelCases = (Resolve-Path -LiteralPath $ModelCases).Path }
if ($ScreenInput) { $ScreenInput = (Resolve-Path -LiteralPath $ScreenInput).Path }
if (!(Test-Path -LiteralPath $UnityPath -PathType Leaf)) { throw "Unity executable missing: $UnityPath" }
$ownedBase = [IO.Path]::GetFullPath('D:/Temp/UnityMeshLab-Coverage')
$ownedProject = Join-Path $ownedBase ([Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $ownedProject 'package'
$consumer = Join-Path $ownedProject 'consumer'
$results = Join-Path $ownedProject 'results'
New-Item -ItemType Directory -Force -Path $packageRoot,$consumer,$results,$Output | Out-Null
$process = $null
try {
    foreach ($folder in @('Editor','Tests','Runtime','Plugins','Shaders')) {
        $source = Join-Path $Root $folder
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $packageRoot -Recurse }
    }
    Copy-Item -LiteralPath (Join-Path $Root 'package.json') -Destination $packageRoot
    foreach ($folder in @('Assets','ProjectSettings','Packages')) {
        Copy-Item -LiteralPath (Join-Path $TemplateProject $folder) -Destination $consumer -Recurse
    }
    $manifestPath = Join-Path $consumer 'Packages/manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $manifest.dependencies['com.sasharx.unitymeshlab'] = 'file:' + $packageRoot.Replace('\','/')
    if (!$manifest.dependencies.ContainsKey('com.unity.testtools.codecoverage')) {
        $manifest.dependencies['com.unity.testtools.codecoverage'] = '1.2.7'
    }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath
    # A copied lockfile may bind the template's package to its previous location.
    $lockfile = Join-Path $consumer 'Packages/packages-lock.json'
    if (Test-Path -LiteralPath $lockfile) { Remove-Item -LiteralPath $lockfile }
    $hashes = @{}
    foreach ($folder in @('Editor','Tests','Runtime')) {
        Get-ChildItem -LiteralPath (Join-Path $packageRoot $folder) -Filter '*.cs' -File -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { $relative = [IO.Path]::GetRelativePath($packageRoot,$_.FullName).Replace('\','/'); $hashes[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    @{ package_root=$packageRoot.Replace('\','/'); files=$hashes; unity=$UnityPath } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'sources.json')
    $texturePackingSuites = @('TextureChannelPackerTests','TexturePackingLoadReviewTests','TexturePackingPsdTests',
        'TexturePackingTiffTests','TexturePackingFormatIntegrationTests','WindowPreferencesTests')
    $assetTransferSuites = @('ToolAssetSaveTests','FbxExportTests','FoliageFbxExportTests','PrReviewRegressionTests',
        'ReverseUvTransferTests','TransferCaptureTests','MeshNamingTests','MeshAccessTests')
    $viewportSuites = @('ViewportOverlayTests')
    $requiredSuites = $texturePackingSuites + $assetTransferSuites + $viewportSuites
    $testFilter = 'LodHardEdgeTests;LodSmoothingRegionTests;LodQslimTransferTests;LodAttributeCorrectionTests;LodQualitySelectionTests;LodGenerationToolTests;LodLoopSimplifierTests;LodSmallPartsTests;LodScreenAcceptanceTests;LodScreenValidationTests;LodProjectVisualQualityTests;CollisionElementTests;CollisionMeshToolTests;LodSharedUvTransferTests;LodPaintRetriangulationTests;' + ($requiredSuites -join ';')
    $arguments = @('-batchmode','-projectPath',$consumer,'-runTests','-testPlatform','EditMode','-testFilter',$testFilter,
        '-testResults',(Join-Path $results 'tests.xml'),'-logFile',(Join-Path $results 'unity.log'),
        '-enableCodeCoverage','-debugCodeOptimization','-coverageResultsPath',(Join-Path $results 'coverage'),
        '-coverageOptions','assemblyFilters:+SashaRX.UnityMeshLab.Editor',
        '-meshlabLodVisualOutput',(Join-Path $results 'visual'))
    if ($ModelCases) {
        if (!(Test-Path -LiteralPath $ModelCases)) { throw "Model case manifest missing: $ModelCases" }
        $arguments += @('-meshlabLodProjectCases',$ModelCases,
            '-meshlabLodBudget','-meshlabLodHardEdges','-meshlabLodFarBudget','-meshlabLodPaintDiagnostics','-meshlabLodAuthored','-meshlabLodAllColorViews')
    }
    if ($ScreenInput) {
        if (!(Test-Path -LiteralPath $ScreenInput)) { throw "Screen fixtures missing: $ScreenInput" }
        $arguments += @('-meshlabLodScreenInput',$ScreenInput)
    }
    $start = [Diagnostics.ProcessStartInfo]::new($UnityPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) { throw 'Owned Unity coverage process timed out' }
    if ($process.ExitCode -ne 0) { throw "Unity coverage failed with exit code $($process.ExitCode)" }
    [xml]$tests = Get-Content -LiteralPath (Join-Path $results 'tests.xml')
    if ([int]$tests.'test-run'.failed -ne 0 -or [int]$tests.'test-run'.passed -le 0) { throw 'Unity tests failed or did not execute' }
    foreach ($suite in $requiredSuites) {
        $executed = $tests.SelectNodes("//test-suite[@name='$suite']//test-case[@result='Passed']")
        if ($executed.Count -eq 0) { throw "Required coverage suite did not execute successfully: $suite" }
    }
    $viewportRegression = $tests.SelectNodes("//test-suite[@name='ViewportOverlayTests']//test-case[@name='UvFillStaysOnTheSurfaceWithCameraDirectedMaterialOffsets' and @result='Passed']")
    if ($viewportRegression.Count -eq 0) { throw 'Required viewport GPU regression did not pass' }
    $raw = @(Get-ChildItem -LiteralPath (Join-Path $results 'coverage') -Filter '*-opencov' -Directory)
    if ($raw.Count -ne 1) { throw 'Expected exactly one Unity OpenCover result directory' }
    Copy-Item -LiteralPath $raw[0].FullName -Destination (Join-Path $Output 'opencover') -Recurse
    Write-Output "Unity tests: $($tests.'test-run'.passed) passed, $($tests.'test-run'.skipped) skipped"
} finally {
    if ($process -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    foreach ($file in @('tests.xml','unity.log')) {
        $path = Join-Path $results $file
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $Output }
    }
    $visual = Join-Path $results 'visual'
    if (Test-Path -LiteralPath $visual) { Copy-Item -LiteralPath $visual -Destination (Join-Path $Output 'visual') -Recurse }
    # Delete only this generated project; never mutate the source or template.
    $resolvedOwned = [IO.Path]::GetFullPath($ownedProject)
    if (!$resolvedOwned.StartsWith($ownedBase + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside owned coverage directory'
    }
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try { Remove-Item -LiteralPath $resolvedOwned -Recurse -Force; break }
        catch {
            if ($attempt -eq 4) { Write-Warning "Owned coverage project cleanup failed: $($_.Exception.Message)" }
            else { Start-Sleep -Seconds 1 }
        }
    }
}
