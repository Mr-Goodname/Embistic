$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$output=Join-Path $PSScriptRoot '..\src\Embistic\Assets'
[IO.Directory]::CreateDirectory($output) | Out-Null
$bitmap=[Drawing.Bitmap]::new(256,256)
$g=[Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
$path=[Drawing.Drawing2D.GraphicsPath]::new()
$path.AddArc(8,8,80,80,180,90)
$path.AddArc(168,8,80,80,270,90)
$path.AddArc(168,168,80,80,0,90)
$path.AddArc(8,168,80,80,90,90)
$path.CloseFigure()
$blue=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#ADC9F0'))
$ink=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#17283E'))
$g.FillPath($blue,$path)
$pen=[Drawing.Pen]::new($ink,14)
$pen.StartCap=[Drawing.Drawing2D.LineCap]::Round
$pen.EndCap=[Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($pen,153,69,153,172)
$g.FillEllipse($ink,105,153,51,38)
$g.DrawLine($pen,154,69,193,88)
$stream=[IO.MemoryStream]::new()
$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
$bytes=$stream.ToArray()
$file=[IO.File]::Create((Join-Path $output 'embistic.ico'))
$writer=[IO.BinaryWriter]::new($file)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]1)
$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([byte]0)
$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$bytes.Length);$writer.Write([uint32]22);$writer.Write($bytes)
$writer.Dispose();$stream.Dispose();$pen.Dispose();$blue.Dispose();$ink.Dispose();$path.Dispose();$g.Dispose();$bitmap.Dispose()
