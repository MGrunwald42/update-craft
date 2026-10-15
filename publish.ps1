# Builds update-craft.exe as a single file and drops it next to the app repositories.
param([string]$Destination = (Split-Path $PSScriptRoot -Parent))

dotnet publish "$PSScriptRoot\UpdateCraft.csproj" -c Release -o "$PSScriptRoot\publish" --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item "$PSScriptRoot\publish\update-craft.exe" $Destination -Force
Write-Host "Installed $Destination\update-craft.exe"
