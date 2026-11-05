<#
.SYNOPSIS
    Backfills Liquipedia match data from 2023-01-01 (or -From) to yesterday into a local rankings.db,
    archiving every day locally and in B2.

.DESCRIPTION
    Loads the credentials from .env at the repository root, then runs Rankings.Job backfill with --wait,
    so it waits for Liquipedia's hourly budget of 60 calls instead of stopping. Every call is logged with
    its place in the hourly budget, and so is every wait for the limit and every finished month. The
    output goes to the console and to logs\backfill-<time>.log.

    It can be stopped at any time: run it again and it carries on after the last finished month.
    While it runs, it keeps Windows from going to sleep.

.EXAMPLE
    .\scripts\backfill.ps1

.EXAMPLE
    .\scripts\backfill.ps1 -From 2025-01-01 -To 2025-12-31 -Restart
#>
[CmdletBinding()]
param(
    # First day to fetch, YYYY-MM-DD. Default: 2023-01-01, or the day after the last finished month.
    [string]$From,
    # Last day to fetch, YYYY-MM-DD. Default: yesterday (UTC).
    [string]$To,
    # Ignore the saved progress and start at -From.
    [switch]$Restart,
    # The database to fill, relative to the repository root.
    [string]$Database = "rankings.db",
    # Archive locally only, not in B2. Full recalculations on GitHub read B2, so only for testing.
    [switch]$SkipB2
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repositoryRoot

# Credentials from .env, into this session only
$envFile = Join-Path $repositoryRoot ".env"
if (-not (Test-Path $envFile)) {
    throw "No .env at $envFile. Create it with the Liquipedia and B2 credentials first."
}
Get-Content $envFile | Where-Object { $_ -match '^\s*[^#\s]' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    Set-Item "env:$($name.Trim())" $value.Trim()
}

$required = @("LIQUIPEDIA_API_KEY", "LIQUIPEDIA_USER_AGENT")
if (-not $SkipB2) { $required += @("B2_KEY_ID", "B2_APPLICATION_KEY", "B2_ENDPOINT", "B2_BUCKET") }
foreach ($name in $required) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value) -or $value -like "dummy*" -or $value -like "*example.com*" -or $value -match '0{32}') {
        throw "$name in .env is missing or still a dummy value."
    }
}

$arguments = @("backfill", "--wait", "--db", $Database)
if ($From) { $arguments += @("--from", $From) }
if ($To) { $arguments += @("--to", $To) }
if ($Restart) { $arguments += "--restart" }
if (-not $SkipB2) { $arguments += "--b2" }

$logDirectory = Join-Path $repositoryRoot "logs"
New-Item -ItemType Directory -Force $logDirectory | Out-Null
$logFile = Join-Path $logDirectory ("backfill-{0}.log" -f (Get-Date -Format "yyyy-MM-dd_HHmmss"))

function Write-Log([string]$line) {
    Write-Host $line
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

# Keep the machine awake while the backfill waits for the hourly budget.
Add-Type -Namespace Rankings -Name Power -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern uint SetThreadExecutionState(uint flags);
'@
$keepAwake = [uint32]"0x80000001"   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED
$allowSleep = [uint32]"0x80000000"  # ES_CONTINUOUS

Write-Log ("{0:yyyy-MM-dd HH:mm:ss} UTC  Building the job; logging to {1}" -f (Get-Date).ToUniversalTime(), $logFile)
& dotnet build src/Rankings.Job -c Release --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The build failed; run 'dotnet build src/Rankings.Job' to see why." }

$exitCode = 1
try {
    [Rankings.Power]::SetThreadExecutionState($keepAwake) | Out-Null
    # Native stderr arrives as error records here; ErrorActionPreference Continue keeps them from stopping the script.
    $ErrorActionPreference = "Continue"
    & dotnet run --project src/Rankings.Job -c Release --no-build -- @arguments 2>&1 |
        ForEach-Object { Write-Log "$_" }
    $exitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = "Stop"
    [Rankings.Power]::SetThreadExecutionState($allowSleep) | Out-Null
}

$now = "{0:yyyy-MM-dd HH:mm:ss} UTC " -f (Get-Date).ToUniversalTime()
switch ($exitCode) {
    0 { Write-Log "$now Backfill finished. Next: assign the waiting teams in the admin dashboard, then reparse." }
    3 { Write-Log "$now Stopped on Liquipedia's rate limit. Run this script again in an hour; it carries on where it stopped." }
    default { Write-Log "$now The backfill failed with exit code $exitCode; see above. Running the script again carries on after the last finished month." }
}
exit $exitCode
