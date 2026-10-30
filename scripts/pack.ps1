[CmdletBinding()]
param(
	[string]$Version = "0.2.2"
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
        "/p:PackageVersion=$Version",
        "/p:ContinuousIntegrationBuild=true"
    )

    dotnet @packArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for '$projectPath'."
    }
}

Get-ChildItem -Path $resolvedOutputPath.FullName -Filter "Perigon.PostgreSQL*.nupkg" |
    Sort-Object Name |
    ForEach-Object { Write-Host "Created package: $($_.FullName)" }
