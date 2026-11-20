# Icon generation checks: reflect into PowerPlanTray.exe and validate the ICO we build.
# Called by run-tests.ps1 through powershell.exe, because pwsh 7 cannot load a
# .NET Framework assembly. ASCII only: Windows PowerShell 5.1 reads BOM-less
# script files with the system ANSI code page.
param(
    [Parameter(Mandatory = $true)][string]$Exe
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]

function Expect {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) {
        Write-Output "[ok] $Name"
    } else {
        $failures.Add($Name + $(if ($Detail) { " ($Detail)" } else { '' }))
        Write-Output "[FAIL] $Name $Detail"
    }
}

$asm = [Reflection.Assembly]::LoadFrom($Exe)
$null = [Reflection.Assembly]::Load('System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a')
$app = $asm.GetType('PowerPlanTray.TrayApp')
$flags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$makeIcon = $app.GetMethod('MakeIcon', $flags)
$renderIcon = $app.GetMethod('RenderIcon', $flags)
Expect 'MakeIcon and RenderIcon are present' ($null -ne $makeIcon -and $null -ne $renderIcon)
if ($null -eq $makeIcon) { exit 1 }

$color = [System.Drawing.Color]::FromArgb(0, 120, 212)

# 1. The generated Icon reports its largest frame.
$icon = $makeIcon.Invoke($null, @($color, 50))
Expect 'icon is 32x32' ($icon.Width -eq 32 -and $icon.Height -eq 32) "$($icon.Width)x$($icon.Height)"

# 2. Master bitmap: transparent corner, body drawn, fill uses the plan colour.
$master = $renderIcon.Invoke($null, @(128, $color, 50))
Expect '128px master renders' ($master.Width -eq 128 -and $master.Height -eq 128)
$corner = $master.GetPixel(0, 0)
$fill = $master.GetPixel(40, 70)
Expect 'master corner is transparent' ($corner.A -eq 0) "A=$($corner.A)"
Expect 'master fill uses the plan colour' ($fill.ToArgb() -eq $color.ToArgb()) ('#' + $fill.ToArgb().ToString('X8'))
$master.Dispose()
$icon.Dispose()

# 3. The charge bar must follow the plan level.
function BarRight([System.Drawing.Bitmap]$bmp) {
    $right = 0
    for ($x = 0; $x -lt $bmp.Width; $x++) {
        if ($bmp.GetPixel($x, 70).ToArgb() -eq $color.ToArgb()) { $right = $x }
    }
    return $right
}
$narrow = $renderIcon.Invoke($null, @(128, $color, 22))
$wide = $renderIcon.Invoke($null, @(128, $color, 100))
$nw = BarRight $narrow
$ww = BarRight $wide
Expect 'bar width follows the plan level' ($ww -gt $nw) "level22=$nw level100=$ww"
$narrow.Dispose()
$wide.Dispose()

# 4. Repeated builds must not pile up GDI handles.
$me = [System.Diagnostics.Process]::GetCurrentProcess()
$before = $me.HandleCount
for ($i = 0; $i -lt 100; $i++) { $ic = $makeIcon.Invoke($null, @($color, 50)); $ic.Dispose() }
[GC]::Collect()
[GC]::WaitForPendingFinalizers()
[GC]::Collect()
$after = [System.Diagnostics.Process]::GetCurrentProcess().HandleCount
Expect 'icon builds do not leak handles' ([Math]::Abs($after - $before) -le 20) "delta=$($after - $before)"

if ($failures.Count -gt 0) {
    Write-Output ('icon probe failed: ' + ($failures -join '; '))
    exit 1
}
Write-Output 'icon probe ok'
exit 0
