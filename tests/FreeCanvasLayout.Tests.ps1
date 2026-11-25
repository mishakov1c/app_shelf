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
