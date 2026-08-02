#requires -Version 7.4
#requires -PSEdition Core

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$FrameworkDependent,

    [switch]$SkipTests,

    [switch]$NoZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\publish"))
$packageName = "ssh-win-gui-win-x64"
$finalDirectory = [System.IO.Path]::GetFullPath((Join-Path $publishRoot $packageName))
$finalZip = "$finalDirectory.zip"
$stagingDirectory = [System.IO.Path]::GetFullPath((Join-Path $publishRoot ".$packageName.staging-$PID"))
$previousDirectory = [System.IO.Path]::GetFullPath((Join-Path $publishRoot ".$packageName.previous-$PID"))
$stagingZip = [System.IO.Path]::GetFullPath((Join-Path $publishRoot ".$packageName.staging-$PID.zip"))

function Assert-DirectChildPath {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$AllowedNamePattern
    )

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals(
            [System.IO.Path]::GetDirectoryName($pathFull),
            $rootFull,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to mutate a path outside the expected root: $pathFull"
    }

    $leaf = [System.IO.Path]::GetFileName($pathFull)
    if ($leaf -notmatch $AllowedNamePattern) {
        throw "Refusing to mutate an unexpected path: $pathFull"
    }
}

function Remove-VerifiedItem {
    param(
        [Parameter(Mandatory)] [string]$Root,
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$AllowedNamePattern
    )

    Assert-DirectChildPath -Root $Root -Path $Path -AllowedNamePattern $AllowedNamePattern
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$ArgumentList,
        [Parameter(Mandatory)] [string]$WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $FilePath @ArgumentList
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

function Remove-UnwantedSatelliteResources {
    param([Parameter(Mandatory)] [string]$PackageDirectory)

    $allowed = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]@("en", "zh-CN"),
        [System.StringComparer]::OrdinalIgnoreCase)
    $resourceDirectories = Get-ChildItem -LiteralPath $PackageDirectory -Directory | Where-Object {
        @(Get-ChildItem -LiteralPath $_.FullName -File -Filter "*.resources.dll").Count -gt 0
    }
    foreach ($directory in $resourceDirectories) {
        if (-not $allowed.Contains($directory.Name)) {
            Remove-VerifiedItem -Root $PackageDirectory -Path $directory.FullName -AllowedNamePattern '^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})?$'
        }
    }

    $remaining = Get-ChildItem -LiteralPath $PackageDirectory -Directory | Where-Object {
        @(Get-ChildItem -LiteralPath $_.FullName -File -Filter "*.resources.dll").Count -gt 0 -and
        -not $allowed.Contains($_.Name)
    }
    if ($remaining) {
        throw "Unexpected satellite-resource languages remain: $($remaining.Name -join ', ')"
    }
}

foreach ($commandName in @("dotnet", "go")) {
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
        throw "Required command is not available: $commandName"
    }
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
Assert-DirectChildPath -Root $publishRoot -Path $finalDirectory -AllowedNamePattern '^ssh-win-gui-win-x64$'
Assert-DirectChildPath -Root $publishRoot -Path $finalZip -AllowedNamePattern '^ssh-win-gui-win-x64\.zip$'
Assert-DirectChildPath -Root $publishRoot -Path $stagingDirectory -AllowedNamePattern '^\.ssh-win-gui-win-x64\.staging-\d+$'
Assert-DirectChildPath -Root $publishRoot -Path $previousDirectory -AllowedNamePattern '^\.ssh-win-gui-win-x64\.previous-\d+$'
Assert-DirectChildPath -Root $publishRoot -Path $stagingZip -AllowedNamePattern '^\.ssh-win-gui-win-x64\.staging-\d+\.zip$'

Remove-VerifiedItem -Root $publishRoot -Path $stagingDirectory -AllowedNamePattern '^\.ssh-win-gui-win-x64\.staging-\d+$'
Remove-VerifiedItem -Root $publishRoot -Path $previousDirectory -AllowedNamePattern '^\.ssh-win-gui-win-x64\.previous-\d+$'
Remove-VerifiedItem -Root $publishRoot -Path $stagingZip -AllowedNamePattern '^\.ssh-win-gui-win-x64\.staging-\d+\.zip$'
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

