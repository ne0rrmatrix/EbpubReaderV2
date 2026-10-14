<#
.SYNOPSIS
	Runs the unit tests and the Windows device tests under dotnet-coverage, and the reader's browser
	tests under Chromium's JavaScript coverage, and writes the reports SonarCloud reads.

.DESCRIPTION
	Output, under artifacts/coverage/:
	  coverage.xml          merged report, Visual Studio XML format (pass to SonarCloud as
	                        sonar.cs.vscoveragexml.reportsPaths)
	  unit.xml              DisplayBook.Tests only
	  device-windows-*.xml  DisplayBook.DeviceTests on Windows only, one per batch (see below)
	  reader-js/lcov.info   EpubText.js, from tests/DisplayBook.ReaderTests (pass to SonarCloud as
	                        sonar.javascript.lcov.reportPaths)

	The device tests run by calling the DeviceRunners CLI directly (the same command that
	"dotnet test -f net10.0-windows10.0.19041.0" runs), so dotnet-coverage profiles the CLI and the
	test app it starts rather than MSBuild. They run in batches, each in its own app process. When the
	code runs slower (under the profiler, or instrumented), the page tests intermittently hit a
	native access violation in WinUI (Microsoft.UI.Xaml.Controls.dll); a full run without it passes.
	So a batch whose app crashes part way is run again, up to three times, and coverage from every
	attempt (the profiler keeps what it collected before the crash) is merged. If it crashes every
	time, the batch is run once more without coverage to prove its tests pass, with a warning.
	A batch with a failing test fails the run straight away. Coverage from Android/iOS/Mac Catalyst device runs is not
	collected; their platform-specific code is not part of a Windows analysis anyway.

.PARAMETER SkipDeviceTests
	Skip the Windows device tests.

.PARAMETER SkipReaderTests
	Skip the reader's browser tests (they need Node.js).

.EXAMPLE
	./tools/coverage.ps1
