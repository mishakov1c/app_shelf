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
[void]$add.Invoke($board, [object[]]@('app:work',$first,20,(Rect 0 0 20 6)))
[void]$add.Invoke($board, [object[]]@('widget:1',$second,20,(Rect 20 0 20 6)))
[void]$add.Invoke($board, [object[]]@('app:other',$third,20,$null))
function Layout {
    $board.Measure((New-Object System.Windows.Size -ArgumentList 900,600))
    $board.Arrange((New-Object System.Windows.Rect -ArgumentList 0,0,$board.DesiredSize.Width,$board.DesiredSize.Height))
}
Layout
$actual = $capture.Invoke($board, $null)
Assert ($actual.Count -eq 3) 'all cards must be placed'
Assert ($actual['widget:1'].X -eq 20 -and $actual['widget:1'].Y -eq 0) 'valid saved location not preserved'
Assert ($actual['app:other'].Y -ge 6) 'new card overlaps existing row'
$blocked = $candidate.Invoke($board, [object[]]@('app:other',(New-Object System.Windows.Point -ArgumentList 0,0)))
Assert ($null -eq $blocked) 'overlapping drag target accepted'
$move = $candidate.Invoke($board, [object[]]@('app:other',(New-Object System.Windows.Point -ArgumentList 40,300)))
Assert ($null -ne $move -and $move.X -eq 2 -and $move.Y -eq 15) 'valid drag target rejected'
[void]$set.Invoke($board, [object[]]@('app:other',$move))
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
