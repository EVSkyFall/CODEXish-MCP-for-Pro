# Regenerates everything in assets/generated from the SVG sources. Authoring time only: the build and the program use
# the committed output and fetch nothing.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File assets\build-icons.ps1      (Windows PowerShell 5.1 or pwsh 7)
#
# Output:
#   codexish.ico                   app icon, 16-256 px
#   tray-<state>.ico               tray icons for running, stopped and attention (retrying or error)
#   codexish-<size>.png            the app icon as PNG for documentation
#   glyphs-<size>.png, glyphs.txt  every icon in assets/lucide, black on transparent, one sprite sheet per size;
#                                  glyphs.txt names the columns in order. The program tints them at run time.
#
# WPF draws the shapes. Only the SVG subset the sources use is understood: svg, g, path, rect, circle, line,
# polyline, polygon, clipPath with clip-rule, and the fill, fill-rule, stroke, stroke-width, stroke-linecap,
# stroke-linejoin, transform (translate, scale, matrix) and clip-path attributes.
param([string]$Assets = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') { throw 'Run this script in an STA PowerShell (the default for powershell.exe and pwsh on Windows).' }

$Culture = [Globalization.CultureInfo]::InvariantCulture
$IconSizes = 16, 20, 24, 32, 40, 48, 64, 256
$GlyphSizes = 16, 20, 24, 32, 48
$Out = Join-Path $Assets 'generated'
New-Item -ItemType Directory -Force -Path $Out | Out-Null

function Number([string]$text, [double]$fallback = 0) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $fallback }
    return [double]::Parse($text.Trim(), $Culture)
}

function Color([string]$value, [Windows.Media.Color]$current) {
    if ([string]::IsNullOrWhiteSpace($value) -or $value -eq 'none') { return $null }
    if ($value -eq 'currentColor') { return $current }
    return [Windows.Media.ColorConverter]::ConvertFromString($value)
}

# SVG lists transforms outermost first; WPF multiplies row vectors, so the innermost is applied first.
function Transform([string]$text) {
    $matrix = [Windows.Media.Matrix]::Identity
    if ([string]::IsNullOrWhiteSpace($text)) { return $matrix }
    $parts = [regex]::Matches($text, '(\w+)\s*\(([^)]*)\)')
    for ($i = $parts.Count - 1; $i -ge 0; $i--) {
        $values = @([regex]::Split($parts[$i].Groups[2].Value.Trim(), '[\s,]+') | Where-Object { $_ } | ForEach-Object { Number $_ })
        $step = [Windows.Media.Matrix]::Identity
        switch ($parts[$i].Groups[1].Value) {
            'translate' { $y = 0; if ($values.Count -gt 1) { $y = $values[1] }; $step.Translate($values[0], $y) }
            'scale' { $y = $values[0]; if ($values.Count -gt 1) { $y = $values[1] }; $step.Scale($values[0], $y) }
            'matrix' { $step = New-Object Windows.Media.Matrix($values[0], $values[1], $values[2], $values[3], $values[4], $values[5]) }
            default { throw "Unsupported transform $($parts[$i].Groups[1].Value)" }
        }
        $matrix = [Windows.Media.Matrix]::Multiply($matrix, $step)
    }
    return $matrix
}

function Points([string]$text, [bool]$closed) {
    $values = @([regex]::Split($text.Trim(), '[\s,]+') | ForEach-Object { Number $_ })
    $data = 'M' + ($values[0..1] -join ',')
    for ($i = 2; $i -lt $values.Count; $i += 2) { $data += ' L' + ($values[$i..($i + 1)] -join ',') }
    if ($closed) { $data += ' Z' }
    return $data
}

