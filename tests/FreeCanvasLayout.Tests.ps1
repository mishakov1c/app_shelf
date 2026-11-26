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
# Nearest positions must retain dimensions and avoid every reserved card.
$blockedRects = New-Object 'System.Collections.Generic.List[AppShelf.FreeCanvasRect]'
$blockedRects.Add((Rect 0 0 6 4))
$blockedRects.Add((Rect 0 8 7 3))
$right = Rect 15 7 3 4
$right.ManualHeight = 3
$near = [AppShelf.FreeCanvasLayout]::FindNearestFree($right,12,12,$blockedRects)
Assert ($null -ne $near -and $near.X -eq 9 -and $near.Y -eq 7 -and $near.Width -eq 3 -and $near.ManualHeight -eq 3) 'right group did not move minimally'
$blockedRects.Add((Rect 9 7 3 4))
$near = [AppShelf.FreeCanvasLayout]::FindNearestFree($right,12,12,$blockedRects)
Assert ($null -ne $near -and [AppShelf.FreeCanvasLayout]::CanPlace($near,$blockedRects) -and ($near.X + $near.Width) -le 12 -and ($near.Y + $near.Height) -le 12) 'nearest position overlaps or leaves viewport'
Assert ($right.X -eq 15 -and $right.Y -eq 7 -and $blockedRects[0].X -eq 0) 'nearest search mutated input'
$blockedRects.Clear()
$blockedRects.Add((Rect 0 0 12 12))
Assert ($null -eq [AppShelf.FreeCanvasLayout]::FindNearestFree($right,12,12,$blockedRects)) 'nearest search accepted full viewport'
Assert ($null -eq [AppShelf.FreeCanvasLayout]::FindNearestFree((Rect 0 0 13 2),12,12,$null)) 'oversized group accepted'
Write-Host 'PASS free canvas geometry and nearest free placement'

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

# Real panel: one out-of-bounds right group must not stack the two left groups.
$adaptive = [Activator]::CreateInstance($type,$true)
$work = New-Object System.Windows.Controls.Border
$work.MinHeight = 72
$tools = New-Object System.Windows.Controls.Border
$tools.MinHeight = 72
$personal = New-Object System.Windows.Controls.Border
$personal.MinHeight = 152
[void]$add.Invoke($adaptive,[object[]]@('app:work',$work.PSObject.BaseObject,20,(Rect 0 1 20 4).PSObject.BaseObject))
[void]$add.Invoke($adaptive,[object[]]@('app:tools',$tools.PSObject.BaseObject,20,(Rect 0 20 20 4).PSObject.BaseObject))
[void]$add.Invoke($adaptive,[object[]]@('widget:personal',$personal.PSObject.BaseObject,10,(Rect 65 16 10 8).PSObject.BaseObject))
$heightMethod = $type.GetMethod('SetViewportHeight',$flags)
function AdaptiveLayout([double]$width,[double]$height) {
    [void]$viewport.Invoke($adaptive,[object[]]@($width,'adaptive'))
    [void]$heightMethod.Invoke($adaptive,[object[]]@($height))
    $adaptive.Measure((New-Object System.Windows.Size -ArgumentList ([double]::PositiveInfinity),([double]::PositiveInfinity)))
    $adaptive.Arrange((New-Object System.Windows.Rect -ArgumentList 0,0,$adaptive.DesiredSize.Width,$adaptive.DesiredSize.Height))
}
function Position($card) { $card.TranslatePoint((New-Object System.Windows.Point -ArgumentList 0,0),$adaptive) }
AdaptiveLayout 1600 600
$savedPositions = $capture.Invoke($adaptive,$null)
AdaptiveLayout 1100 600
Assert ($temp.GetValue($adaptive,$null)) 'adaptation did not activate'
$editable = $type.GetMethod('CaptureEditableLayout',$flags).Invoke($adaptive,$null)
Assert ($editable['widget:personal'].X -eq 45 -and $editable['widget:personal'].Y -eq 16 -and $editable['app:tools'].Y -eq 20) 'personal layout did not capture adapted positions'
$editable['app:work'].X = 5
Assert ($capture.Invoke($adaptive,$null)['app:work'].X -eq 0) 'personal snapshot changed original geometry'
Assert ((Position $work).X -eq 0 -and (Position $work).Y -eq 20 -and $work.RenderSize.Width -eq 392) 'fitting work group changed'
Assert ((Position $tools).X -eq 0 -and (Position $tools).Y -eq 400 -and $tools.RenderSize.Width -eq 392) 'fitting tools group changed'
Assert ((Position $personal).X -eq 900 -and (Position $personal).Y -eq 320 -and $personal.RenderSize.Width -eq 192) 'right group was not moved minimally'
$adaptedPositions = $capture.Invoke($adaptive,$null)
foreach ($key in $savedPositions.Keys) {
    Assert ($adaptedPositions[$key].X -eq $savedPositions[$key].X -and $adaptedPositions[$key].Y -eq $savedPositions[$key].Y) 'adaptation altered original coordinates'
}
Assert ($null -eq $candidate.Invoke($adaptive,[object[]]@('widget:personal',(New-Object System.Windows.Point -ArgumentList 0,0).PSObject.BaseObject))) 'adaptive view accepts drag'
AdaptiveLayout 1100 600
Assert ((Position $personal).X -eq 900 -and (Position $personal).Y -eq 320) 'adaptation not repeatable'
AdaptiveLayout 300 600
$narrowEditable = $type.GetMethod('CaptureEditableLayout',$flags).Invoke($adaptive,$null)
Assert ($null -ne $narrowEditable -and $narrowEditable.Count -eq 3 -and $narrowEditable['app:work'].Width -le 15) 'narrow fallback cannot create editable layout'
Assert ($work.RenderSize.Width -eq 292 -and (Position $personal).X -eq 0 -and (Position $tools).Y -gt (Position $personal).Y) 'insufficient width did not fall back to vertical layout'
AdaptiveLayout 1600 600
Assert (-not $temp.GetValue($adaptive,$null) -and (Position $personal).X -eq 1300 -and (Position $personal).Y -eq 320 -and (Position $tools).Y -eq 400) 'large viewport did not restore composition'
Write-Host 'PASS adaptive canvas preserves fitting groups, moves only overflow, falls back and restores'
$monitorA = [AppShelf.FreeCanvasLayout]::MonitorKey('DISPLAY1',1920,1080,96,96)
$monitorB = [AppShelf.FreeCanvasLayout]::MonitorKey('DISPLAY2',1920,1080,96,96)
Assert ($monitorA -ne $monitorB -and $monitorA -ne [AppShelf.FreeCanvasLayout]::MonitorKey('DISPLAY1',1920,1080,144,144)) 'monitor keys collide'
Assert ($monitorA -eq [AppShelf.FreeCanvasLayout]::MonitorKey('display1',1920,1080,96,96)) 'monitor identity case unstable'
$personalBoard = [Activator]::CreateInstance($type,$true)
$pCard = New-Object System.Windows.Controls.Border
$pCard.MinHeight = 152
[void]$add.Invoke($personalBoard,[object[]]@('widget:personal',$pCard.PSObject.BaseObject,10,$editable['widget:personal'].PSObject.BaseObject))
[void]$viewport.Invoke($personalBoard,[object[]]@(1100.0,'scroll'))
$personalSize = New-Object System.Windows.Size -ArgumentList ([double]::PositiveInfinity),([double]::PositiveInfinity)
$personalBoard.Measure($personalSize)
$personalBoard.Arrange((New-Object System.Windows.Rect -ArgumentList 0,0,$personalBoard.DesiredSize.Width,$personalBoard.DesiredSize.Height))
Assert (-not $temp.GetValue($personalBoard,$null)) 'personal layout not editable'
Assert ($null -ne $candidate.Invoke($personalBoard,[object[]]@('widget:personal',(New-Object System.Windows.Point -ArgumentList 20,40).PSObject.BaseObject))) 'personal drag rejected'
Assert ($null -ne $resize.Invoke($personalBoard,[object[]]@('widget:personal',280.0,180.0))) 'personal resize rejected'
Write-Host 'PASS independent monitor key and editable snapshot'
# A larger viewport must expose empty area, and drag cannot introduce adaptive overflow.
[void]$type.GetMethod('SetViewportHeight',$flags).Invoke($board,[object[]]@(1000.0))
$board.Measure((New-Object System.Windows.Size -ArgumentList ([double]::PositiveInfinity),([double]::PositiveInfinity)))
$board.Arrange((New-Object System.Windows.Rect -ArgumentList 0,0,$board.DesiredSize.Width,$board.DesiredSize.Height))
Assert ($board.DesiredSize.Height -ge 1000) 'canvas does not fill taller viewport'
$outside = $candidate.Invoke($board,[object[]]@('app:other',(New-Object System.Windows.Point -ArgumentList 1100,700).PSObject.BaseObject))
Assert ($null -eq $outside -and -not $temp.GetValue($board,$null)) 'drag beyond right edge accepted in adaptive mode'
$lower = $candidate.Invoke($board,[object[]]@('app:other',(New-Object System.Windows.Point -ArgumentList 40,700).PSObject.BaseObject))
Assert ($null -ne $lower) 'available lower viewport rejected'
Write-Host 'PASS enlarged viewport and adaptive drag boundary'

