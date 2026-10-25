param(
    [string]$GameRoot = $env:COI_ROOT,
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$GameRoot) { $GameRoot = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Steam/steamapps/common/Captain of Industry' }
if (!(Test-Path -LiteralPath (Join-Path $GameRoot 'Captain of Industry_Data/Managed/Mafi.Core.dll'))) {
    throw 'Set COI_ROOT or pass -GameRoot with your game installation path.'
}
Push-Location $repoRoot
try {
    dotnet build TrainParameterMod.sln -c Release "-p:COI_ROOT=$GameRoot"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
    dotnet ./tests/bin/Release/net10.0/TrainParameterMod.SmokeTests.dll $GameRoot $repoRoot
    if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed.' }
    $manifest = Get-Content ./manifest.json -Raw | ConvertFrom-Json
    $package = Join-Path $repoRoot "dist/$($manifest.id)"
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    foreach ($file in @('manifest.json', 'config.json', 'LICENSE', 'README.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $package
    }
    Copy-Item ./assets/thumbnail.png $package
    Copy-Item ./src/bin/Release/net48/TrainParameterMod.dll $package
    Copy-Item ./src/bin/Release/net48/0Harmony.dll $package
    Copy-Item ./THIRD-PARTY-NOTICES.txt $package
    $zip = Join-Path $repoRoot "dist/$($manifest.id)-$($manifest.version).zip"
    Compress-Archive -LiteralPath $package -DestinationPath $zip -Force
    Write-Output "Package: $zip"
    if ($Install) {
        $mods = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Captain of Industry/Mods/$($manifest.id)"
        New-Item -ItemType Directory -Path $mods -Force | Out-Null
        Get-ChildItem -LiteralPath $package -File | Copy-Item -Destination $mods
        Write-Output "Installed: $mods"
    }
}
finally { Pop-Location }
