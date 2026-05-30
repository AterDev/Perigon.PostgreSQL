[CmdletBinding()]
param(
	[string]$Version = "0.1.4"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src\Perigon.PostgreSQL\Perigon.PostgreSQL.csproj"
$outputPath = Join-Path $repoRoot "artifacts\packages"

$resolvedOutputPath = New-Item -ItemType Directory -Force -Path $outputPath

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

Get-ChildItem -Path $resolvedOutputPath.FullName -Filter "Perigon.PostgreSQL*.nupkg" |
	Sort-Object LastWriteTime -Descending |
	Select-Object -First 1 |
	ForEach-Object { Write-Host "Created package: $($_.FullName)" }
