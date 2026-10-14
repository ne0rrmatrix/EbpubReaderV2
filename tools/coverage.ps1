<#
.SYNOPSIS
	Runs the unit tests and the Windows device tests under dotnet-coverage and writes one merged
	coverage report for SonarCloud.

.DESCRIPTION
	Output, under artifacts/coverage/:
	  coverage.xml          merged report, Visual Studio XML format (pass to SonarCloud as
	                        sonar.cs.vscoveragexml.reportsPaths)
	  unit.xml              DisplayBook.Tests only
	  device-windows.xml    DisplayBook.DeviceTests on Windows only

	The device tests run by calling the DeviceRunners CLI directly (the same command that
	"dotnet test -f net10.0-windows10.0.19041.0" runs), so dotnet-coverage profiles the CLI and the
	test app it starts rather than MSBuild. Coverage from Android/iOS/Mac Catalyst device runs is not
	collected; their platform-specific code is not part of a Windows analysis anyway.

.PARAMETER SkipDeviceTests
	Only run the unit tests.

.EXAMPLE
	./tools/coverage.ps1
#>
[CmdletBinding()]
param(
	[switch] $SkipDeviceTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$settings = Join-Path $PSScriptRoot 'dotnet-coverage.settings.xml'
$output = Join-Path $root 'artifacts/coverage'
$unitProject = Join-Path $root 'tests/DisplayBook.Tests/DisplayBook.Tests.csproj'
$deviceProject = Join-Path $root 'tests/DisplayBook.DeviceTests/DisplayBook.DeviceTests.csproj'
$windowsFramework = 'net10.0-windows10.0.19041.0'
$configuration = 'Debug'

function Invoke-Checked([string] $description, [scriptblock] $command) {
	Write-Host "==> $description" -ForegroundColor Cyan
	& $command
	if ($LASTEXITCODE -ne 0) {
		throw "$description failed with exit code $LASTEXITCODE."
	}
}

Push-Location $root
try {
	Invoke-Checked 'Restoring local tools' { dotnet tool restore }
	if (Test-Path $output) {
		Remove-Item $output -Recurse -Force
	}
	New-Item -ItemType Directory -Path $output | Out-Null
	$reports = @()

	$unitReport = Join-Path $output 'unit.xml'
	Invoke-Checked 'Building unit tests' { dotnet build $unitProject -c $configuration }
	Invoke-Checked 'Running unit tests under coverage' {
		dotnet dotnet-coverage collect --settings $settings --output-format xml --output $unitReport `
			"dotnet test `"$unitProject`" -c $configuration --no-build"
	}
	$reports += $unitReport

	if (-not $SkipDeviceTests) {
		$deviceReport = Join-Path $output 'device-windows.xml'
		$deviceResults = Join-Path $output 'device-test-results'
		Invoke-Checked 'Building Windows device tests' { dotnet build $deviceProject -f $windowsFramework -c $configuration }

		# Ask MSBuild for the DeviceRunners CLI and the built app rather than hard-coding package paths.
		$properties = dotnet msbuild $deviceProject -p:TargetFramework=$windowsFramework -p:Configuration=$configuration `
			-getProperty:_DeviceRunnersCommand -getProperty:TargetPath | Out-String | ConvertFrom-Json
		$cli = $properties.Properties._DeviceRunnersCommand
		$app = [IO.Path]::ChangeExtension($properties.Properties.TargetPath, '.exe')
		if (-not (Test-Path $cli)) { throw "DeviceRunners CLI not found at '$cli'." }
		if (-not (Test-Path $app)) { throw "Device-test app not found at '$app'." }

		Invoke-Checked 'Running Windows device tests under coverage' {
			dotnet dotnet-coverage collect --settings $settings --output-format xml --output $deviceReport `
				"`"$cli`" windows test --app `"$app`" --results-directory `"$deviceResults`" --logger trx"
		}
		$reports += $deviceReport
	}

	$merged = Join-Path $output 'coverage.xml'
	Invoke-Checked 'Merging coverage' {
		dotnet dotnet-coverage merge --output-format xml --output $merged @reports
	}

	# Line totals, so a run that silently collected nothing is obvious.
	[xml] $report = Get-Content $merged
	$modules = @($report.results.modules.module)
	$covered = ($modules | Measure-Object -Property lines_covered -Sum).Sum
	$partial = ($modules | Measure-Object -Property lines_partially_covered -Sum).Sum
	$notCovered = ($modules | Measure-Object -Property lines_not_covered -Sum).Sum
	$total = $covered + $partial + $notCovered
	if ($total -eq 0) {
		throw 'The merged report contains no app code. Check tools/dotnet-coverage.settings.xml.'
	}

	Write-Host ''
	Write-Host ('Line coverage: {0:P1} ({1} of {2} lines, {3} partially)' -f (($covered + $partial) / $total), $covered, $total, $partial) -ForegroundColor Green
	Write-Host "Report: $merged"
}
finally {
	Pop-Location
}
