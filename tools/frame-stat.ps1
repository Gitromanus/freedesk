param([string]$Path = "$env:TEMP\freedesk-frame.jpg")
Add-Type -AssemblyName System.Drawing
$bmp = [Drawing.Bitmap]::FromFile($Path)
$sum = 0.0; $n = 0; $bright = 0; $mid = 0
for ($x = 0; $x -lt $bmp.Width; $x += 37) {
    for ($y = 0; $y -lt $bmp.Height; $y += 37) {
        $c = $bmp.GetPixel($x, $y)
        $l = 0.299*$c.R + 0.587*$c.G + 0.114*$c.B
        $sum += $l; $n++
        if ($l -gt 40) { $bright++ }
        if ($l -gt 5)  { $mid++ }
    }
}
'{0}: mean lum {1:F1}, px>5 {2:P1}, px>40 {3:P1}, size {4}x{5}' -f $Path, ($sum/$n), ($mid/$n), ($bright/$n), $bmp.Width, $bmp.Height
$bmp.Dispose()
