# Generates Assets/Nighty.ico (rounded blue-violet square with a crescent moon).
Add-Type -AssemblyName System.Drawing
$sizes = 16,24,32,48,64,128,256
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s,$s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode='AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $r = $s*0.22
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r*2
  $path.AddArc(0,0,$d,$d,180,90); $path.AddArc($s-$d-1,0,$d,$d,270,90)
  $path.AddArc($s-$d-1,$s-$d-1,$d,$d,0,90); $path.AddArc(0,$s-$d-1,$d,$d,90,90); $path.CloseFigure()
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0,0)),([System.Drawing.Point]::new($s,$s)),([System.Drawing.Color]::FromArgb(47,95,208)),([System.Drawing.Color]::FromArgb(106,79,224))
  $g.FillPath($br,$path)
  # crescent = white circle minus offset circle
  $m = New-Object System.Drawing.Drawing2D.GraphicsPath
  $m.AddEllipse($s*0.24,$s*0.22,$s*0.52,$s*0.52)
  $cut = New-Object System.Drawing.Drawing2D.GraphicsPath
  $cut.AddEllipse($s*0.40,$s*0.14,$s*0.50,$s*0.50)
  $reg = New-Object System.Drawing.Region $m
  $reg.Exclude($cut)
  $g.FillRegion([System.Drawing.Brushes]::White,$reg)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms,[System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,@($s,$ms.ToArray())
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16*$pngs.Count
foreach ($p in $pngs) {
  $s=$p[0]; $b=$p[1]
  $w.Write([byte]($(if($s -ge 256){0}else{$s}))); $w.Write([byte]($(if($s -ge 256){0}else{$s})))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$b.Length); $w.Write([uint32]$offset); $offset += $b.Length
}
foreach ($p in $pngs) { $w.Write($p[1]) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot '..\Assets\Nighty.ico'), $out.ToArray())
