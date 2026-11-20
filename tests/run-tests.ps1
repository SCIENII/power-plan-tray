# PowerPlanTray acceptance tests.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
#
# Covers the build, the whole command-line contract (--version / --help / --list /
# --set plus every documented exit code), duplicate plan names, single instance and
# the icon generator. The active power plan is recorded first and restored at the
# end, so a passing run leaves the machine exactly as it found it.
# ASCII only: Windows PowerShell 5.1 parses BOM-less script files as ANSI.
param(
    [switch]$SkipBuild,
    [switch]$SkipIconProbe
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $root 'PowerPlanTray.exe'
$work = Join-Path $root ('build/test-run-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Force -Path $work

$script:pass = 0
$script:fail = 0
$script:skip = 0
$lines = New-Object System.Collections.Generic.List[string]

function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) {
        $script:pass++
        $lines.Add("  [ok]   $Name")
    } else {
        $script:fail++
        $lines.Add("  [FAIL] $Name" + $(if ($Detail) { "  <-- $Detail" } else { '' }))
    }
}

function Note {
    param([string]$Name)
    $script:skip++
    $lines.Add("  [skip] $Name")
}

# Runs the exe with an already quoted argument string and returns its exit code.
function Invoke-Exe {
    param([string]$Arguments)
    $p = Start-Process -FilePath $exe -ArgumentList $Arguments -Wait -PassThru
    return $p.ExitCode
}

