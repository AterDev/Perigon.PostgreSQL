[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [double]$LineThreshold = 75,

    [double]$BranchThreshold = 60,

    [string]$DiffBase = "",

    [double]$DiffLineThreshold = 80
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Normalize-PathKey([string]$path) {
    $resolvedPath = if ([IO.Path]::IsPathRooted($path)) {
        $path
    }
    else {
        Join-Path $repoRoot $path
    }

    return [IO.Path]::GetFullPath($resolvedPath).Replace('\', '/').ToLowerInvariant()
}

function Get-CoverageMetrics([object[]]$selectedClasses) {
    $totalLines = 0
    $coveredLines = 0
    $totalBranches = 0
    $coveredBranches = 0
    foreach ($class in $selectedClasses) {
        foreach ($line in $class.Lines.Values) {
            $totalLines++
            if ($line.Hits -gt 0) {
                $coveredLines++
            }

            $totalBranches += $line.BranchTotal
            $coveredBranches += [Math]::Min($line.BranchCovered, $line.BranchTotal)
        }
    }

    [pscustomobject]@{
        TotalLines = $totalLines
        CoveredLines = $coveredLines
        LineRate = if ($totalLines -eq 0) { 0 } else { 100 * $coveredLines / $totalLines }
        TotalBranches = $totalBranches
        CoveredBranches = $coveredBranches
        BranchRate = if (-not $totalBranches) { 100 } else { 100 * $coveredBranches / $totalBranches }
    }
}

$reports = @(Get-ChildItem -LiteralPath $InputPath -Recurse -File -Filter "coverage.cobertura.xml")
if ($reports.Count -eq 0) {
    throw "No Cobertura reports were found below '$InputPath'."
}

$classes = @{}
foreach ($report in $reports) {
    [xml]$document = Get-Content -LiteralPath $report.FullName -Raw
    foreach ($class in @($document.coverage.packages.package.classes.class)) {
        $className = [string]$class.name
        $fileName = [string]$class.filename
        $classKey = "$fileName|$className"
        if (-not $classes.ContainsKey($classKey)) {
            $classes[$classKey] = [ordered]@{
                Name = $className
                FileName = $fileName
                Lines = @{}
            }
        }

        foreach ($line in @($class.lines.line)) {
            $lineNumber = [int]$line.number
            $hits = [int]$line.hits
            if (-not $classes[$classKey].Lines.ContainsKey($lineNumber)) {
                $classes[$classKey].Lines[$lineNumber] = [ordered]@{
                    Number = $lineNumber
                    Hits = $hits
                    BranchCovered = 0
                    BranchTotal = 0
                }
            }

            $mergedLine = $classes[$classKey].Lines[$lineNumber]
            $mergedLine.Hits = [Math]::Max($mergedLine.Hits, $hits)
            $conditionCoverage = $line.Attributes | Where-Object Name -eq "condition-coverage" | Select-Object -First 1
            if ($null -ne $conditionCoverage -and $conditionCoverage.Value -match '\((\d+)\s*/\s*(\d+)\)') {
                $mergedLine.BranchCovered = [Math]::Max($mergedLine.BranchCovered, [int]$Matches[1])
                $mergedLine.BranchTotal = [Math]::Max($mergedLine.BranchTotal, [int]$Matches[2])
            }
        }
    }
}

$totalLines = 0
$coveredLines = 0
$totalBranches = 0
$coveredBranches = 0
foreach ($class in $classes.Values) {
    foreach ($line in $class.Lines.Values) {
        $totalLines++
        if ($line.Hits -gt 0) {
            $coveredLines++
        }

        $totalBranches += $line.BranchTotal
        $coveredBranches += [Math]::Min($line.BranchCovered, $line.BranchTotal)
    }
}

$lineRate = if ($totalLines -eq 0) { 0 } else { 100 * $coveredLines / $totalLines }
$branchRate = if ($totalBranches -eq 0) { 100 } else { 100 * $coveredBranches / $totalBranches }

$coreRoot = Normalize-PathKey (Join-Path $repoRoot "src\Perigon.PostgreSQL") + "/"
$sourceGenerationRoot = Normalize-PathKey (Join-Path $repoRoot "src\Perigon.PostgreSQL.SourceGeneration") + "/"
$coreClasses = @($classes.Values | Where-Object { (Normalize-PathKey $_.FileName).StartsWith($coreRoot, [StringComparison]::Ordinal) })
$sourceGenerationClasses = @($classes.Values | Where-Object { (Normalize-PathKey $_.FileName).StartsWith($sourceGenerationRoot, [StringComparison]::Ordinal) })
$coreMetrics = Get-CoverageMetrics $coreClasses
$sourceGenerationMetrics = Get-CoverageMetrics $sourceGenerationClasses

$changedLineKeys = @{}
if (-not [string]::IsNullOrWhiteSpace($DiffBase)) {
    $diffLines = @(& git -C $repoRoot diff --unified=0 "$DiffBase...HEAD" -- '*.cs')
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read git diff from '$DiffBase'."
    }

    $currentFile = $null
    foreach ($diffLine in $diffLines) {
        if ($diffLine -match '^\+\+\+ b/(.+)$') {
            $currentFile = Normalize-PathKey (Join-Path $repoRoot $Matches[1])
            continue
        }

        if ($null -eq $currentFile -or $diffLine -notmatch '^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@') {
            continue
        }

        $startLine = [int]$Matches[1]
        $lineCount = if ([string]::IsNullOrEmpty($Matches[2])) { 1 } else { [int]$Matches[2] }
        for ($lineOffset = 0; $lineOffset -lt $lineCount; $lineOffset++) {
            $changedLineKeys["$currentFile|$($startLine + $lineOffset)"] = $true
        }
    }
}

$diffLinesByFile = @{}
foreach ($class in $classes.Values) {
    $fileKey = Normalize-PathKey $class.FileName
    foreach ($line in $class.Lines.Values) {
        $key = "$fileKey|$($line.Number)"
        if (-not $changedLineKeys.ContainsKey($key)) {
            continue
        }

        if (-not $diffLinesByFile.ContainsKey($key)) {
            $diffLinesByFile[$key] = $line.Hits -gt 0
        }
        else {
            $diffLinesByFile[$key] = $diffLinesByFile[$key] -or $line.Hits -gt 0
        }
    }
}

$diffTotalLines = $diffLinesByFile.Count
$diffCoveredLines = @($diffLinesByFile.GetEnumerator() | Where-Object Value).Count
$diffLineRate = if ($diffTotalLines -eq 0) { 100 } else { 100 * $diffCoveredLines / $diffTotalLines }

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$merged = New-Object System.Xml.XmlDocument
$merged.AppendChild($merged.CreateXmlDeclaration("1.0", "utf-8", $null)) | Out-Null
$coverage = $merged.CreateElement("coverage")
$coverage.SetAttribute("line-rate", ($lineRate / 100).ToString("0.0000", [Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("branch-rate", ($branchRate / 100).ToString("0.0000", [Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("lines-covered", $coveredLines.ToString([Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("lines-valid", $totalLines.ToString([Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("branches-covered", $coveredBranches.ToString([Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("branches-valid", $totalBranches.ToString([Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("complexity", "0")
$coverage.SetAttribute("version", "merged")
$merged.AppendChild($coverage) | Out-Null

$packages = $merged.CreateElement("packages")
$coverage.AppendChild($packages) | Out-Null
$package = $merged.CreateElement("package")
$package.SetAttribute("name", "Perigon.PostgreSQL")
$package.SetAttribute("line-rate", ($lineRate / 100).ToString("0.0000", [Globalization.CultureInfo]::InvariantCulture))
$package.SetAttribute("branch-rate", ($branchRate / 100).ToString("0.0000", [Globalization.CultureInfo]::InvariantCulture))
$classesElement = $merged.CreateElement("classes")
$package.AppendChild($classesElement) | Out-Null
$packages.AppendChild($package) | Out-Null

foreach ($class in $classes.Values | Sort-Object FileName, Name) {
    $classElement = $merged.CreateElement("class")
    $classElement.SetAttribute("name", $class.Name)
    $classElement.SetAttribute("filename", $class.FileName)
    $classElement.SetAttribute("line-rate", "0")
    $classElement.SetAttribute("branch-rate", "0")
    $linesElement = $merged.CreateElement("lines")
    $classElement.AppendChild($linesElement) | Out-Null

    foreach ($line in $class.Lines.Values | Sort-Object Number) {
        $lineElement = $merged.CreateElement("line")
        $lineElement.SetAttribute("number", $line.Number.ToString([Globalization.CultureInfo]::InvariantCulture))
        $lineElement.SetAttribute("hits", $line.Hits.ToString([Globalization.CultureInfo]::InvariantCulture))
        if ($line.BranchTotal -gt 0) {
            $lineElement.SetAttribute("branch", "true")
            $branchPercent = 100 * $line.BranchCovered / $line.BranchTotal
            $lineElement.SetAttribute("condition-coverage", "$($branchPercent.ToString('0.00', [Globalization.CultureInfo]::InvariantCulture))% ($($line.BranchCovered)/$($line.BranchTotal))")
        }

        $linesElement.AppendChild($lineElement) | Out-Null
    }

    $classesElement.AppendChild($classElement) | Out-Null
}

$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$merged.Save($resolvedOutputPath)
Write-Host ("Merged {0} Cobertura report(s): line {1:N2}% (gate {2:N2}%), branch {3:N2}% (gate {4:N2}%)." -f $reports.Count, $lineRate, $LineThreshold, $branchRate, $BranchThreshold)
Write-Host ("Core library: line {0:N2}% (gate {1:N2}%), branch {2:N2}% (gate {3:N2}%)." -f $coreMetrics.LineRate, $LineThreshold, $coreMetrics.BranchRate, $BranchThreshold)
Write-Host ("SourceGeneration: line {0:N2}% (gate 90.00%), branch {1:N2}% (gate 70.00%)." -f $sourceGenerationMetrics.LineRate, $sourceGenerationMetrics.BranchRate)
if (-not [string]::IsNullOrWhiteSpace($DiffBase)) {
    Write-Host ("Changed instrumented lines from {0}: {1:N2}% (gate {2:N2}%)." -f $DiffBase, $diffLineRate, $DiffLineThreshold)
}

if ($lineRate -lt $LineThreshold -or $branchRate -lt $BranchThreshold -or
    $coreMetrics.LineRate -lt $LineThreshold -or $coreMetrics.BranchRate -lt $BranchThreshold) {
    throw "Coverage gate failed. Required line >= $LineThreshold%, branch >= $BranchThreshold%."
}

if ($sourceGenerationClasses.Count -eq 0 -or
    $sourceGenerationMetrics.LineRate -lt 90 -or $sourceGenerationMetrics.BranchRate -lt 70) {
    throw "SourceGeneration coverage gate failed. Required line >= 90%, branch >= 70%."
}

if (-not [string]::IsNullOrWhiteSpace($DiffBase) -and $diffLineRate -lt $DiffLineThreshold) {
    throw "Diff coverage gate failed. Required changed line coverage >= $DiffLineThreshold%."
}
