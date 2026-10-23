# Regenerates the multi-resolution Windows icon from the same waveform as Theme.xaml.
Add-Type -AssemblyName System.Drawing
$frames = foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 64.0, $size / 64.0)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(0, 0, 24, 24, 180, 90)
    $shape.AddArc(40, 0, 24, 24, 270, 90)
    $shape.AddArc(40, 40, 24, 24, 0, 90)
    $shape.AddArc(0, 40, 24, 24, 90, 90)
    $shape.CloseFigure()
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#70E2C4'))
    $graphics.FillPath($brush, $shape)
    $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#102331'), 5)
    $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    foreach ($bar in @(@(14,27,37), @(23,19,45), @(32,12,52), @(41,22,42), @(50,27,37))) {
        $graphics.DrawLine($pen, $bar[0], $bar[1], $bar[0], $bar[2])
    }
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    [PSCustomObject]@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose(); $pen.Dispose(); $brush.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [IO.File]::Create((Join-Path $PSScriptRoot 'AudioLink.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
