# Geometry regression for the shared free canvas, using the built Windows EXE.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[void][Reflection.Assembly]::LoadFrom((Join-Path $root 'AppShelf.exe'))
function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
}
function Rect([int]$x,[int]$y,[int]$w,[int]$h) {
    New-Object AppShelf.FreeCanvasRect -ArgumentList $x,$y,$w,$h
}
$a = Rect 0 0 4 3
$b = Rect 4 0 2 3
$c = Rect 3 2 2 2
Assert ([AppShelf.FreeCanvasLayout]::IsValid($a)) 'valid rect rejected'
Assert (-not [AppShelf.FreeCanvasLayout]::Intersects($a,$b)) 'touching edges overlap'
Assert ([AppShelf.FreeCanvasLayout]::Intersects($a,$c)) 'overlap missed'
Assert (-not [AppShelf.FreeCanvasLayout]::IsValid((Rect -1 0 1 1))) 'negative coordinate accepted'
Assert (-not [AppShelf.FreeCanvasLayout]::IsValid((Rect 4095 0 2 1))) 'out of bounds accepted'
Assert (-not [AppShelf.FreeCanvasLayout]::IsValid((Rect 0 0 0 1))) 'empty rectangle accepted'
Assert (-not [AppShelf.FreeCanvasLayout]::IsValid((Rect ([int]::MaxValue) 0 1 1))) 'overflow accepted'
$occupied = New-Object 'System.Collections.Generic.List[AppShelf.FreeCanvasRect]'
$occupied.Add($a)
$occupied.Add($b)
Assert (-not [AppShelf.FreeCanvasLayout]::CanPlace($c,$occupied)) 'collision accepted'
$slot = [AppShelf.FreeCanvasLayout]::FindFirstFree(2,2,8,$occupied)
Assert ($slot.X -eq 6 -and $slot.Y -eq 0) 'did not use first free gap'
$slot = [AppShelf.FreeCanvasLayout]::FindFirstFree(3,2,8,$occupied)
Assert ($slot.X -eq 0 -and $slot.Y -eq 3) 'did not wrap to next row'
Assert ($a.X -eq 0 -and $a.Width -eq 4 -and $b.X -eq 4) 'occupied positions mutated'
Assert ($null -eq [AppShelf.FreeCanvasLayout]::FindFirstFree(9,1,8,$occupied)) 'oversized card accepted'
$occupied.Clear()
$occupied.Add((Rect 0 0 8 4096))
Assert ($null -eq [AppShelf.FreeCanvasLayout]::FindFirstFree(1,1,8,$occupied)) 'no-space condition missed'
Assert ([AppShelf.FreeCanvasLayout]::Snap(15,10) -eq 2) 'half step not rounded away from zero'
Assert ([AppShelf.FreeCanvasLayout]::Snap(-10,10) -eq 0) 'negative snap not clamped'
Assert ([AppShelf.FreeCanvasLayout]::Snap(100000,10) -eq 4096) 'snap overflow not clamped'
Write-Host 'PASS free canvas geometry'

