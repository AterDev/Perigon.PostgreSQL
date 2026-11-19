[CmdletBinding()]
param(
	[string]$Version
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPaths = @(
    (Join-Path $repoRoot "src\Perigon.PostgreSQL\Perigon.PostgreSQL.csproj"),
    (Join-Path $repoRoot "src\Perigon.PostgreSQL.Tools\Perigon.PostgreSQL.Tools.csproj")
)
$outputPath = Join-Path $repoRoot "artifacts\packages"

$resolvedOutputPath = New-Item -ItemType Directory -Force -Path $outputPath

foreach ($projectPath in $projectPaths) {
    $packArgs = @(
        "pack",
        $projectPath,
        "-c",
        "Release",
        "-o",
        $resolvedOutputPath.FullName,
        "/p:ContinuousIntegrationBuild=true"
    )
    if ($Version) {
        $packArgs += "/p:PackageVersion=$Version"
    }

    dotnet @packArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for '$projectPath'."
    }
}

Get-ChildItem -Path $resolvedOutputPath.FullName -Filter "Perigon.PostgreSQL*.nupkg" |
    Sort-Object Name |
    ForEach-Object { Write-Host "Created package: $($_.FullName)" }
