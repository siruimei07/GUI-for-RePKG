[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [string] $ExecutablePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\Release\net10.0-windows\WallpaperField.exe'
}
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('WallpaperField-preview-' + [Guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $fixtureRoot 'source'
$libraryRoot = Join-Path $fixtureRoot 'output'
[IO.Directory]::CreateDirectory($sourceRoot) | Out-Null
[IO.Directory]::CreateDirectory($libraryRoot) | Out-Null

# Original abstract covers are test fixtures, not bundled wallpaper content.
Add-Type -AssemblyName System.Drawing
$titles = @('Northern lights', 'Midnight city', 'Ocean blue', 'Solar bloom', 'Cloud atlas', 'Neon horizon', 'Quiet forest', 'Desert dusk', 'Crystal lake', 'After the rain', 'Lunar orbit', 'Violet waves')
$colors = @('#34537f', '#6b467e', '#196c83', '#b26738', '#5c7595', '#7c3e72', '#29685d', '#a55c49', '#287b9b', '#466b76', '#4d5289', '#725794')
try {
    for ($index = 0; $index -lt 36; $index++) {
        $projectRoot = Join-Path $sourceRoot ([string](91000 + $index))
        [IO.Directory]::CreateDirectory($projectRoot) | Out-Null
        $project = @{ title = $titles[$index % $titles.Count]; workshopid = [string](91000 + $index); type = 'scene'; file = 'scene.json' }
        [IO.File]::WriteAllText((Join-Path $projectRoot 'project.json'), ($project | ConvertTo-Json))
        # A valid empty package is sufficient for read-only scanning and the READY badge.
        $package = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $projectRoot 'scene.pkg')))
        try {
            $magic = [Text.Encoding]::ASCII.GetBytes('PKGV0001')
            $package.Write([int]$magic.Length)
            $package.Write($magic)
            $package.Write([int]0)
        } finally { $package.Dispose() }
        $bitmap = [Drawing.Bitmap]::new(480, 480)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $gradient = $null
        $brush = $null
        $pen = $null
        try {
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $baseColor = [Drawing.ColorTranslator]::FromHtml($colors[$index % $colors.Count])
            $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(0, 0), [Drawing.Point]::new(480, 480), $baseColor, [Drawing.Color]::FromArgb(12, 19, 35))
            $graphics.FillRectangle($gradient, 0, 0, 480, 480)
            $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(140, 225, 235, 255))
            $size = 80 + ($index % 4) * 32
            $graphics.FillEllipse($brush, 230 - ($index % 3) * 42, 62 + ($index % 4) * 20, $size, $size)
            $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(45, 225, 235, 255), 2)
            for ($line = 0; $line -lt 9; $line++) {
                $graphics.DrawBezier($pen, -30, 270 + $line * 22, 120, 160 + $line * 24, 310, 470 - $line * 18, 510, 280 + $line * 20)
            }
            $bitmap.Save((Join-Path $projectRoot 'preview.png'), [Drawing.Imaging.ImageFormat]::Png)
        } finally {
            if ($pen) { $pen.Dispose() }
            if ($brush) { $brush.Dispose() }
            if ($gradient) { $gradient.Dispose() }
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
    foreach ($size in @(@{Name='wide'; Width=1480; Height=900}, @{Name='compact'; Width=920; Height=680})) {
        $snapshot = Join-Path $output ('browser-' + $size.Name + '.png')
        $arguments = '--source "{0}" --output "{1}" --scan --page browse --snapshot "{2}" --width {3} --height {4} --reduced-motion' -f $sourceRoot, $libraryRoot, $snapshot, $size.Width, $size.Height
        $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -WindowStyle Hidden
        try {
            if (-not $process.WaitForExit(60000)) {
                $process.Kill()
                $process.WaitForExit()
                throw "Preview $($size.Name) did not exit within 60 seconds."
            }
            if ($process.ExitCode -ne 0) { throw "Preview $($size.Name) exited $($process.ExitCode)." }
            if (-not (Test-Path -LiteralPath $snapshot -PathType Leaf) -or (Get-Item -LiteralPath $snapshot).Length -eq 0) {
                throw "Preview $($size.Name) did not produce an image."
            }
            Write-Output "BROWSER_PREVIEW name=$($size.Name) result=PASS path=$snapshot"
        } finally { $process.Dispose() }
    }
} finally {
    # This unique directory is created and owned only by this invocation.
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