#>
[CmdletBinding()]
param(
	[switch] $SkipDeviceTests,
	[switch] $SkipReaderTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$settings = Join-Path $PSScriptRoot 'dotnet-coverage.settings.xml'
$output = Join-Path $root 'artifacts/coverage'
$unitProject = Join-Path $root 'tests/DisplayBook.Tests/DisplayBook.Tests.csproj'
$deviceProject = Join-Path $root 'tests/DisplayBook.DeviceTests/DisplayBook.DeviceTests.csproj'
$readerTests = Join-Path $root 'tests/DisplayBook.ReaderTests'
$windowsFramework = 'net10.0-windows10.0.19041.0'
$configuration = 'Debug'

# True when a DeviceRunners run finished (its event stream ends with "end"); throws when a test failed.
function Test-DeviceRun([string] $batch, [string] $resultsDirectory) {
	$trx = Get-ChildItem $resultsDirectory -Filter *.trx -ErrorAction SilentlyContinue | Select-Object -First 1
	if ($trx) {
		[xml] $results = Get-Content $trx.FullName
		$failed = [int]$results.TestRun.ResultSummary.Counters.failed
		if ($failed -gt 0) {
			throw "$failed $batch device test(s) failed; see $($trx.FullName)."
		}
	}

	$events = Join-Path $resultsDirectory 'tcp-test-events.jsonl'
	return (Test-Path $events) -and [bool](Select-String -Path $events -Pattern '"type":"end"' -SimpleMatch -Quiet)
}

function Invoke-Checked {
	# Its job is to show progress in the console, so Write-Host is what it should use.
	[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Console progress output.')]
	param([string] $description, [scriptblock] $command)

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
		Invoke-Checked 'Building Windows device tests' { dotnet build $deviceProject -f $windowsFramework -c $configuration }

		# Ask MSBuild for the DeviceRunners CLI and the built app rather than hard-coding package paths.
		$properties = dotnet msbuild $deviceProject -p:TargetFramework=$windowsFramework -p:Configuration=$configuration `
			-getProperty:_DeviceRunnersCommand -getProperty:TargetPath | Out-String | ConvertFrom-Json
		$cli = $properties.Properties._DeviceRunnersCommand
		$app = [IO.Path]::ChangeExtension($properties.Properties.TargetPath, '.exe')
		if (-not (Test-Path $cli)) { throw "DeviceRunners CLI not found at '$cli'." }
		if (-not (Test-Path $app)) { throw "Device-test app not found at '$app'." }

		# Every test is in exactly one batch.
		$pages = 'DisplayBook.DeviceTests.Tests.Pages.'
		$batches = [ordered]@{
			'services' = "FullyQualifiedName!~$pages"
			'reader'   = "FullyQualifiedName~${pages}ReaderPageTests"
			'library'  = "FullyQualifiedName~${pages}LibraryPageTests"
			'pages'    = "FullyQualifiedName~$pages&FullyQualifiedName!~${pages}ReaderPageTests&FullyQualifiedName!~${pages}LibraryPageTests"
		}
		foreach ($batch in $batches.GetEnumerator()) {
			$completed = $false
			for ($attempt = 1; $attempt -le 3 -and -not $completed; $attempt++) {
				$deviceReport = Join-Path $output "device-windows-$($batch.Key)-$attempt.xml"
				$deviceResults = Join-Path $output "device-test-results/$($batch.Key)-$attempt"
				Write-Host "==> Running Windows device tests under coverage ($($batch.Key), attempt $attempt)" -ForegroundColor Cyan
				dotnet dotnet-coverage collect --settings $settings --output-format xml --output $deviceReport `
					"`"$cli`" windows test --app `"$app`" --results-directory `"$deviceResults`" --logger trx --data-timeout 300 --filter `"$($batch.Value)`"" |
					Select-String -Pattern '^\s*\[FAIL\]|Results: Total=' | ForEach-Object { Write-Host $_.Line }
				if (Test-Path $deviceReport) {
					$reports += $deviceReport
				}
				$completed = Test-DeviceRun $batch.Key $deviceResults
				if (-not $completed) {
					Write-Warning "The device-test app crashed during the $($batch.Key) tests (attempt $attempt)."
				}
			}

			if (-not $completed) {
				$deviceResults = Join-Path $output "device-test-results/$($batch.Key)-without-coverage"
				Write-Host "==> Running Windows device tests without coverage ($($batch.Key))" -ForegroundColor Cyan
				& $cli windows test --app $app --results-directory $deviceResults --logger trx --data-timeout 300 --filter $batch.Value |
					Select-String -Pattern '^\s*\[FAIL\]|Results: Total=' | ForEach-Object { Write-Host $_.Line }
				if (-not (Test-DeviceRun $batch.Key $deviceResults)) {
					throw "The device-test app crashed during the $($batch.Key) tests even without coverage."
				}
				Write-Warning "The $($batch.Key) tests pass, but crashed under coverage every time; their coverage is partial."
			}
		}
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

	if (-not $SkipReaderTests) {
		Push-Location $readerTests
		try {
			Invoke-Checked 'Installing reader test packages' { npm ci --no-audit --no-fund }
			Invoke-Checked 'Installing Chromium for the reader tests' { npx playwright install chromium }
			Invoke-Checked 'Running reader tests under coverage' { npx playwright test }
		}
		finally {
			Pop-Location
		}
	}

	Write-Host ''
	Write-Host ('Line coverage: {0:P1} ({1} of {2} lines, {3} partially)' -f (($covered + $partial) / $total), $covered, $total, $partial) -ForegroundColor Green
	Write-Host "Report: $merged"
	$readerReport = Join-Path $output 'reader-js/lcov.info'
	if (Test-Path $readerReport) {
		$lines = @(Get-Content $readerReport | Where-Object { $_ -like 'DA:*' })
		$hit = @($lines | Where-Object { $_ -notmatch ',0$' }).Count
		Write-Host ('EpubText.js line coverage: {0:P1} ({1} of {2} lines)' -f ($hit / [math]::Max(1, $lines.Count)), $hit, $lines.Count) -ForegroundColor Green
		Write-Host "Report: $readerReport"
	}
}
finally {
	Pop-Location
}
