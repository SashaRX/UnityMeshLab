param(
    [Parameter(Mandatory=$true)][string]$UnityEditor,
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [ValidateRange(1,5)][int]$Repetitions=3,
    [ValidateSet(10,32)][int]$Density=32,
    [ValidateRange(60,14400)][int]$TimeoutSeconds=1800,
    [switch]$DisableFootprintCache,
    [switch]$DisableFootprintSearchIndex,
    [switch]$DisableCollisionRowCache,
    [switch]$DisableNativeFootprintSearch,
    [string]$NativeFootprintLibrary='',
    [switch]$CombinedNative,
    [switch]$DisableOccupancySnapshotReuse,
    [switch]$DisableBurstRaster,
    [switch]$RequireBurstRaster,
    [switch]$CaptureNativePacking,
    [switch]$DisableForwardReuse,
    [string]$NativePackingReplayExecutable='',
    [string]$AdditionalTestFilter='',
    [ValidateSet('legacy','auto')][string]$QueryBackend='auto',
    [string]$NativeQueryLibrary=''
)
$ErrorActionPreference='Stop'
$perfEditor=(Resolve-Path -LiteralPath $UnityEditor).Path
$perfProject=(Resolve-Path -LiteralPath $ProjectPath).Path
if(Test-Path -LiteralPath (Join-Path $perfProject 'Temp/UnityLockfile')) {throw 'Use a closed, isolated test project.'}
$perfRoot=[IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $perfRoot -Force | Out-Null
# The three paired fixtures only: the TransferDiagnostics cases of the same test are ignored
# without MESHLAB_TRANSFER_USER_SETTINGS and are not part of this comparison.
$perfModelPattern="FrozenForwardReverseComparison\(.Assets/(JunctionFixtures|TransferBenchmarkInputs|KamazFixtures)/[^,]+,$Density\)"
$perfFilter="SashaRX\.UnityMeshLab\.Tests\.PreviewLodSwitchTests\.$perfModelPattern"
if($AdditionalTestFilter) {$perfFilter+=';'+$AdditionalTestFilter}
$savedOutput=$env:MESHLAB_PAIRED_TRANSFER_OUTPUT
$savedCache=$env:MESHLAB_DISABLE_FOOTPRINT_CACHE
$savedSearchIndex=$env:MESHLAB_DISABLE_FOOTPRINT_SEARCH_INDEX
$savedCollisionRows=$env:MESHLAB_DISABLE_COLLISION_ROW_CACHE
$savedNativeSearch=$env:MESHLAB_DISABLE_NATIVE_FOOTPRINT_SEARCH
$savedNativeSearchLibrary=$env:MESHLAB_NATIVE_FOOTPRINT_LIBRARY
$savedCombined=$env:MESHLAB_COMBINED_NATIVE
$savedReuse=$env:MESHLAB_DISABLE_OCCUPANCY_SNAPSHOT_REUSE
$savedBurst=$env:MESHLAB_DISABLE_BURST_RASTER
$savedRequireBurst=$env:MESHLAB_REQUIRE_BURST_RASTER
$savedNativeCapture=$env:MESHLAB_CAPTURE_NATIVE_PACKING
$savedNativeReplay=$env:MESHLAB_NATIVE_PACKING_REPLAY_EXECUTABLE
$savedForward=$env:MESHLAB_REVERSE_FROM_FORWARD
$savedForwardReuse=$env:MESHLAB_DISABLE_FORWARD_REUSE
$savedExisting=$env:MESHLAB_PAIRED_EXISTING_SEED
$savedQuery=$env:MESHLAB_QUERY_BACKEND
$savedQueryLibrary=$env:MESHLAB_NATIVE_QUERY_LIBRARY
try {
    $env:MESHLAB_QUERY_BACKEND=$QueryBackend
    $env:MESHLAB_NATIVE_QUERY_LIBRARY=if($NativeQueryLibrary){(Resolve-Path -LiteralPath $NativeQueryLibrary).Path}else{''}
    if($NativeFootprintLibrary) {
        $NativeFootprintLibrary=(Resolve-Path -LiteralPath $NativeFootprintLibrary).Path
        if(!(Test-Path -LiteralPath $NativeFootprintLibrary -PathType Leaf)) {throw 'Private native footprint library is missing.'}
    }
    $env:MESHLAB_NATIVE_FOOTPRINT_LIBRARY=$NativeFootprintLibrary
    if($CombinedNative -and !$NativeFootprintLibrary) {throw 'Combined native testing requires a private library.'}
    if($RequireBurstRaster -and $DisableBurstRaster) {throw 'Required Burst raster cannot be disabled.'}
    $env:MESHLAB_COMBINED_NATIVE=if($CombinedNative){'1'}else{'0'}
    $env:MESHLAB_DISABLE_OCCUPANCY_SNAPSHOT_REUSE=if($DisableOccupancySnapshotReuse){'1'}else{'0'}
    $env:MESHLAB_DISABLE_BURST_RASTER=if($DisableBurstRaster){'1'}else{'0'}
    $env:MESHLAB_REQUIRE_BURST_RASTER=if($RequireBurstRaster){'1'}else{'0'}
    $env:MESHLAB_DISABLE_NATIVE_FOOTPRINT_SEARCH=if($DisableNativeFootprintSearch){'1'}else{'0'}
    if($NativePackingReplayExecutable) {
        $NativePackingReplayExecutable=(Resolve-Path -LiteralPath $NativePackingReplayExecutable).Path
        if(!(Test-Path -LiteralPath $NativePackingReplayExecutable -PathType Leaf)) {throw 'Native replay executable is missing.'}
    }
    $env:MESHLAB_CAPTURE_NATIVE_PACKING=if($CaptureNativePacking -or $NativePackingReplayExecutable){'1'}else{'0'}
    $env:MESHLAB_NATIVE_PACKING_REPLAY_EXECUTABLE=$NativePackingReplayExecutable
    $env:MESHLAB_DISABLE_FOOTPRINT_CACHE=if($DisableFootprintCache){'1'}else{'0'}
    $env:MESHLAB_DISABLE_FOOTPRINT_SEARCH_INDEX=if($DisableFootprintSearchIndex){'1'}else{'0'}
    $env:MESHLAB_DISABLE_COLLISION_ROW_CACHE=if($DisableCollisionRowCache){'1'}else{'0'}
    $env:MESHLAB_REVERSE_FROM_FORWARD='1'
    $env:MESHLAB_DISABLE_FORWARD_REUSE=if($DisableForwardReuse){'1'}else{'0'}
    $env:MESHLAB_PAIRED_EXISTING_SEED='0'
    for($perfRun=1;$perfRun -le $Repetitions;++$perfRun) {
        $perfDirectory=Join-Path $perfRoot ('run-'+$perfRun.ToString('00'))
        if(Test-Path -LiteralPath $perfDirectory) {throw "Choose a fresh OutputRoot: $perfDirectory already exists."}
        New-Item -ItemType Directory -Path $perfDirectory | Out-Null
        $env:MESHLAB_PAIRED_TRANSFER_OUTPUT=Join-Path $perfDirectory 'models'
        $perfXml=Join-Path $perfDirectory 'tests.xml'
        $perfLog=Join-Path $perfDirectory 'unity.log'
        $perfArgs=@('-batchmode','-force-d3d11','-stackTraceLogType','None','-projectPath',$perfProject,
            '-runTests','-testPlatform','EditMode','-testFilter',$perfFilter,'-testResults',$perfXml,'-logFile',$perfLog)
        $perfQuoted=$perfArgs | ForEach-Object {
            if($_ -match '["\r\n]') {throw 'Arguments cannot contain quotes or line breaks.'}
            '"'+([regex]::Replace($_,'(\\+)$','$1$1'))+'"'
        }
        $perfProcess=Start-Process -FilePath $perfEditor -ArgumentList $perfQuoted -WindowStyle Hidden -PassThru
        Write-Output "Run $perfRun; PID $($perfProcess.Id); log: $perfLog"
        if(!$perfProcess.WaitForExit($TimeoutSeconds*1000)) {
            Stop-Process -Id $perfProcess.Id -Force
            throw "Owned benchmark process timed out. Completed artifacts remain in $perfDirectory."
        }
        $perfProcess.Refresh()
        if($perfProcess.ExitCode -ne 0) {throw "Unity failed with exit $($perfProcess.ExitCode). See $perfLog."}
        [xml]$perfResults=Get-Content -LiteralPath $perfXml -Raw
        $perfCases=@($perfResults.SelectNodes('//test-case') | Where-Object {$_.fullname -match $perfModelPattern})
        if($perfResults.'test-run'.failed -ne '0' -or $perfCases.Count -ne 3 -or @($perfCases | Where-Object {$_.result -ne 'Passed'}).Count -ne 0) {
            throw "All three model cases must execute and pass. See $perfXml."
        }
        Write-Output "Run ${perfRun}: three models passed; ordinary and both reverse modes exported."
    }
}
finally {
    $env:MESHLAB_PAIRED_TRANSFER_OUTPUT=$savedOutput
    $env:MESHLAB_DISABLE_FOOTPRINT_CACHE=$savedCache
    $env:MESHLAB_DISABLE_FOOTPRINT_SEARCH_INDEX=$savedSearchIndex
    $env:MESHLAB_DISABLE_COLLISION_ROW_CACHE=$savedCollisionRows
    $env:MESHLAB_DISABLE_NATIVE_FOOTPRINT_SEARCH=$savedNativeSearch
    $env:MESHLAB_NATIVE_FOOTPRINT_LIBRARY=$savedNativeSearchLibrary
    $env:MESHLAB_COMBINED_NATIVE=$savedCombined
    $env:MESHLAB_DISABLE_OCCUPANCY_SNAPSHOT_REUSE=$savedReuse
    $env:MESHLAB_DISABLE_BURST_RASTER=$savedBurst
    $env:MESHLAB_REQUIRE_BURST_RASTER=$savedRequireBurst
    $env:MESHLAB_CAPTURE_NATIVE_PACKING=$savedNativeCapture
    $env:MESHLAB_NATIVE_PACKING_REPLAY_EXECUTABLE=$savedNativeReplay
    $env:MESHLAB_REVERSE_FROM_FORWARD=$savedForward
    $env:MESHLAB_DISABLE_FORWARD_REUSE=$savedForwardReuse
    $env:MESHLAB_PAIRED_EXISTING_SEED=$savedExisting
    $env:MESHLAB_QUERY_BACKEND=$savedQuery
    $env:MESHLAB_NATIVE_QUERY_LIBRARY=$savedQueryLibrary
}
