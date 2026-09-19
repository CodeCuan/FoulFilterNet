<#
.SYNOPSIS
  Builds a media directory for the W16 end-to-end harness: the fixtures, plus
  one longer video made from them, with a manifest covering both.

.DESCRIPTION
  The fixtures in tests/fixtures/media are all shorter than one 28 s
  transcription window, so on their own they cannot show a seek ahead of the
  coverage, windows transcribed out of order, or the hold-then-release of a
  first view on more than one window. This script copies them into
  -OutputDir and adds long_video.mp4: the fixtures' audio, one after another,
  twice (about two minutes, five or six windows), under a test-pattern
  picture.

  The ground truth stays exact: each part is decoded to 48 kHz PCM first, so
  its offset in the long file is the running sum of whole samples, and every
  span is its fixture's span moved by that offset.

  Point Watch:DevFileProvider:Directory at -OutputDir. The directory is left
  outside the repository on purpose: the long video is generated, not a
  fixture.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests/fixtures/generate-harness-media.ps1 -OutputDir $env:TEMP\ffn-harness-media
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDir,
    [int]$Repeats = 2
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$source = Join-Path $here 'media'
$rate = 48000

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("ff-harness-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    $manifest = Get-Content -Raw -Path (Join-Path $source 'manifest.json') | ConvertFrom-Json
    foreach ($fixture in $manifest.fixtures) {
        Copy-Item -Force -Path (Join-Path $source $fixture.file) -Destination $OutputDir
    }

    # The long video is every fixture in the manifest's order, $Repeats times.
    $parts = @()
    $truth = @()
    $samples = [long]0
    for ($round = 0; $round -lt $Repeats; $round++) {
        foreach ($fixture in $manifest.fixtures) {
            $wav = Join-Path $work ("part-{0:d3}.wav" -f $parts.Count)
            & ffmpeg -y -loglevel error -i (Join-Path $source $fixture.file) -vn -ar $rate -ac 1 -acodec pcm_s16le $wav
            if ($LASTEXITCODE -ne 0) { throw "ffmpeg could not decode $($fixture.file)" }

            $offset = $samples / [double]$rate
            foreach ($hit in $fixture.hits) {
                $truth += [pscustomobject]@{
                    phrase = $hit.phrase
                    start  = [math]::Round($offset + $hit.start, 3)
                    end    = [math]::Round($offset + $hit.end, 3)
                }
            }

            # The part's exact length in samples (ffmpeg's WAV header is not always 44 bytes).
            $count = & ffprobe -v error -select_streams a:0 -show_entries stream=duration_ts -of csv=p=0 $wav
            $samples += [long]::Parse(($count | Select-Object -First 1).Trim())
            $parts += $wav
        }
    }

    $list = Join-Path $work 'concat.txt'
    Set-Content -Path $list -Encoding ascii -Value ($parts | ForEach-Object { "file '$($_ -replace '\\', '/')'" })
    $joined = Join-Path $work 'joined.wav'
    & ffmpeg -y -loglevel error -f concat -safe 0 -i $list -c copy $joined
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg could not join the parts' }

    $long = Join-Path $OutputDir 'long_video.mp4'
    & ffmpeg -y -loglevel error -f lavfi -i 'testsrc2=size=320x240:rate=15' -i $joined `
        -shortest -codec:v libx264 -preset ultrafast -pix_fmt yuv420p -codec:a aac -b:a 128k $long
    if ($LASTEXITCODE -ne 0) { throw 'ffmpeg could not write long_video.mp4' }

    $duration = [math]::Round($samples / [double]$rate, 3)
    $fixtures = @($manifest.fixtures) + [pscustomobject]@{
        file     = 'long_video.mp4'
        kind     = 'video'
        duration = $duration
        hits     = @($truth)
    }

    [pscustomobject]@{
        generatedBy = 'tests/fixtures/generate-harness-media.ps1'
        note        = "The fixtures from tests/fixtures/media, plus long_video.mp4: their audio decoded to $rate Hz and joined $Repeats times, spans moved by whole-sample offsets."
        fixtures    = $fixtures
    } | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutputDir 'manifest.json') -Encoding utf8

    Write-Host ("Wrote {0} fixtures and long_video.mp4 ({1:n1} s, {2} spans) to {3}" -f $manifest.fixtures.Count, $duration, $truth.Count, $OutputDir)
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
