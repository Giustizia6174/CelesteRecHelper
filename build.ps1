[CmdletBinding()]
param(
    [string]$CelesteDirectory = $env:CELESTE_DIR,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$SkipDeploy,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'

function Find-CelesteDirectory {
    $steamRoots = @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")
    $steamRegistry = Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
    if ($steamRegistry.SteamPath) { $steamRoots += $steamRegistry.SteamPath }
    foreach ($steamRoot in ($steamRoots | Select-Object -Unique)) {
        $libraries = @($steamRoot)
        $libraryFile = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $libraryFile) {
            $libraryText = Get-Content -LiteralPath $libraryFile -Raw
            foreach ($match in [regex]::Matches($libraryText, '"path"\s*"([^"]+)"')) {
                $libraries += $match.Groups[1].Value.Replace('\\', '\')
            }
        }
        foreach ($library in ($libraries | Select-Object -Unique)) {
            $candidate = Join-Path $library 'steamapps\common\Celeste'
            if (Test-Path -LiteralPath (Join-Path $candidate 'MMHOOK_Celeste.dll')) { return $candidate }
        }
    }
    throw 'Everest installation not found. Pass -CelesteDirectory or set CELESTE_DIR.'
}

if (-not $CelesteDirectory) { $CelesteDirectory = Find-CelesteDirectory }
$CelesteDirectory = (Resolve-Path -LiteralPath $CelesteDirectory).Path
foreach ($assembly in @('Celeste.dll', 'MMHOOK_Celeste.dll', 'FNA.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $CelesteDirectory $assembly))) {
        throw "Required Everest assembly is missing: $assembly"
    }
}
$dotnetCommand = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetCommand)) { $dotnetCommand = (Get-Command dotnet -ErrorAction Stop).Source }
$stageDirectory = Join-Path $PSScriptRoot "dist\$Configuration\CelesteRecHelper"
$modDirectory = Join-Path $CelesteDirectory 'Mods\CelesteRecHelper'

Push-Location $PSScriptRoot
try {
    & $dotnetCommand build CelesteRecHelper.sln --configuration $Configuration "-p:CelesteDirectory=$CelesteDirectory" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $dotnetCommand test tests/CelesteRecHelper.Core.Tests/CelesteRecHelper.Core.Tests.csproj --configuration $Configuration --no-build --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    New-Item -ItemType Directory -Path (Join-Path $stageDirectory 'bin') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stageDirectory 'Dialog') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'everest.yaml') -Destination $stageDirectory -Force
    foreach ($name in @('English.txt', 'Simplified Chinese.txt')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Dialog\$name") -Destination (Join-Path $stageDirectory 'Dialog') -Force
    }
    foreach ($name in @('CelesteRecHelper.dll', 'CelesteRecHelper.pdb', 'CelesteRecHelper.deps.json', 'CelesteRecHelper.Core.dll', 'CelesteRecHelper.Core.pdb')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "src\CelesteRecHelper\bin\$Configuration\net8.0\$name") -Destination (Join-Path $stageDirectory 'bin') -Force
    }
    & $dotnetCommand publish src/CelesteRecHelper.Worker/CelesteRecHelper.Worker.csproj --configuration $Configuration --no-restore --output (Join-Path $stageDirectory 'worker') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
    if (-not $SkipDeploy) {
        $runningGame = Get-Process Celeste -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $CelesteDirectory 'Celeste.exe') }
        if ($runningGame -and (Test-Path -LiteralPath $modDirectory)) {
            throw 'Close Celeste before updating an installed mod, or use -SkipDeploy.'
        }
        if (Test-Path -LiteralPath (Join-Path $CelesteDirectory 'Mods\CelesteRecHelper.zip')) {
            throw 'Mods/CelesteRecHelper.zip already exists; remove the duplicate manually or use -SkipDeploy.'
        }
        if (Test-Path -LiteralPath $modDirectory) {
            Get-ChildItem -LiteralPath $stageDirectory | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $modDirectory -Recurse -Force
            }
        }
        else {
            # Publish a complete directory in one rename so Everest never sees a half-copied mod.
            $installDirectory = Join-Path $CelesteDirectory ('.CelesteRecHelper-install-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $installDirectory | Out-Null
            Get-ChildItem -LiteralPath $stageDirectory | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $installDirectory -Recurse -Force
            }
            $resolvedInstall = (Resolve-Path -LiteralPath $installDirectory).Path
            $expectedGame = [IO.Path]::GetFullPath($CelesteDirectory).TrimEnd('\')
            $expectedMods = [IO.Path]::GetFullPath((Join-Path $CelesteDirectory 'Mods'))
            if ((Split-Path $resolvedInstall -Parent) -ne $expectedGame -or
                (Split-Path ([IO.Path]::GetFullPath($modDirectory)) -Parent) -ne $expectedMods) {
                throw 'Install paths are outside the intended game directories.'
            }
            Move-Item -LiteralPath $resolvedInstall -Destination $modDirectory
        }
        Write-Host "Deployed to $modDirectory"
    }
    Write-Host "Build output: $stageDirectory"
}
finally { Pop-Location }
