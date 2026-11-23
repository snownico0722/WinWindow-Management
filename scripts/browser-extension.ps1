param(
    [string]$WinWindowExe,
    [string[]]$ExtensionId,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$hostName = 'com.winwindow.browser'
$directory = Join-Path $env:LOCALAPPDATA 'WinWindow-Management\Layout\BrowserBridge'
$manifest = Join-Path $directory 'native-host.json'
$registrations = @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts',
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts'
)

if ($Remove) {
    foreach ($root in $registrations) {
        $key = Join-Path $root $hostName
        if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key }
    }
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
    Write-Host 'WinWindow browser host removed. Restart WinWindow to close the connection.'
    return
}

if (-not $ExtensionId -or ($ExtensionId | Where-Object { $_ -cnotmatch '^[a-p]{32}$' })) {
    throw 'Copy the 32-character ID from the browser extension page.'
}
if (-not $WinWindowExe) { throw 'Supply -WinWindowExe with the path to the new WinWindow executable.' }
$exe = Get-Item -LiteralPath $WinWindowExe
if ($exe.PSIsContainer -or $exe.Extension -ne '.exe') { throw 'WinWindowExe must point to a Windows executable.' }
[void][IO.Directory]::CreateDirectory($directory)
$value = @{
    name = $hostName
    description = 'WinWindow browser relay'
    path = $exe.FullName
    type = 'stdio'
    allowed_origins = @($ExtensionId | Select-Object -Unique | ForEach-Object { "chrome-extension://$_/" })
} | ConvertTo-Json
[IO.File]::WriteAllText($manifest, $value, [Text.UTF8Encoding]::new($false))
foreach ($root in $registrations) {
    $key = Join-Path $root $hostName
    [void](New-Item -Path $key -Force)
    Set-Item -Path $key -Value $manifest
}
Write-Host 'WinWindow browser host registered for Chrome and Edge. Restart WinWindow and reload the extension.'