# Geometry.Parse treats data without a fill-rule prefix as even-odd; SVG's default is nonzero.
function Geometry($node, [string]$rule) {
    $prefix = 'F1 '
    if ($rule -eq 'evenodd') { $prefix = 'F0 ' }
    switch ($node.LocalName) {
        'path' { return [Windows.Media.Geometry]::Parse($prefix + $node.GetAttribute('d')) }
        'rect' {
            $rx = Number $node.GetAttribute('rx')
            $ry = Number $node.GetAttribute('ry') $rx
            if (-not $node.HasAttribute('rx')) { $rx = $ry }
            $rect = New-Object Windows.Rect((Number $node.GetAttribute('x')), (Number $node.GetAttribute('y')), (Number $node.GetAttribute('width')), (Number $node.GetAttribute('height')))
            return New-Object Windows.Media.RectangleGeometry($rect, $rx, $ry)
        }
        'circle' {
            $r = Number $node.GetAttribute('r')
            $center = New-Object Windows.Point((Number $node.GetAttribute('cx')), (Number $node.GetAttribute('cy')))
            return New-Object Windows.Media.EllipseGeometry($center, $r, $r)
        }
        'line' {
            $from = New-Object Windows.Point((Number $node.GetAttribute('x1')), (Number $node.GetAttribute('y1')))
            $to = New-Object Windows.Point((Number $node.GetAttribute('x2')), (Number $node.GetAttribute('y2')))
            return New-Object Windows.Media.LineGeometry($from, $to)
        }
        'polyline' { return [Windows.Media.Geometry]::Parse($prefix + (Points $node.GetAttribute('points') $false)) }
        'polygon' { return [Windows.Media.Geometry]::Parse($prefix + (Points $node.GetAttribute('points') $true)) }
        default { return $null }
    }
}

function Inherit($style, $node) {
    $next = $style.Clone()
    foreach ($name in 'fill', 'fill-rule', 'stroke', 'stroke-width', 'stroke-linecap', 'stroke-linejoin') {
        if ($node.HasAttribute($name)) { $next[$name] = $node.GetAttribute($name) }
    }
    return $next
}

function Draw($context, $node, $style, $clips, [Windows.Media.Color]$current) {
    if ($node.NodeType -ne 'Element' -or $node.LocalName -in 'defs', 'clipPath', 'title', 'desc') { return }
    $style = Inherit $style $node
    $pushed = 0
    if ($node.HasAttribute('transform')) {
        $context.PushTransform((New-Object Windows.Media.MatrixTransform((Transform $node.GetAttribute('transform')))))
        $pushed++
    }
    if ($node.HasAttribute('clip-path')) {
        $id = [regex]::Match($node.GetAttribute('clip-path'), 'url\(#([^)]+)\)').Groups[1].Value
        $context.PushClip($clips[$id])
        $pushed++
    }
    if ($node.LocalName -in 'svg', 'g') {
        foreach ($child in $node.ChildNodes) { Draw $context $child $style $clips $current }
    }
    else {
        $geometry = Geometry $node $style['fill-rule']
        if ($null -ne $geometry) {
            $brush = $null
            $pen = $null
            $fill = Color $style['fill'] $current
            if ($null -ne $fill) { $brush = New-Object Windows.Media.SolidColorBrush($fill) }
            $stroke = Color $style['stroke'] $current
            if ($null -ne $stroke) {
                $pen = New-Object Windows.Media.Pen((New-Object Windows.Media.SolidColorBrush($stroke)), (Number $style['stroke-width'] 1))
                $cap = 'Flat'
                if ($style['stroke-linecap'] -eq 'round') { $cap = 'Round' } elseif ($style['stroke-linecap'] -eq 'square') { $cap = 'Square' }
                $pen.StartLineCap = $cap
                $pen.EndLineCap = $cap
                $pen.DashCap = $cap
                $join = 'Miter'
                if ($style['stroke-linejoin'] -eq 'round') { $join = 'Round' } elseif ($style['stroke-linejoin'] -eq 'bevel') { $join = 'Bevel' }
                $pen.LineJoin = $join
            }
            $context.DrawGeometry($brush, $pen, $geometry)
        }
    }
    for ($i = 0; $i -lt $pushed; $i++) { $context.Pop() }
}

function Load([string]$path) {
    $document = New-Object Xml.XmlDocument
    $document.Load($path)
    $root = $document.DocumentElement
    $box = @(0, 0, 24, 24)
    if ($root.HasAttribute('viewBox')) { $box = @([regex]::Split($root.GetAttribute('viewBox').Trim(), '[\s,]+') | ForEach-Object { Number $_ }) }
    $clips = @{}
    foreach ($clip in $root.GetElementsByTagName('clipPath')) {
        $group = New-Object Windows.Media.GeometryGroup
        foreach ($shape in $clip.ChildNodes) {
            if ($shape.NodeType -ne 'Element') { continue }
            $rule = 'nonzero'
            if ($shape.HasAttribute('clip-rule')) { $rule = $shape.GetAttribute('clip-rule') }
            $group.Children.Add((Geometry $shape $rule))
        }
        $clips[$clip.GetAttribute('id')] = $group
    }
    return @{ Root = $root; Box = $box; Clips = $clips }
}

