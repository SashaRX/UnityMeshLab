param(
    [Parameter(Mandatory=$true)][string]$UnityEditor,
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [Parameter(Mandatory=$true)][string]$Library,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [ValidateSet('raw','native','guided','candidates')][string]$Backend='raw',
    [string]$BakeLayoutRoot=''
)
$ErrorActionPreference='Stop'
$benchProject=(Resolve-Path -LiteralPath $ProjectPath).Path
if(Test-Path -LiteralPath (Join-Path $benchProject 'Temp/UnityLockfile')) {throw 'Use a closed isolated project.'}
$benchRoot=[IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $benchRoot -Force | Out-Null
$benchFiles=@('EmbreeQueryBench.cs','ManagedCandidateBvh.cs','BakerQueryBench.cs')
$benchOwned=@{}
foreach($benchFile in $benchFiles){$benchDriver=Join-Path $benchProject ('Assets/Editor/'+$benchFile);
    if((Test-Path -LiteralPath $benchDriver) -or (Test-Path -LiteralPath ($benchDriver+'.meta'))) {throw 'Private driver already exists; inspect it before replacing.'}}
New-Item -ItemType Directory -Path (Join-Path $benchProject 'Assets/Editor') -Force | Out-Null
$benchLibraryBefore=$env:MESHLAB_EMBREE_BENCH_LIBRARY
$benchOutputBefore=$env:MESHLAB_EMBREE_BENCH_OUTPUT
$benchBackendBefore=$env:MESHLAB_EMBREE_BENCH_BACKEND
$benchBakeBefore=$env:MESHLAB_EMBREE_BAKE_LAYOUTS
try {
    foreach($benchFile in $benchFiles){$benchDriver=Join-Path $benchProject ('Assets/Editor/'+$benchFile);
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $benchFile) -Destination $benchDriver;$benchOwned[$benchDriver]=(Get-FileHash -LiteralPath $benchDriver).Hash}
    $env:MESHLAB_EMBREE_BENCH_LIBRARY=(Resolve-Path -LiteralPath $Library).Path
    $env:MESHLAB_EMBREE_BENCH_OUTPUT=Join-Path $benchRoot 'queries.json'
    $env:MESHLAB_EMBREE_BENCH_BACKEND=$Backend
    $env:MESHLAB_EMBREE_BAKE_LAYOUTS=if($BakeLayoutRoot){(Resolve-Path -LiteralPath $BakeLayoutRoot).Path}else{$null}
    $benchLog=Join-Path $benchRoot 'unity.log'
    $benchArgs=@('-batchmode','-nographics','-quit','-projectPath',('"'+$benchProject+'"'),'-executeMethod','EmbreeQueryBench.Run','-logFile',('"'+$benchLog+'"'))
    $benchProcess=Start-Process -FilePath $UnityEditor -ArgumentList $benchArgs -PassThru -WindowStyle Hidden
    if(!$benchProcess.WaitForExit(900000)) {$benchProcess.Kill();throw 'Embree query test timed out.'}
    if($benchProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $env:MESHLAB_EMBREE_BENCH_OUTPUT)) {
        Get-Content -LiteralPath $benchLog -Tail 70
        throw "Embree query test failed with exit $($benchProcess.ExitCode)."
    }
    $benchReport=Get-Content -LiteralPath $env:MESHLAB_EMBREE_BENCH_OUTPUT -Raw | ConvertFrom-Json
    if($benchReport.backend -ne $Backend){throw 'Wrong query backend executed.'}
    if($benchReport.models.Count -ne 9 -or @($benchReport.models | Where-Object {$_.measurements.Count -ne 10}).Count) {throw 'Incomplete model/mode coverage.'}
    if($BakeLayoutRoot -and ($benchReport.bakes.Count -ne 6 -or @($benchReport.bakes | Where-Object {!$_.allMapBytesAndCountersExact -or $_.rayQueries -eq 0 -or $_.covered -le 0 -or $_.uniqueColors -le 1}).Count)){throw 'Incomplete bake verification.'}
    foreach($benchModel in $benchReport.models) {
        foreach($benchMeasurement in $benchModel.measurements | Where-Object threads -eq 8) {
            [pscustomobject]@{Model=$benchModel.name;Mode=$benchMeasurement.mode;Queries=$benchMeasurement.queries;Hits=$benchMeasurement.hitMismatch;Faces=$benchMeasurement.faceMismatch;Uv=$benchMeasurement.uvMismatch}
        }
    }
} finally {
    $env:MESHLAB_EMBREE_BENCH_LIBRARY=$benchLibraryBefore
    $env:MESHLAB_EMBREE_BENCH_OUTPUT=$benchOutputBefore
    $env:MESHLAB_EMBREE_BENCH_BACKEND=$benchBackendBefore
    $env:MESHLAB_EMBREE_BAKE_LAYOUTS=$benchBakeBefore
    foreach($benchDriver in $benchOwned.Keys){if((Test-Path -LiteralPath $benchDriver) -and (Get-FileHash -LiteralPath $benchDriver).Hash -eq $benchOwned[$benchDriver]) {
        Remove-Item -LiteralPath $benchDriver
        $benchMeta=$benchDriver+'.meta'
        if(Test-Path -LiteralPath $benchMeta) {Remove-Item -LiteralPath $benchMeta}
    }}
}
