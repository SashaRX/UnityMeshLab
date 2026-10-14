param(
    [Parameter(Mandatory=$true)][string]$UnityEditor,
    [Parameter(Mandatory=$true)][string]$ProjectPath,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [Parameter(Mandatory=$true)][string]$NativeFootprintLibrary,
    [Parameter(Mandatory=$true)][string]$NativeQueryLibrary,
    [ValidateRange(1,5)][int]$Repetitions=3
)
$ErrorActionPreference='Stop'
$queryRoot=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $queryRoot) {throw 'Use a fresh output directory for paired query trials.'}
$queryFilter='SashaRX.UnityMeshLab.Tests.TransferPerformanceTests;SashaRX.UnityMeshLab.Tests.ReverseUvFrozenCascadeTests;SashaRX.UnityMeshLab.Tests.ReverseUvFamilyPackingTests;SashaRX.UnityMeshLab.Tests.ReverseUvFootprintPackingTests;SashaRX.UnityMeshLab.Tests.ReverseUvSeamTests;SashaRX.UnityMeshLab.Tests.ReverseUvTransferTests;SashaRX.UnityMeshLab.Tests.UvJunctionCutsTests;SashaRX.UnityMeshLab.Tests.ReverseUvQueryBackendTests'
for($queryRound=1;$queryRound -le $Repetitions;++$queryRound) {
    $queryOrder=if($queryRound%2){@('legacy','auto')}else{@('auto','legacy')}
    foreach($queryArm in $queryOrder) {
        $queryOptions=@{UnityEditor=$UnityEditor;ProjectPath=$ProjectPath;
            OutputRoot=(Join-Path $queryRoot ($queryArm+'/round-'+$queryRound));Repetitions=1;
            NativeFootprintLibrary=$NativeFootprintLibrary;NativeQueryLibrary=$NativeQueryLibrary;
            QueryBackend=$queryArm;CombinedNative=$true;RequireBurstRaster=$true}
        if($queryRound -eq 1) {$queryOptions.AdditionalTestFilter=$queryFilter}
        Write-Output "Query round $queryRound / $queryArm"
        & (Join-Path $PSScriptRoot 'run-transfer-performance.ps1') @queryOptions
    }
}