# Exercise the actual WPF panel without creating a window or touching user data.
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
$asm = [AppShelf.FreeCanvasRect].Assembly
$type = $asm.GetType('AppShelf.FreeCanvasBoard', $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$board = [Activator]::CreateInstance($type, $true)
$add = $type.GetMethod('AddCard', $flags)
$capture = $type.GetMethod('Capture', $flags)
$candidate = $type.GetMethod('MoveCandidate', $flags)
$set = $type.GetMethod('SetPreferred', $flags)
$first = New-Object System.Windows.Controls.Border
$first.MinHeight = 100
$second = New-Object System.Windows.Controls.Border
$second.MinHeight = 100
$third = New-Object System.Windows.Controls.Border
$third.MinHeight = 140
[void]$add.Invoke($board, [object[]]@('app:work',$first.PSObject.BaseObject,20,(Rect 0 0 20 6).PSObject.BaseObject))
[void]$add.Invoke($board, [object[]]@('widget:1',$second.PSObject.BaseObject,20,(Rect 20 0 20 6).PSObject.BaseObject))
[void]$add.Invoke($board, [object[]]@('app:other',$third.PSObject.BaseObject,20,$null))
function Layout {
    $board.Measure((New-Object System.Windows.Size -ArgumentList 900,600))
    $board.Arrange((New-Object System.Windows.Rect -ArgumentList 0,0,$board.DesiredSize.Width,$board.DesiredSize.Height))
}
Layout
$actual = $capture.Invoke($board, $null)
Assert ($actual.Count -eq 3) 'all cards must be placed'
Assert ($actual['widget:1'].X -eq 20 -and $actual['widget:1'].Y -eq 0) 'valid saved location not preserved'
Assert ($actual['app:other'].Y -ge 6) 'new card overlaps existing row'
$blocked = $candidate.Invoke($board, [object[]]@('app:other',(New-Object System.Windows.Point -ArgumentList 0,0).PSObject.BaseObject))
Assert ($null -eq $blocked) 'overlapping drag target accepted'
$move = $candidate.Invoke($board, [object[]]@('app:other',(New-Object System.Windows.Point -ArgumentList 40,300).PSObject.BaseObject))
Assert ($null -ne $move -and $move.X -eq 2 -and $move.Y -eq 15) 'valid drag target rejected'
[void]$set.Invoke($board, [object[]]@('app:other',$move.PSObject.BaseObject))
Layout
$actual = $capture.Invoke($board, $null)
Assert ($actual['app:other'].X -eq 2 -and $actual['app:other'].Y -eq 15) 'move not retained'
$first.MinHeight = 400
$board.InvalidateMeasure()
Layout
$actual = $capture.Invoke($board, $null)
Assert ($actual.Count -eq 3) 'card lost after content grows'
Assert (-not [AppShelf.FreeCanvasLayout]::Intersects($actual['app:work'],$actual['app:other'])) 'growth overlaps another card'
Assert (-not [AppShelf.FreeCanvasLayout]::Intersects($actual['widget:1'],$actual['app:other'])) 'growth overlaps widget card'
$first.MinHeight = 100
$board.InvalidateMeasure()
Layout
$actual = $capture.Invoke($board, $null)
Assert ($actual['app:other'].X -eq 2 -and $actual['app:other'].Y -eq 15) 'temporary growth overwrote preferred position'
$config = New-Object AppShelf.AppConfig
$config.CanvasPositions = $actual
$copyMethod = $config.GetType().GetMethod('Copy',$flags)
$copy = $copyMethod.Invoke($config,$null)
$copy.CanvasPositions['app:other'].X = 10
Assert ($config.CanvasPositions['app:other'].X -eq 2) 'staged settings mutate original geometry'
Write-Host 'PASS shared WPF canvas layout and staged configuration'

# Explicit resize, collision, grid conversion and drag offset.
$resize = $type.GetMethod('ResizeCandidate', $flags)
$wide = $resize.Invoke($board,[object[]]@('app:other', 600.0, 240.0))
Assert ($null -ne $wide -and $wide.Width -eq 30 -and $wide.ManualHeight -eq 12) 'free resize rejected'
[void]$set.Invoke($board,[object[]]@('app:other',$wide.PSObject.BaseObject))
Layout
$actual = $capture.Invoke($board,$null)
Assert ($actual['app:other'].Width -eq 30 -and $actual['app:other'].Height -ge 12) 'resize not applied'
$collision = $resize.Invoke($board,[object[]]@('app:work', 600.0, 100.0))
Assert ($null -eq $collision) 'resize into neighboring card accepted'
$converted = [AppShelf.FreeCanvasLayout]::ConvertStep($wide,20,10)
Assert ($converted.X -eq 4 -and $converted.Width -eq 60 -and $converted.ManualHeight -eq 24) 'grid conversion changed DIP geometry'
$begin = $type.GetMethod('BeginGroupDrag',$flags)
$finish = $type.GetMethod('EndGroupDrag',$flags)
[void]$begin.Invoke($board,[object[]]@('app:other',(New-Object System.Windows.Point -ArgumentList 60,320).PSObject.BaseObject))
$moved = $candidate.Invoke($board,[object[]]@('app:other',(New-Object System.Windows.Point -ArgumentList 100,360).PSObject.BaseObject))
Assert ($null -ne $moved -and $moved.X -eq 4 -and $moved.Y -eq 17) 'drag anchor offset lost'
[void]$finish.Invoke($board,$null)
Write-Host 'PASS resize, grid conversion and drag anchor'

# Narrow-window strategies are view-only and never persist altered positions.
$viewport = $type.GetMethod('SetViewport',$flags)
$temp = $type.GetProperty('IsTemporaryLayout',$flags)
$scale = $type.GetProperty('DisplayScale',$flags)
$toggle = $type.GetMethod('ToggleTemporaryGroup',$flags)
$before = $capture.Invoke($board,$null)
[void]$viewport.Invoke($board,[object[]]@(420.0,'adaptive'))
Layout
Assert ($temp.GetValue($board,$null)) 'adaptive view did not activate'
Assert ($first.RenderSize.Width -le 420 -and $second.RenderSize.Width -le 420) 'adaptive cards exceed viewport'
Assert ($null -eq $candidate.Invoke($board,[object[]]@('app:other',(New-Object System.Windows.Point -ArgumentList 0,0).PSObject.BaseObject))) 'temporary layout accepts edits'
[void]$viewport.Invoke($board,[object[]]@(420.0,'collapse'))
Layout
Assert ($first.Visibility.ToString() -eq 'Hidden') 'compact view did not collapse cards'
Assert ($toggle.Invoke($board,[object[]]@('app:work'))) 'temporary reveal rejected'
Layout
Assert ($first.Visibility.ToString() -eq 'Visible' -and $second.Visibility.ToString() -eq 'Hidden') 'temporary reveal changed other card'
[void]$viewport.Invoke($board,[object[]]@(420.0,'scale'))
Layout
Assert ([Math]::Abs($scale.GetValue($board,$null) - 0.75) -lt 0.001) 'minimum readable scale not respected'
[void]$viewport.Invoke($board,[object[]]@(420.0,'scroll'))
Layout
Assert (-not $temp.GetValue($board,$null)) 'scroll mode unexpectedly temporary'
Assert ($board.DesiredSize.Width -gt 420) 'scroll mode lost original extent'
[void]$viewport.Invoke($board,[object[]]@(1200.0,'adaptive'))
Layout
Assert (-not $temp.GetValue($board,$null)) 'wide view did not restore original layout'
$after = $capture.Invoke($board,$null)
foreach ($key in $before.Keys) {
    Assert ($before[$key].X -eq $after[$key].X -and $before[$key].Y -eq $after[$key].Y -and $before[$key].Width -eq $after[$key].Width -and $before[$key].Height -eq $after[$key].Height) 'viewport strategy modified original placement'
}
Write-Host 'PASS viewport strategies and restoration'

# All arrangement commands leave their source untouched and never overlap.
$source = $capture.Invoke($board,$null)
foreach ($command in @('vertical','rows','compact','left','right')) {
    $arranged = [AppShelf.FreeCanvasLayout]::Arrange($source,$command,40)
    Assert ($null -ne $arranged -and $arranged.Count -eq $source.Count) "arrangement lost cards: $command"
    $keys = @($arranged.Keys)
    for ($i=0; $i -lt $keys.Count; $i++) {
        for ($j=$i+1; $j -lt $keys.Count; $j++) {
            Assert (-not [AppShelf.FreeCanvasLayout]::Intersects($arranged[$keys[$i]],$arranged[$keys[$j]])) "overlap: $command"
        }
    }
    Assert ($source['app:other'].X -eq 2) 'arrangement mutated source'
    if ($command -eq 'vertical' -or $command -eq 'left') {
        foreach ($rect in $arranged.Values) { Assert ($rect.X -eq 0) 'left alignment failed' }
    }
    if ($command -eq 'right') {
        foreach ($rect in $arranged.Values) { Assert (($rect.X+$rect.Width) -eq 40) 'right alignment failed' }
    }
}
Write-Host 'PASS arrangement commands'

# D04: a long sequence and a single movement to the same pointer must
# produce identical geometry. In particular 56 DIP is not 360 DIP.
$pointerResize = $type.GetMethod('ResizeFromPointer',$flags)
$original = $capture.Invoke($board,$null)['app:other']
$start = (New-Object System.Windows.Point -ArgumentList 100,100).PSObject.BaseObject
$result = $null
for ($i=1; $i -le 12; $i++) {
    $point = (New-Object System.Windows.Point -ArgumentList (100+56.0*$i/12),(100+40.0*$i/12)).PSObject.BaseObject
    $result = $pointerResize.Invoke($board,[object[]]@('app:other',$original.PSObject.BaseObject,$start,$point))
}
$end = (New-Object System.Windows.Point -ArgumentList 156,140).PSObject.BaseObject
$single = $pointerResize.Invoke($board,[object[]]@('app:other',$original.PSObject.BaseObject,$start,$end))
Assert ($null -ne $result -and $null -ne $single) 'D04 valid resize rejected'
Assert ($result.Width -eq $single.Width -and $result.Height -eq $single.Height) 'D04 resize depends on event count'
Assert ($result.Width -eq ($original.Width + 3) -and $result.Height -eq ($original.Height + 2)) 'D04 displacement did not follow pointer'
$back = $pointerResize.Invoke($board,[object[]]@('app:other',$original.PSObject.BaseObject,$start,$start))
Assert ($back.Width -eq $original.Width -and $back.Height -eq $original.Height) 'D04 returning pointer did not restore original size'
Write-Host 'PASS D04 absolute resize pointer'
