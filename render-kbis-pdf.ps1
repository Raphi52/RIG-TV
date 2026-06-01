# render-kbis-pdf.ps1 - rend le VRAI PDF genere par RIG en PNG via le moteur PDF integre
# a Windows (Windows.Data.Pdf). Headless, deterministe, independant de tout desktop/compositeur :
# c'est pourquoi il marche la ou screenshoter le viewer AcroPDF echoue (HDESK non composite par DWM).
# Rend TOUTES les pages, empilees verticalement, dans un seul PNG.
# Appele par le worker SmokeRunner apres verification du contenu (PdfPig).
# PS 5.1 : pas d'em-dash, pas de ternaire.
param(
    [Parameter(Mandatory = $true)] [string] $PdfPath,
    [Parameter(Mandatory = $true)] [string] $OutPng,
    [int] $Width = 1240
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $PdfPath)) { throw "PDF introuvable : $PdfPath" }

Add-Type -AssemblyName System.Runtime.WindowsRuntime
Add-Type -AssemblyName System.Drawing

# Helpers pour attendre les operations asynchrones WinRT depuis PowerShell 5.1.
$asTaskOp = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
$asTaskAct = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' })[0]
function AwaitOp($op, $t) { $task = $asTaskOp.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
function AwaitAct($act) { $task = $asTaskAct.Invoke($null, @($act)); $task.Wait(-1) | Out-Null }

[void][Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
[void][Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime]
[void][Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType = WindowsRuntime]
[void][Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]

$file = AwaitOp ([Windows.Storage.StorageFile]::GetFileFromPathAsync($PdfPath)) ([Windows.Storage.StorageFile])
$doc  = AwaitOp ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
if ($doc.PageCount -le 0) { throw "PDF sans page : $PdfPath" }

$imgs = New-Object System.Collections.ArrayList
for ($i = 0; $i -lt $doc.PageCount; $i++) {
    $page = $doc.GetPage([uint32]$i)
    $stream = [Windows.Storage.Streams.InMemoryRandomAccessStream]::new()
    $opt = [Windows.Data.Pdf.PdfPageRenderOptions]::new()
    $opt.DestinationWidth = [uint32]$Width
    AwaitAct ($page.RenderToStreamAsync($stream, $opt))
    $netIn = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($stream.GetInputStreamAt(0))
    $src = [System.Drawing.Image]::FromStream($netIn)
    $img = New-Object System.Drawing.Bitmap $src
    $src.Dispose(); $netIn.Dispose()
    [void]$imgs.Add($img)
}

# Empile les pages verticalement (8 px de marge blanche entre pages).
$gap = 8
$maxW = 0; $totalH = 0
foreach ($im in $imgs) { if ($im.Width -gt $maxW) { $maxW = $im.Width }; $totalH += $im.Height }
$totalH += $gap * [Math]::Max(0, $imgs.Count - 1)
$bmp = New-Object System.Drawing.Bitmap $maxW, $totalH
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$y = 0
foreach ($im in $imgs) { $g.DrawImage($im, 0, $y, $im.Width, $im.Height); $y += $im.Height + $gap }
$g.Dispose()

$dir = [System.IO.Path]::GetDirectoryName($OutPng)
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
foreach ($im in $imgs) { $im.Dispose() }
Write-Output ("RENDERED {0} page(s) -> {1}" -f $doc.PageCount, $OutPng)