# Collapsed effective height must not invalidate the saved manual height or collision checks.
$collapsedBoard = [Activator]::CreateInstance($type,$true)
$expander = New-Object System.Windows.Controls.Expander
$expander.Header = 'Collapsed group'
$body = New-Object System.Windows.Controls.Border
$body.Height = 180
$expander.Content = $body
$expander.IsExpanded = $false
$card = New-Object System.Windows.Controls.Border
$card.Child = $expander
$pref = Rect 2 2 12 18
$pref.ManualHeight = 18
[void]$add.Invoke($collapsedBoard,[object[]]@('app:collapsed',$card.PSObject.BaseObject,12,$pref.PSObject.BaseObject))
$infinite = New-Object System.Windows.Size -ArgumentList ([double]::PositiveInfinity),([double]::PositiveInfinity)
$collapsedBoard.Measure($infinite)
$collapsedBoard.Arrange((New-Object System.Windows.Rect -ArgumentList 0,0,$collapsedBoard.DesiredSize.Width,$collapsedBoard.DesiredSize.Height))
$positions = $capture.Invoke($collapsedBoard,$null)
$short = $positions['app:collapsed']
Assert ([AppShelf.FreeCanvasLayout]::IsValid($short) -and $short.Height -lt 18 -and $short.X -eq 2) 'collapsed saved size leaves invalid effective rectangle'
$stored = $type.GetMethod('PreferenceForEdit',$flags).Invoke($collapsedBoard,[object[]]@('app:collapsed',$short.PSObject.BaseObject))
Assert ($stored.ManualHeight -eq 18) 'collapsed drag loses expanded manual height'
$expander.IsExpanded = $true
$collapsedBoard.InvalidateMeasure()
$collapsedBoard.Measure($infinite)
$positions = $capture.Invoke($collapsedBoard,$null)
$expanded = $positions['app:collapsed']
Assert ($expanded.Height -ge 18 -and $expanded.ManualHeight -eq 18 -and $expanded.X -eq 2) 'expanded height not restored'
Write-Host 'PASS collapsed effective geometry preserves manual expanded height'

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