function Get-Report {
    param([string]$Json)
    if (!(Test-Path -LiteralPath $Json)) { return $null }
    return [System.IO.File]::ReadAllText($Json, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
}

$originalActive = $null

try {
    # ---------- build ----------
    if ($SkipBuild) {
        Note 'compile with build.cmd (skipped)'
    } else {
        $build = & cmd.exe /c "`"$root\build.cmd`"" 2>&1
        Check 'compile with build.cmd' ($LASTEXITCODE -eq 0) (($build | Out-String).Trim())
    }
    Check 'PowerPlanTray.exe exists' (Test-Path -LiteralPath $exe)
    if (!(Test-Path -LiteralPath $exe)) { throw 'nothing to test without a build' }

    # ---------- command-line contract ----------
    Check '--version exits 0' ((Invoke-Exe '--version') -eq 0)
    Check '--help exits 0' ((Invoke-Exe '--help') -eq 0)
    Check '--set without a value exits 2' ((Invoke-Exe '--set') -eq 2)
    Check 'unknown argument exits 2' ((Invoke-Exe '--bogus') -eq 2)
    Check 'unknown plan exits 3' ((Invoke-Exe '--set "NoSuchPlan__"') -eq 3)
    Check 'power-mode overlay GUID is rejected (exit 3)' `
        ((Invoke-Exe '--set ded574b5-45a0-4f42-8737-46345c09c238') -eq 3)

    # ---------- --list ----------
    $jsonPath = Join-Path $work 'plans.json'
    $txtPath = Join-Path $work 'plans.txt'
    Check '--list <json> exits 0' ((Invoke-Exe "--list `"$jsonPath`"") -eq 0)
    Check '--list <txt> exits 0' ((Invoke-Exe "--list `"$txtPath`"") -eq 0)

    $report = Get-Report $jsonPath
    Check 'plan report parses as JSON' ($null -ne $report)
    if ($report) {
        $originalActive = $report.active
        Check 'report lists plans' (@($report.plans).Count -gt 0)
        Check 'report exposes a version' ($report.version -match '^\d+\.\d+\.\d+$') $report.version
        $bad = @($report.plans | Where-Object {
                !($_.guid -match '^[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}$') -or
                [string]::IsNullOrWhiteSpace($_.name) -or
                [string]::IsNullOrWhiteSpace($_.label) -or
                [string]::IsNullOrWhiteSpace($_.englishName)
            })
        Check 'every plan has guid/name/label/englishName' ($bad.Count -eq 0) ($bad | ConvertTo-Json -Compress)
        $dupes = @($report.plans | Group-Object label | Where-Object { $_.Count -gt 1 })
        Check 'display labels are unique' ($dupes.Count -eq 0) (($dupes | ForEach-Object { $_.Name }) -join ', ')
        $marked = @($report.plans | Where-Object { $_.active })
        if (@($report.plans | Where-Object { $_.guid -eq $report.active }).Count -eq 1) {
            Check 'exactly one plan is marked active' ($marked.Count -eq 1)
        } else {
            Note 'active plan is missing from the list (cannot assert the active flag)'
        }
    }

    if (Test-Path -LiteralPath $txtPath) {
        $text = [System.IO.File]::ReadAllText($txtPath, [System.Text.Encoding]::UTF8)
        Check 'txt report lists the plans' ($text -match 'PowerPlanTray \d')
        Check 'txt report marks the active plan' ($text -match '\*\s')
        Check 'txt report is written without a BOM' (([System.IO.File]::ReadAllBytes($txtPath)[0]) -ne 0xEF)
    } else {
        Check 'txt report written' $false
    }

    # ---------- duplicate names must be distinguishable ----------
    if ($report) {
        $amb = @($report.plans | Group-Object name | Where-Object { $_.Count -gt 1 })
        if ($amb.Count -gt 0) {
            $dupName = $amb[0].Name
            Check "duplicate name '$dupName' exits 4" ((Invoke-Exe "--set `"$dupName`"") -eq 4)
            $label = @($amb[0].Group)[0].label
            Check "disambiguated label '$label' is accepted" ((Invoke-Exe "--set `"$label`"") -eq 0)
        } else {
            Note 'no duplicate plan names here (ambiguity branch untested)'
        }
    }

    # ---------- a no-op switch must not move anything ----------
    if ($report -and $originalActive) {
        $activePlan = @($report.plans | Where-Object { $_.active })[0]
        if ($activePlan) {
            Check 'switching to the active plan exits 0' ((Invoke-Exe "--set `"$($activePlan.label)`"") -eq 0)
            $afterPath = Join-Path $work 'plans-after.json'
            [void](Invoke-Exe "--list `"$afterPath`"")
            $after = Get-Report $afterPath
            Check 'active plan unchanged after no-op switch' ($after.active -eq $originalActive)
        }
    }

    # ---------- single instance ----------
    if (@(Get-Process -Name 'PowerPlanTray' -ErrorAction SilentlyContinue).Count -gt 0) {
        Note 'single-instance behaviour (a PowerPlanTray is already running)'
    } else {
        $first = Start-Process -FilePath $exe -PassThru
        Start-Sleep -Milliseconds 1500
        Check 'launching without arguments keeps running' (-not $first.HasExited)
        $second = Start-Process -FilePath $exe -Wait -PassThru
        Check 'a second launch exits 0' ($second.ExitCode -eq 0)
        if (-not $first.HasExited) { $first.Kill(); [void]$first.WaitForExit(5000) }
    }

    # ---------- icon generator ----------
    if ($SkipIconProbe) {
        Note 'icon probe (skipped)'
    } else {
        $probe = Join-Path $PSScriptRoot 'icon-probe.ps1'
        if (Test-Path -LiteralPath $probe) {
            $probeOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $probe -Exe $exe 2>&1
            $probeCode = $LASTEXITCODE
            Check 'icon probe passes' ($probeCode -eq 0) (($probeOut | Out-String).Trim())
            foreach ($line in @($probeOut)) { if ($line) { $lines.Add("         $line") } }
        } else {
            Note 'icon probe script missing'
        }
    }
}
finally {
    # Restore the user's power plan no matter how the tests ended.
    try {
        $finalPath = Join-Path $work 'plans-final.json'
        [void](Invoke-Exe "--list `"$finalPath`"")
        $final = Get-Report $finalPath
        if ($originalActive -and $final -and $final.active -ne $originalActive) {
            [void](Invoke-Exe "--set $originalActive")
            $lines.Add("  [note] restored the active power plan to $originalActive")
        }
    } catch {
        $lines.Add("  [warn] could not restore the power plan: $($_.Exception.Message)")
    }
    Get-Process -Name 'PowerPlanTray' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe } | ForEach-Object { $_.Kill() }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$lines.Insert(0, "PowerPlanTray tests: $script:pass passed, $script:fail failed, $script:skip skipped")
foreach ($line in $lines) { Write-Output $line }
if ($script:fail -gt 0) { exit 1 } else { exit 0 }
