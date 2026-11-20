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

function Publish-Application([bool] $SelfContained) {
    $name = if ($SelfContained) { 'WinWindow-win-x64-self-contained' } else { 'WinWindow-win-x64' }
    $publish = Join-Path $stage $name
    $bundleRuntime = $SelfContained.ToString().ToLowerInvariant()
    dotnet publish $project -c Release -r win-x64 --self-contained $bundleRuntime '-p:PublishSingleFile=true' "-p:NoFocusLossResourceDirectory=$nativeResources" -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name ($LASTEXITCODE)" }

    $executable = Join-Path $publish 'WinWindow.exe'
    if (-not (Test-Path $executable -PathType Leaf)) { throw "Single-file executable was not produced for $name" }
    Get-ChildItem $publish -Filter '*.pdb' -File | Remove-Item
    Copy-Item $executable -Destination (Join-Path $output "$name.exe") -Force
}

try {
    New-Item -ItemType Directory -Path $output -Force | Out-Null

    # Remove legacy archive outputs so a local or CI run cannot leave stale ZIPs
    # that look like current release downloads.
    Get-ChildItem $output -Filter 'WinWindow-*.zip' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem $output -Filter 'WinWindow-win-x64*.exe' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem $output -Filter '*-LICENSE.txt' -File -ErrorAction SilentlyContinue | Remove-Item -Force

    New-Item -ItemType Directory -Path $nativeResources -Force | Out-Null
    Build-NoFocusLoss 'Win32' 'NoFocusLoss.dll'
    Build-NoFocusLoss 'x64' 'NoFocusLoss64.dll'
    Publish-Application $false
    if ($IncludeRuntime) { Publish-Application $true }

    # Keep the upstream notices beside the downloadable executable assets.
    Copy-Item (Join-Path $root 'native/NoFocusLoss/LICENSE.txt') (Join-Path $output 'NoFocusLoss-LICENSE.txt')
    Copy-Item (Join-Path $root 'native/NoFocusLoss/MinHook-LICENSE.txt') (Join-Path $output 'MinHook-LICENSE.txt')
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
