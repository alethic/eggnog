<#
.SYNOPSIS
Times two builds of Eggnog.Benchmarks against each other by the method of "Comparing two builds" in README.md, and
compares them.

.DESCRIPTION
For each round and each class, runs the builds in the order baseline, candidate, candidate, baseline, one process per
class and slot, into <Campaign>/<name>/r<round>-<slot>; then runs the candidate's --compare over the campaign with the
baseline as the baseline. Writes the comparison to <Campaign>/compare.txt and exits with its exit code.

.PARAMETER Baseline
The baseline's Eggnog.Benchmarks.dll.

.PARAMETER Candidate
The candidate's Eggnog.Benchmarks.dll.

.PARAMETER Classes
The classes to time, as JSON: an object whose keys name the runs and whose values are each run's arguments, for
example {"RuleSetRunBenchmarks": ["--filter", "*.RuleSetRunBenchmarks.*"]}.

.PARAMETER Campaign
The directory the runs and the comparison write to.

.PARAMETER BaselineName
The baseline's name in the campaign and the comparison.

.PARAMETER CandidateName
The candidate's name in the campaign and the comparison.

.PARAMETER Rounds
The rounds to run.

.PARAMETER Job
A job to run every case with in place of the default, for example 'dry' to check the procedure without timing.

.PARAMETER CompareArguments
More arguments for --compare, separated by spaces, for example '--min-time 0us'.
#>
param(
    [Parameter(Mandatory = $true)][string] $Baseline,
    [Parameter(Mandatory = $true)][string] $Candidate,
    [Parameter(Mandatory = $true)][string] $Classes,
    [string] $Campaign = 'campaign',
    [string] $BaselineName = 'main',
    [string] $CandidateName = 'candidate',
    [int] $Rounds = 2,
    [string] $Job = '',
    [string] $CompareArguments = ''
)

$ErrorActionPreference = 'Stop'

$builds = @{ $BaselineName = $Baseline; $CandidateName = $Candidate }
$order = $BaselineName, $CandidateName, $CandidateName, $BaselineName
$runs = $Classes | ConvertFrom-Json -AsHashtable
$extra = if ($Job) { @('--job', $Job) } else { @() }

foreach ($round in 1..$Rounds) {
    foreach ($class in $runs.Keys) {
        for ($slot = 0; $slot -lt $order.Count; $slot++) {
            $name = $order[$slot]
            $artifacts = Join-Path $Campaign $name "r$round-$slot"
            Write-Host "round ${round}, ${class}, slot ${slot}: ${name}"
            & dotnet $builds[$name] @($runs[$class]) @extra --allStats --artifacts $artifacts
            if ($LASTEXITCODE -ne 0) {
                throw "$name failed on $class in round $round, slot $slot (exit code $LASTEXITCODE)"
            }
        }
    }
}

$compare = if ($CompareArguments.Trim()) { $CompareArguments.Trim() -split '\s+' } else { @() }
$report = Join-Path $Campaign 'compare.txt'
& dotnet $Candidate --compare $Campaign --baseline $BaselineName @compare | Tee-Object -FilePath $report
exit $LASTEXITCODE
