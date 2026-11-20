[CmdletBinding()]
param([switch] $IncludeRuntime, [switch] $TestNative)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/WinWindow/WinWindow.csproj'
$output = Join-Path $root 'artifacts'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('WinWindow-' + [guid]::NewGuid())

$nativeResources = Join-Path $stage 'native-resources'

function Build-NoFocusLoss([string] $Platform, [string] $LibraryName) {
    $build = Join-Path $output "native-$Platform"
    $testing = if ($TestNative) { 'ON' } else { 'OFF' }
    cmake -S (Join-Path $root 'native/NoFocusLoss') -B $build -A $Platform "-DBUILD_TESTING=$testing"
    if ($LASTEXITCODE -ne 0) { throw "NoFocusLoss configure failed for $Platform" }
    cmake --build $build --config Release
    if ($LASTEXITCODE -ne 0) { throw "NoFocusLoss build failed for $Platform" }
    if ($TestNative) {
        ctest --test-dir $build -C Release --output-on-failure --no-tests=error
        if ($LASTEXITCODE -ne 0) { throw "NoFocusLoss behavior test failed for $Platform" }
    }
    Copy-Item (Join-Path $build "Release/$LibraryName") -Destination $nativeResources
}

function Write-Package([bool] $SelfContained) {
    $name = if ($SelfContained) { 'WinWindow-win-x64-self-contained' } else { 'WinWindow-win-x64' }
    $publish = Join-Path $stage $name
    $bundleRuntime = $SelfContained.ToString().ToLowerInvariant()
    dotnet publish $project -c Release -r win-x64 --self-contained $bundleRuntime '-p:PublishSingleFile=true' "-p:NoFocusLossResourceDirectory=$nativeResources" -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name ($LASTEXITCODE)" }

    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'docs/MANUAL_TESTS.md') -Destination $publish
    $notices = Join-Path $publish 'licenses/NoFocusLoss'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    Copy-Item (Join-Path $root 'native/NoFocusLoss/*LICENSE.txt') -Destination $notices
    Get-ChildItem $publish -Filter '*.pdb' -File | Remove-Item
    $zip = Join-Path $stage "$name.zip"
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
    Move-Item $zip -Destination (Join-Path $output "$name.zip") -Force
}

try {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    New-Item -ItemType Directory -Path $nativeResources -Force | Out-Null
    Build-NoFocusLoss 'Win32' 'NoFocusLoss.dll'
    Build-NoFocusLoss 'x64' 'NoFocusLoss64.dll'
    Write-Package $false
    if ($IncludeRuntime) { Write-Package $true }
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
