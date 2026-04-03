param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot "..\..\LilAgents"),
    [string]$TargetRoot = (Join-Path $PSScriptRoot "..\Resources"),
    [string]$FfmpegPath = ""
)

$ErrorActionPreference = "Stop"

function Resolve-Ffmpeg {
    param([string]$Preferred)

    if ($Preferred -and (Test-Path -LiteralPath $Preferred)) {
        return (Resolve-Path -LiteralPath $Preferred).Path
    }

    $pythonFfmpeg = Get-ChildItem -Path (Join-Path $env:APPDATA "Python") -Recurse -Filter "ffmpeg-*.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($pythonFfmpeg) {
        return $pythonFfmpeg.FullName
    }

    $ffmpegCmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($ffmpegCmd) {
        return $ffmpegCmd.Source
    }

    throw "ffmpeg was not found. Install ffmpeg or pass -FfmpegPath."
}

$resolvedSourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
if (-not (Test-Path -LiteralPath $TargetRoot)) {
    New-Item -ItemType Directory -Path $TargetRoot | Out-Null
}
$resolvedTargetRoot = (Resolve-Path -LiteralPath $TargetRoot).Path
$ffmpeg = Resolve-Ffmpeg -Preferred $FfmpegPath

Write-Host "Using ffmpeg: $ffmpeg"
Write-Host "Source root: $resolvedSourceRoot"
Write-Host "Target root: $resolvedTargetRoot"

$spritesRoot = Join-Path $resolvedTargetRoot "Sprites"
$soundsRoot = Join-Path $resolvedTargetRoot "Sounds"
New-Item -ItemType Directory -Path $spritesRoot -Force | Out-Null
New-Item -ItemType Directory -Path $soundsRoot -Force | Out-Null

$videos = @(
    @{ Name = "walk-bruce-01.mov"; Output = "walk-bruce-01" },
    @{ Name = "walk-jazz-01.mov"; Output = "walk-jazz-01" }
)

foreach ($video in $videos) {
    $videoPath = Join-Path $resolvedSourceRoot $video.Name
    if (-not (Test-Path -LiteralPath $videoPath)) {
        throw "Missing source video: $videoPath"
    }

    $outputDir = Join-Path $spritesRoot $video.Output
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

    Get-ChildItem -Path $outputDir -Filter "frame_*.png" -ErrorAction SilentlyContinue |
        Remove-Item -Force

    $outputPattern = Join-Path $outputDir "frame_%04d.png"

    & $ffmpeg -hide_banner -y `
        -i $videoPath `
        -vf "colorkey=0x000000:0.035:0.012,scale=270:480:flags=lanczos,format=rgba,fps=24" `
        -frames:v 240 `
        $outputPattern

    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed for $videoPath"
    }
}

$sourceSounds = Join-Path $resolvedSourceRoot "Sounds"
if (-not (Test-Path -LiteralPath $sourceSounds)) {
    throw "Missing source sounds directory: $sourceSounds"
}

Copy-Item -Path (Join-Path $sourceSounds "*") -Destination $soundsRoot -Recurse -Force

Write-Host "Sprite and sound extraction completed successfully."
