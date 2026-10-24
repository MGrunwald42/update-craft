<#
.SYNOPSIS
    Sets up the MX Creative Keypad button for update-craft: publishes update-craft.exe and installs the keypad plugin.

.DESCRIPTION
    1. Checks for Logi Options+ (Logi Plugin Service) and the .NET 10 SDK.
    2. Publishes update-craft.exe with publish.ps1, so it reports its progress.
    3. Writes %LOCALAPPDATA%\update-craft\keypad.json: which exe the key starts, and with which arguments.
    4. Builds the plugin. The build registers it with Logi Plugin Service (a .link file in its Plugins folder that
       points at keypad-plugin\bin\Release) and asks the service to reload it.
    5. Waits for the service to report whether the plugin loaded.
    Run it again after pulling changes. The button itself is assigned in Options+, see README.md.

.PARAMETER Arguments
    What update-craft is started with when the key is pressed, e.g. 'update all --env-file C:\path\github.env', or
    just '--env-file C:\path\github.env' for the interactive menu. Default: 'update all', or what keypad.json already says.

.PARAMETER Destination
    Where publish.ps1 installs update-craft.exe (default: the folder holding this repository, next to the app repos).

.PARAMETER Uninstall
    Unregisters the plugin and deletes its build output. update-craft.exe and keypad.json are left in place.

.EXAMPLE
    .\keypad-plugin\setup.ps1 -Arguments '--env-file "C:\Users\me\GitHub\.env"'
#>
param([string]$Arguments, [string]$Destination, [switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'src\UpdateCraftPlugin.csproj'
$service = Join-Path $env:ProgramFiles 'Logi\LogiPluginService'
$pluginLog = Join-Path $env:LOCALAPPDATA 'Logi\LogiPluginService\Logs\plugin_logs\UpdateCraft.log'
$settingsFile = Join-Path $env:LOCALAPPDATA 'update-craft\keypad.json'

if ($Uninstall) {
    # The project's clean target deletes the .link file along with the build output.
    dotnet clean $project -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host 'Plugin removed. Restart Logi Options+ (or sign out and in) for its action to disappear.'
    exit 0
}

# ── 1. Prerequisites ──
if (-not (Test-Path (Join-Path $service 'PluginApi.dll'))) {
    throw "Logi Plugin Service was not found in $service. Install Logi Options+ first: https://www.logitech.com/software/logi-options-plus.html"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or
    -not (dotnet --list-sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 10 })) {
    throw 'The .NET 10 SDK (or newer) is required: https://dotnet.microsoft.com/download'
}

# ── 2. update-craft.exe ──
Write-Host "`n== Publishing update-craft ==" -ForegroundColor Cyan
$publishArgs = @{}
if ($Destination) { $publishArgs.Destination = $Destination }
& (Join-Path $repo 'publish.ps1') @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'publish.ps1 failed (is update-craft.exe still running?)' }
$exe = Join-Path $(if ($Destination) { $Destination } else { Split-Path $repo -Parent }) 'update-craft.exe'

# ── 3. What the key starts ──
$existing = if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw | ConvertFrom-Json } else { $null }
if (-not $Arguments) { $Arguments = if ($existing.Arguments) { $existing.Arguments } else { 'update all' } }
New-Item -ItemType Directory -Force (Split-Path $settingsFile) | Out-Null
[ordered]@{ Exe = (Resolve-Path $exe).Path; Arguments = $Arguments } | ConvertTo-Json | Set-Content $settingsFile -Encoding utf8
Write-Host "`nThe key starts: $exe $Arguments   (saved in $settingsFile)"

# ── 4. Plugin ──
Write-Host "`n== Building and registering the keypad plugin ==" -ForegroundColor Cyan
$started = Get-Date
dotnet build $project -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# ── 5. Did Logi Plugin Service load it? ──
Write-Host "`n== Waiting for Logi Plugin Service ==" -ForegroundColor Cyan
$result = $null
for ($i = 0; $i -lt 30 -and -not $result; $i++) {
    Start-Sleep -Milliseconds 500
    if ((Test-Path $pluginLog) -and (Get-Item $pluginLog).LastWriteTime -gt $started) {
        $result = Select-String -Path $pluginLog -Pattern "Plugin 'UpdateCraft' version .* loaded|Cannot load plugin" | Select-Object -Last 1
    }
}

if (-not $result) {
    Write-Warning "No answer from Logi Plugin Service yet. If 'update-craft' is missing under Installed Plugins in Options+, restart Options+. Log: $pluginLog"
}
elseif ($result.Line -match 'Cannot load') {
    throw "Logi Plugin Service could not load the plugin. Details: $pluginLog and $(Split-Path $pluginLog)\_errors.log"
}
else {
    Write-Host 'Plugin loaded.' -ForegroundColor Green
}

Write-Host @"

Next, in Logi Options+:
  1. Select the MX Creative Keypad.
  2. Open "All Actions" > "Installed Plugins" > "update-craft" and drag "Update Craft apps" onto a key
     (in place of a manually added update-craft button).
  3. Leave the key's icon alone in the icon editor: a custom icon may take the place of the progress display.

Pressing the key starts update-craft in its own window; the key shows its progress and goes back to its icon when done.
To change what the key starts, run this script again with -Arguments.
"@
