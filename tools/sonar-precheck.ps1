<#
.SYNOPSIS
	Lists the C# issues the SonarQube Cloud PR analysis would report on this branch, without pushing.

.DESCRIPTION
	The analysis on CI (.github/workflows/build.yml) gets its C# issues from two places:
	  - Sonar's own rules (S####), which the scanner adds to the build as the SonarAnalyzer.CSharp
	    analyzer, with the rules of the project's quality profile turned on.
	  - Every other compiler diagnostic (CA####, IDE####, SYSLIB####, xUnit####...), including
	    suggestion-level ones that never show in the build output, which it imports from each
	    compilation's SARIF log.

	This script does the same locally. It reads the project's C# quality profile from SonarQube
	Cloud's public API and turns it into an analyzer config file, then builds DisplayBook.slnx with
	SonarPrecheck=true, which adds SonarAnalyzer.CSharp with that config and writes a SARIF log per
	project and target framework (see Directory.Build.props and Directory.Build.targets). The build
	goes to bin/sonar and obj/sonar in each project, so it doesn't touch the normal build output (or
	a running app's exe) and is incremental from one run to the next.

	It then lists what those logs report, the way the PR analysis would:
	  - Only on lines changed since the branch left -Base, committed or not (every line of an
	    untracked file): what SonarQube Cloud counts as new code in a PR.
	  - Not issues SonarQube Cloud already has on -Base, open or marked accepted/false positive
	    (matched by rule and file, since line numbers move).
	  - In test projects (file name containing "test"), compiler diagnostics and only the Sonar rules
	    meant for tests, such as S2699: the scanner runs no other Sonar rules on test code.
	  - Once per issue, however many target frameworks report it.

	Not covered: JavaScript, CSS and HTML (EpubText.js, the reader tests...). For those, run
	"SonarQube: Analyze Changed Files with SonarQube" in VS Code before committing; SonarQube for IDE
	is bound to the SonarQube Cloud project, so it uses the same rules.

.PARAMETER Base
	The branch the PR targets. Default: origin/main.

.PARAMETER All
	List every issue in every file: no changed-lines or known-issue filtering.

.PARAMETER Offline
	Don't call SonarQube Cloud: use SonarAnalyzer.CSharp's default rules (the "Sonar way" profile)
	and don't filter out issues already known on -Base. Also what happens if the API can't be reached.

.PARAMETER ProjectKey
	The SonarQube Cloud project. Default: ne0rrmatrix_EbpubReaderV2.

.PARAMETER Organization
	The SonarQube Cloud organization. Default: ne0rrmatrix.

.EXAMPLE
	./tools/sonar-precheck.ps1

.EXAMPLE
	./tools/sonar-precheck.ps1 -All
#>
[CmdletBinding()]
param(
	[string] $Base = 'origin/main',
	[switch] $All,
	[switch] $Offline,
	[string] $ProjectKey = 'ne0rrmatrix_EbpubReaderV2',
	[string] $Organization = 'ne0rrmatrix'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $root 'DisplayBook.slnx'
$output = Join-Path $root 'artifacts/sonar-precheck'
$globalConfig = Join-Path $output 'SonarQubeCloud.globalconfig'
$api = 'https://sonarcloud.io/api'

function Get-RelativePath([string] $path) {
	[IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
}

# Every page of a SonarQube Cloud search (500 per page, the API's maximum).
function Get-SonarPages([string] $path, [string] $query, [string] $items) {
	$page = 1
	do {
		$response = Invoke-RestMethod "$api/$path`?$query&ps=500&p=$page" -TimeoutSec 30
		$response.$items
		# issues/search reports the total under "paging", rules/search at the top level.
		$total = if ($response.PSObject.Properties['paging']) { $response.paging.total } else { $response.total }
		$fetched = $page * 500
		$page++
	} while ($fetched -lt $total)
}

# The rule key without its repository ("csharpsquid:S2077" -> S2077, "external_roslyn:CA1873" -> CA1873).
function Get-RuleId([string] $key) {
	$key.Substring($key.IndexOf(':') + 1)
}

# Writes the analyzer config the precheck build always gets. It is a compiler input, so it is
# rewritten only when its content changes: then (and only then) every project compiles again, which
# is also what makes switching between -Offline and the quality profile take effect.
function Write-GlobalConfig([string[]] $lines) {
	$content = ((@('is_global = true') + $lines) -join "`n") + "`n"
	New-Item -ItemType Directory -Force $output | Out-Null
	if (-not (Test-Path $globalConfig) -or (Get-Content $globalConfig -Raw) -ne $content) {
		Set-Content $globalConfig $content -NoNewline
	}
}

# The quality profile's Sonar rules on and the rest off, as the scanner sets them.
function Get-ProfileConfig {
	$profiles = (Invoke-RestMethod "$api/qualityprofiles/search?project=$ProjectKey&organization=$Organization" -TimeoutSec 30).profiles
	$profile = $profiles | Where-Object language -EQ 'cs' | Select-Object -First 1
	if (-not $profile) {
		throw "SonarQube Cloud returned no C# quality profile for $ProjectKey."
	}

	$active = [Collections.Generic.HashSet[string]]::new()
	Get-SonarPages 'rules/search' "organization=$Organization&qprofile=$($profile.key)&activation=true&repositories=csharpsquid&f=name" 'rules' |
		ForEach-Object { [void]$active.Add((Get-RuleId $_.key)) }
	$rules = Get-SonarPages 'rules/search' "organization=$Organization&repositories=csharpsquid&f=name" 'rules' |
		ForEach-Object { Get-RuleId $_.key } | Sort-Object { [int]$_.Substring(1) }

	Write-Host "Quality profile: $($profile.name) ($($active.Count) C# rules)."
	"# Generated by tools/sonar-precheck.ps1 from the SonarQube Cloud quality profile `"$($profile.name)`"."
	$rules | ForEach-Object { "dotnet_diagnostic.$_.severity = $(if ($active.Contains($_)) { 'warning' } else { 'none' })" }
}

# Issues SonarQube Cloud already has on the target branch, open or accepted/false positive, counted
# per rule and file. A PR analysis doesn't report those again.
function Get-KnownIssues([string] $branch) {
	$known = @{}
	Get-SonarPages 'issues/search' "componentKeys=$ProjectKey&branch=$branch&issueStatuses=OPEN,CONFIRMED,ACCEPTED,FALSE_POSITIVE" 'issues' |
		ForEach-Object {
			$key = "$(Get-RuleId $_.rule)|$($_.component.Substring($_.component.IndexOf(':') + 1))"
			$known[$key] = 1 + ($known[$key] ?? 0)
		}
	$known
}

$known = @{}
$config = @('# Offline: SonarAnalyzer.CSharp''s default rules.')
if (-not $Offline) {
	try {
		$config = @(Get-ProfileConfig)
		if (-not $All) {
			$known = Get-KnownIssues ($Base -replace '^origin/', '')
		}
	}
	catch {
		Write-Warning "Could not read from SonarQube Cloud ($($_.Exception.Message)); carrying on as with -Offline."
		$config = @('# Offline: SonarAnalyzer.CSharp''s default rules.')
		$known = @{}
	}
}

Write-GlobalConfig $config
$buildArguments = @($solution, '-c', 'Debug', '-p:SonarPrecheck=true', "-p:SonarPrecheckGlobalConfig=$globalConfig", '-nologo', '-v:q')

Write-Host 'Building DisplayBook.slnx for the precheck (bin/sonar, obj/sonar)...'
$build = & dotnet build @buildArguments 2>&1
if ($LASTEXITCODE -ne 0) {
	$build | Where-Object { $_ -match ': error ' } | Select-Object -Unique | ForEach-Object { Write-Host $_ }
	throw 'The build failed; fix the errors above first.'
}

# Changed lines per file, from where the branch left -Base to the working tree. An untracked file
# maps to $null: every line is new.
$changed = $null
if (-not $All) {
	$mergeBase = git -C $root merge-base HEAD $Base
	if ($LASTEXITCODE -ne 0) {
		throw "Could not find where this branch left $Base. Fetch it, or pass -Base."
	}

	$changed = [Collections.Generic.Dictionary[string, Collections.Generic.HashSet[int]]]::new([StringComparer]::OrdinalIgnoreCase)
	$file = $null
	foreach ($line in git -C $root diff --unified=0 --no-color --no-ext-diff $mergeBase) {
		if ($line -match '^\+\+\+ (?:b/(.+)|/dev/null)$') {
			$file = $Matches[1]
			if ($file -and -not $changed.ContainsKey($file)) {
				$changed[$file] = [Collections.Generic.HashSet[int]]::new()
			}
		}
		elseif ($file -and $line -match '^@@ -\S+ \+(\d+)(?:,(\d+))? @@') {
			$start = [int]$Matches[1]
			$count = if ($Matches[2]) { [int]$Matches[2] } else { 1 }
			for ($n = $start; $n -lt $start + $count; $n++) {
				[void]$changed[$file].Add($n)
			}
		}
	}

	git -C $root ls-files --others --exclude-standard | ForEach-Object { $changed[$_] = $null }
}

# The scanner treats a project whose file name contains "test" as test code
# (sonar.msbuild.testProjectPattern) and runs only the Sonar rules meant for tests there.
function Test-ReportedInTestCode([string] $ruleId, [string[]] $tags) {
	$ruleId -notmatch '^S\d+$' -or ('TestSourceScope' -in $tags -and 'MainSourceScope' -notin $tags)
}

$logs = Get-ChildItem $root -Recurse -Filter *.sarif -File |
	Where-Object { $_.FullName -match '[\\/]obj[\\/]sonar[\\/]' }
if (-not $logs) {
	throw 'The build wrote no SARIF logs; check SonarPrecheck in Directory.Build.targets.'
}

$issues = [ordered]@{}
foreach ($log in $logs) {
	$sarif = Get-Content $log.FullName -Raw | ConvertFrom-Json
	$isTestProject = $log.BaseName -match 'test'
	foreach ($run in $sarif.runs) {
		$tags = @{}
		if ($run.tool.driver.PSObject.Properties['rules']) {
			foreach ($rule in $run.tool.driver.rules) {
				$tags[$rule.id] = if ($rule.PSObject.Properties['properties'] -and $rule.properties.PSObject.Properties['tags']) { [string[]]$rule.properties.tags } else { @() }
			}
		}

		foreach ($result in @($run.results)) {
			if (-not $result -or ($result.PSObject.Properties['suppressions'] -and $result.suppressions)) {
				continue
			}

			if ($isTestProject -and -not (Test-ReportedInTestCode $result.ruleId $tags[$result.ruleId])) {
				continue
			}

			$file = ''
			$line = 0
			if ($result.PSObject.Properties['locations'] -and $result.locations) {
				$location = $result.locations[0].physicalLocation
				$file = Get-RelativePath ([Uri]$location.artifactLocation.uri).LocalPath
				if ($location.PSObject.Properties['region']) {
					$line = $location.region.startLine
				}
			}

			# Generated code (obj/) isn't analyzed by SonarQube Cloud.
			if ($file -match '(^|/)(obj|bin)/' -or $file.StartsWith('..')) {
				continue
			}

			if ($null -ne $changed) {
				if (-not $file -or -not $changed.ContainsKey($file)) {
					continue
				}

				$lines = $changed[$file]
				if ($null -ne $lines -and $line -and -not $lines.Contains($line)) {
					continue
				}
			}

			$message = $result.message.text
			$key = "$($result.ruleId)|$file|$line|$message"
			if (-not $issues.Contains($key)) {
				$issues[$key] = [pscustomobject]@{ Rule = $result.ruleId; File = $file; Line = $line; Message = $message }
			}
		}
	}
}

# Drop as many issues per rule and file as the target branch already has there.
$found = @($issues.Values | Sort-Object File, Line | Where-Object {
		$key = "$($_.Rule)|$($_.File)"
		if ($known[$key] -gt 0) {
			$known[$key]--
			$false
		}
		else {
			$true
		}
	})

$scope = if ($All) { 'the solution' } else { "lines changed since $Base" }
$reminder = 'For JavaScript, CSS and HTML, run "SonarQube: Analyze Changed Files with SonarQube" in VS Code.'
if ($found.Count -eq 0) {
	Write-Host "No C# issues in $scope."
	Write-Host $reminder
	exit 0
}

foreach ($group in $found | Group-Object File) {
	Write-Host ''
	Write-Host $(if ($group.Name) { $group.Name } else { '(no file)' })
	foreach ($issue in $group.Group) {
		Write-Host ("  {0,5}  {1,-10} {2}" -f $issue.Line, $issue.Rule, $issue.Message)
	}
}

Write-Host ''
Write-Host "$($found.Count) C# issue(s) in ${scope}:"
$found | Group-Object Rule | Sort-Object Count -Descending | ForEach-Object { Write-Host ("  {0,4}  {1}" -f $_.Count, $_.Name) }
Write-Host $reminder
exit 1