# One source drawn at (x, 0), scaled to size px.
function Paint($context, $svg, [double]$x, [int]$size, [Windows.Media.Color]$current) {
    $scale = $size / $svg.Box[2]
    $context.PushTransform((New-Object Windows.Media.TranslateTransform($x, 0)))
    $context.PushTransform((New-Object Windows.Media.ScaleTransform($scale, $scale)))
    $context.PushTransform((New-Object Windows.Media.TranslateTransform(-$svg.Box[0], -$svg.Box[1])))
    $style = @{ 'fill' = '#000000'; 'fill-rule' = 'nonzero'; 'stroke' = 'none'; 'stroke-width' = '1'; 'stroke-linecap' = 'butt'; 'stroke-linejoin' = 'miter' }
    Draw $context $svg.Root $style $svg.Clips $current
    $context.Pop()
    $context.Pop()
    $context.Pop()
}

function Render([int]$width, [int]$height, [scriptblock]$paint) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    & $paint $context
    $context.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap.Freeze()
    return $bitmap
}

function Png($bitmap) {
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

# Classic 32-bit DIB entries below 256 px for every reader, a PNG entry at 256 px.
function Dib($bitmap, [int]$size) {
    $straight = New-Object Windows.Media.Imaging.FormatConvertedBitmap($bitmap, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $pixels = New-Object byte[] ($size * $size * 4)
    $straight.CopyPixels($pixels, $size * 4, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter($stream)
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2)); $writer.Write([int16]1); $writer.Write([int16]32)
    $writer.Write([int]0); $writer.Write([int]($size * $size * 4 + $maskStride * $size)); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    for ($row = $size - 1; $row -ge 0; $row--) { $writer.Write($pixels, $row * $size * 4, $size * 4) }
    for ($row = $size - 1; $row -ge 0; $row--) {
        $mask = New-Object byte[] $maskStride
        for ($column = 0; $column -lt $size; $column++) {
            if ($pixels[($row * $size + $column) * 4 + 3] -eq 0) {
                $index = [int][Math]::Floor($column / 8)
                $mask[$index] = $mask[$index] -bor (0x80 -shr ($column % 8))
            }
        }
        $writer.Write($mask)
    }
    $writer.Flush()
    return , $stream.ToArray()
}

function Ico([string]$path, $svg) {
    $images = New-Object Collections.Generic.List[byte[]]
    foreach ($size in $IconSizes) {
        $bitmap = Render $size $size { param($context) Paint $context $svg 0 $size ([Windows.Media.Colors]::Black) }
        if ($size -ge 256) { $images.Add((Png $bitmap)) } else { $images.Add((Dib $bitmap $size)) }
    }
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter($stream)
    $writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$IconSizes.Count)
    $offset = 6 + 16 * $IconSizes.Count
    for ($i = 0; $i -lt $IconSizes.Count; $i++) {
        $edge = $IconSizes[$i] % 256
        $writer.Write([byte]$edge); $writer.Write([byte]$edge); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]$images[$i].Length); $writer.Write([int]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
    $writer.Flush()
    [IO.File]::WriteAllBytes($path, $stream.ToArray())
}

$app = Load (Join-Path $Assets 'icon\codexish.svg')
Ico (Join-Path $Out 'codexish.ico') $app
foreach ($size in 64, 256) {
    $bitmap = Render $size $size { param($context) Paint $context $app 0 $size ([Windows.Media.Colors]::Black) }
    [IO.File]::WriteAllBytes((Join-Path $Out "codexish-$size.png"), (Png $bitmap))
}
foreach ($state in 'running', 'stopped', 'attention') {
    Ico (Join-Path $Out "tray-$state.ico") (Load (Join-Path $Assets "icon\tray-$state.svg"))
}

$sources = @(Get-ChildItem (Join-Path $Assets 'lucide') -Filter *.svg | Sort-Object Name)
$glyphs = @($sources | ForEach-Object { Load $_.FullName })
$names = (($sources | ForEach-Object { $_.BaseName }) -join "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $Out 'glyphs.txt'), $names, (New-Object Text.UTF8Encoding($false)))
foreach ($size in $GlyphSizes) {
    $sheet = Render ($size * $glyphs.Count) $size {
        param($context)
        for ($i = 0; $i -lt $glyphs.Count; $i++) { Paint $context $glyphs[$i] ($i * $size) $size ([Windows.Media.Colors]::Black) }
    }
    [IO.File]::WriteAllBytes((Join-Path $Out "glyphs-$size.png"), (Png $sheet))
}
Write-Host "Wrote $Out"
