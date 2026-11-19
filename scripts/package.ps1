[CmdletBinding()]
param([switch] $IncludeRuntime)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/WinWindow/WinWindow.csproj'
$output = Join-Path $root 'artifacts'
$stage = Join-Path ([IO.Path]::GetTempPath()) ('WinWindow-' + [guid]::NewGuid())

function Write-Package([bool] $SelfContained) {
    $name = if ($SelfContained) { 'WinWindow-win-x64-self-contained' } else { 'WinWindow-win-x64' }
    $publish = Join-Path $stage $name
    $bundleRuntime = $SelfContained.ToString().ToLowerInvariant()
    dotnet publish $project -c Release -r win-x64 --self-contained $bundleRuntime -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name ($LASTEXITCODE)" }

    Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'docs/MANUAL_TESTS.md') -Destination $publish
    Get-ChildItem $publish -Filter '*.pdb' -File | Remove-Item
    $zip = Join-Path $stage "$name.zip"
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
    Move-Item $zip -Destination (Join-Path $output "$name.zip") -Force
}

try {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Write-Package $false
    if ($IncludeRuntime) { Write-Package $true }
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