$previousGoMaxProcs = [Environment]::GetEnvironmentVariable("GOMAXPROCS", "Process")
[Environment]::SetEnvironmentVariable("GOMAXPROCS", "8", "Process")
$swapped = $false
try {
    $workerSource = Join-Path $repoRoot "src\RsyncShell.RsyncWorker"
    $vendorRsyncSource = Join-Path $repoRoot "third_party\rsync"
    if (-not $SkipTests) {
        Invoke-Checked -FilePath "go" -ArgumentList @("test", "./...") -WorkingDirectory $workerSource
        Invoke-Checked -FilePath "go" -ArgumentList @("vet", "./...") -WorkingDirectory $workerSource
        Invoke-Checked -FilePath "go" -ArgumentList @("test", "./internal/sender", "./internal/receiver") -WorkingDirectory $vendorRsyncSource
        Invoke-Checked -FilePath "go" -ArgumentList @("vet", "./internal/sender", "./internal/receiver") -WorkingDirectory $vendorRsyncSource
    }

    $solution = Join-Path $repoRoot "RsyncShell.sln"
    Invoke-Checked -FilePath "dotnet" -ArgumentList @("restore", $solution) -WorkingDirectory $repoRoot
    if (-not $SkipTests) {
        Invoke-Checked -FilePath "dotnet" -ArgumentList @("build", $solution, "-c", $Configuration, "--no-restore", "-m:4") -WorkingDirectory $repoRoot
        Invoke-Checked -FilePath "dotnet" -ArgumentList @("test", $solution, "-c", $Configuration, "--no-build", "--no-restore", "-m:4") -WorkingDirectory $repoRoot
    }

    $appProject = Join-Path $repoRoot "src\ssh-win-gui\ssh-win-gui.csproj"
    $selfContained = if ($FrameworkDependent) { "false" } else { "true" }
    Invoke-Checked -FilePath "dotnet" -ArgumentList @(
        "publish", $appProject,
        "-c", $Configuration,
        "-r", "win-x64",
        "--self-contained", $selfContained,
        "--no-restore",
        "-m:4",
        "-p:DebugSymbols=false",
        "-p:DebugType=None",
        "-o", $stagingDirectory
    ) -WorkingDirectory $repoRoot

    $workerDirectory = Join-Path $stagingDirectory "tools\rsync"
    New-Item -ItemType Directory -Path $workerDirectory -Force | Out-Null
    Invoke-Checked -FilePath "go" -ArgumentList @(
        "build", "-trimpath", "-ldflags", "-s -w", "-o", (Join-Path $workerDirectory "rsyncworker.exe"), "."
    ) -WorkingDirectory $workerSource

    Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\architecture.md") -Destination (Join-Path $stagingDirectory "ARCHITECTURE.md")
    Copy-Item -LiteralPath (Join-Path $workerSource "THIRD_PARTY_NOTICES.md") -Destination $workerDirectory
    Copy-Item -LiteralPath (Join-Path $workerSource "LICENSE") -Destination (Join-Path $workerDirectory "LICENSE")
    Copy-Item -LiteralPath (Join-Path $vendorRsyncSource "LICENSE") -Destination (Join-Path $workerDirectory "RSYNC_LICENSE")
    Copy-Item -LiteralPath (Join-Path $vendorRsyncSource "RSYNCSHELL_VENDOR.md") -Destination (Join-Path $workerDirectory "RSYNC_VENDOR.md")

    Remove-UnwantedSatelliteResources -PackageDirectory $stagingDirectory

    $checksumPath = Join-Path $stagingDirectory "SHA256SUMS.txt"
    $checksums = Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File |
        Where-Object FullName -NE $checksumPath |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = [System.IO.Path]::GetRelativePath($stagingDirectory, $_.FullName).Replace("\", "/")
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
            "$hash  $relativePath"
        }
    [System.IO.File]::WriteAllLines($checksumPath, $checksums, [System.Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $finalDirectory) {
        Move-Item -LiteralPath $finalDirectory -Destination $previousDirectory
    }
    try {
        Move-Item -LiteralPath $stagingDirectory -Destination $finalDirectory
        $swapped = $true
    }
    catch {
        if (Test-Path -LiteralPath $previousDirectory) {
            Move-Item -LiteralPath $previousDirectory -Destination $finalDirectory
        }
        throw
    }
    Remove-VerifiedItem -Root $publishRoot -Path $previousDirectory -AllowedNamePattern '^\.ssh-win-gui-win-x64\.previous-\d+$'

    if (-not $NoZip) {
        Compress-Archive -Path (Join-Path $finalDirectory "*") -DestinationPath $stagingZip -CompressionLevel Optimal
        Move-Item -LiteralPath $stagingZip -Destination $finalZip -Force
    }
    elseif (Test-Path -LiteralPath $finalZip) {
        Remove-VerifiedItem -Root $publishRoot -Path $finalZip -AllowedNamePattern '^ssh-win-gui-win-x64\.zip$'
    }

    $legacyItems = Get-ChildItem -LiteralPath $publishRoot -Force | Where-Object {
        $_.Name -match '^RsyncShell-win-x64-\d{8}(?:-\d{6})?(?:-m\d+)?(?:\.zip)?$'
    }
    foreach ($legacyItem in $legacyItems) {
        Remove-VerifiedItem -Root $publishRoot -Path $legacyItem.FullName -AllowedNamePattern '^RsyncShell-win-x64-\d{8}(?:-\d{6})?(?:-m\d+)?(?:\.zip)?$'
    }

    foreach ($oldFixedName in @("RsyncShell-win-x64", "RsyncShell-win-x64.zip")) {
        $oldFixedPath = [System.IO.Path]::GetFullPath((Join-Path $publishRoot $oldFixedName))
        Remove-VerifiedItem -Root $publishRoot -Path $oldFixedPath -AllowedNamePattern '^RsyncShell-win-x64(?:\.zip)?$'
    }

    Write-Host "Package: $finalDirectory"
    if (-not $NoZip) {
        Write-Host "Archive: $finalZip"
    }
}
finally {
    [Environment]::SetEnvironmentVariable("GOMAXPROCS", $previousGoMaxProcs, "Process")
    if (-not $swapped) {
        Remove-VerifiedItem -Root $publishRoot -Path $stagingDirectory -AllowedNamePattern '^\.ssh-win-gui-win-x64\.staging-\d+$'
    }
    Remove-VerifiedItem -Root $publishRoot -Path $stagingZip -AllowedNamePattern '^\.ssh-win-gui-win-x64\.staging-\d+\.zip$'
}
