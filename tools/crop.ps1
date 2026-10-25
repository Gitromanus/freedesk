param(
    [string]$Path = "$env:TEMP\freedesk-frame.jpg",
    [int]$X, [int]$Y, [int]$W, [int]$H,
    [double]$Zoom = 2.0,
    [string]$Out = "$env:TEMP\freedesk-crop.png"
)
Add-Type -AssemblyName System.Drawing
$src = [Drawing.Bitmap]::FromFile($Path)
$rect = New-Object Drawing.Rectangle($X, $Y, $W, $H)
$crop = New-Object Drawing.Bitmap($W, $H)
$g = [Drawing.Graphics]::FromImage($crop)
$g.DrawImage($src, (New-Object Drawing.Rectangle(0, 0, $W, $H)), $rect, [Drawing.GraphicsUnit]::Pixel)
$g.Dispose()
$zoomed = New-Object Drawing.Bitmap([int]($W * $Zoom), [int]($H * $Zoom))
$g2 = [Drawing.Graphics]::FromImage($zoomed)
$g2.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g2.DrawImage($crop, 0, 0, $zoomed.Width, $zoomed.Height)
$g2.Dispose()
$zoomed.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$src.Dispose(); $crop.Dispose(); $zoomed.Dispose()
Write-Output $Out
