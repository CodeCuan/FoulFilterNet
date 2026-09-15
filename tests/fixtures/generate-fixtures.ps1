<#
.SYNOPSIS
  Generates the media fixtures used by FoulFilterNet's integration tests.

.DESCRIPTION
  Every fixture is assembled from individually synthesised parts that are
  concatenated in order. Each profanity is its own part, so its start and end
  time are known *by construction* — they are the running sum of the durations
  before it — rather than estimated. That is what makes the manifest usable for
  scoring boundary error and not just recall.

  This mirrors how the Legacy eval harness (scripts/eval_misses.py) worked: it
  spliced known clips into clean audio at known offsets. Measuring a spoken
  prefix separately does not work, because SAPI pads every fragment with
  silence and re-prosodies it as a complete sentence.

  Uses Windows SAPI (System.Speech) for TTS and FFmpeg for muxing. Both are
  already prerequisites on this machine; nothing is downloaded.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests/fixtures/generate-fixtures.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$Voice = 'Microsoft Zira Desktop',
    [double]$GapSeconds = 0.4
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech

if (-not $OutputDir) {
    $root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $OutputDir = Join-Path $root 'media'
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("ff-fixtures-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work, $OutputDir | Out-Null

# --- primitives --------------------------------------------------------------

function New-Utterance {
    param([string]$Text, [string]$Path)

    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $synth.SelectVoice($Voice)
        $synth.Rate = 0
        $synth.SetOutputToWaveFile($Path)
        $synth.Speak($Text)
        $synth.SetOutputToNull()
    }
    finally { $synth.Dispose() }
}

function Get-Duration {
    param([string]$Path)
    $d = & ffprobe -v error -show_entries format=duration -of csv=p=0 $Path
    [double]::Parse($d, [System.Globalization.CultureInfo]::InvariantCulture)
}

# SAPI brackets every fragment with silence. Left in place, that silence would
# be attributed to the spoken part and inflate its measured span, so each part
# is trimmed to its actual speech before anything is timed.
function New-TrimmedUtterance {
    param([string]$Text, [string]$Path)

    $raw = Join-Path $work ("raw-" + [guid]::NewGuid().ToString('N') + '.wav')
    New-Utterance -Text $Text -Path $raw
    & ffmpeg -y -loglevel error -i $raw `
        -af "silenceremove=start_periods=1:start_silence=0.02:start_threshold=-45dB:detection=peak,areverse,silenceremove=start_periods=1:start_silence=0.02:start_threshold=-45dB:detection=peak,areverse" `
        -ar 22050 -ac 1 -acodec pcm_s16le $Path
    Remove-Item $raw -Force -ErrorAction SilentlyContinue
}

# --- fixture assembly --------------------------------------------------------

# A fixture is a list of lines; a line is a list of parts. Parts within a line
# are butt-joined with no gap; lines are separated by $GapSeconds of silence.
# A part carrying a 'hit' key contributes its exact span to the ground truth.
function New-Fixture {
    param(
        [string]$Name,
        [string]$Extension,
        [object[]]$Lines,
        [string[]]$ExtraFFmpegArgs = @(),
        [string]$Kind = 'audio'
    )

    Write-Host ("  {0,-22}" -f "$Name$Extension") -NoNewline

    $segments = @()   # ordered list of wav paths to concatenate
    $truth = @()
    $cursor = 0.0

    $silence = Join-Path $work 'gap.wav'
    if (-not (Test-Path $silence)) {
        & ffmpeg -y -loglevel error -f lavfi -i "anullsrc=r=22050:cl=mono" `
            -t $GapSeconds -ar 22050 -ac 1 -acodec pcm_s16le $silence
    }

    $lineIndex = 0
    foreach ($line in $Lines) {
        foreach ($part in $line.parts) {
            $wav = Join-Path $work ("p-{0:d4}.wav" -f $segments.Count)
            New-TrimmedUtterance -Text $part.t -Path $wav
            $len = Get-Duration $wav

            if ($part.hit) {
                $truth += [pscustomobject]@{
                    phrase = ([string]$part.hit).ToLowerInvariant()
                    start  = [math]::Round($cursor, 3)
                    end    = [math]::Round($cursor + $len, 3)
                }
            }

            $segments += $wav
            $cursor += $len
        }

        $lineIndex++
        if ($lineIndex -lt $Lines.Count) {
            $segments += $silence
            $cursor += $GapSeconds
        }
    }

    $listFile = Join-Path $work "concat-$Name.txt"
    Set-Content -Path $listFile -Encoding ascii `
        -Value ($segments | ForEach-Object { "file '$($_ -replace '\\', '/')'" })

    $joined = Join-Path $work "$Name-joined.wav"
    & ffmpeg -y -loglevel error -f concat -safe 0 -i $listFile -ar 16000 -ac 1 -acodec pcm_s16le $joined

    $out = Join-Path $OutputDir "$Name$Extension"
    $ffArgs = @('-y', '-loglevel', 'error', '-i', $joined) + $ExtraFFmpegArgs + @($out)
    & ffmpeg @ffArgs

    $duration = Get-Duration $out
    Write-Host (" {0,5:n1}s  {1,5:n0} KB  {2} hit(s)" -f $duration, ((Get-Item $out).Length / 1KB), $truth.Count)

    [pscustomobject]@{
        file     = "$Name$Extension"
        kind     = $Kind
        duration = [math]::Round($duration, 3)
        hits     = @($truth)
    }
}

function Line { param([object[]]$Parts) @{ parts = $Parts } }
function Say  { param([string]$Text) @{ t = $Text } }
function Swear {
    param([string]$Text, [string]$As)
    $phrase = $As
    if (-not $phrase) { $phrase = $Text }
    @{ t = $Text; hit = $phrase }
}

# --- the fixtures ------------------------------------------------------------

Write-Host "Generating fixtures into $OutputDir`n"
$mp3 = @('-codec:a', 'libmp3lame', '-b:a', '96k')
$manifest = @()

# 1. No profanity: exercises the copy-through path when there are no hits.
$manifest += New-Fixture -Name 'clean_speech' -Extension '.mp3' -ExtraFFmpegArgs $mp3 -Lines @(
    (Line @( (Say 'This is a perfectly ordinary recording with nothing objectionable in it.') )),
    (Line @( (Say 'The weather today is mild and the garden needs watering.') ))
)

# 2. A single single-word hit.
$manifest += New-Fixture -Name 'single_hit' -Extension '.mp3' -ExtraFFmpegArgs $mp3 -Lines @(
    (Line @( (Say 'This is a test recording for the profanity filter.') )),
    (Line @( (Say 'Well'), (Swear 'damn'), (Say 'that was completely unexpected.') )),
    (Line @( (Say 'Everything else here is perfectly clean.') ))
)

# 3. A multi-word phrase. The matcher must prefer 'go to hell' over the bare
#    'hell' nested inside it.
$manifest += New-Fixture -Name 'phrase_hit' -Extension '.mp3' -ExtraFFmpegArgs $mp3 -Lines @(
    (Line @( (Say 'He told me to leave and never come back.') )),
    (Line @( (Say 'I said he can'), (Swear 'go to hell'), (Say 'for all I care.') )),
    (Line @( (Say 'Then I walked away and did not look back.') ))
)

# 4. The same word five times. Regression cover for finding 2: the Python
#    reconciles candidates to hits by phrase string and loses the repeats.
$manifest += New-Fixture -Name 'repeated_hits' -Extension '.mp3' -ExtraFFmpegArgs $mp3 -Lines @(
    (Line @( (Swear 'Damn' 'damn'), (Say 'the first one went wrong.') )),
    (Line @( (Swear 'Damn' 'damn'), (Say 'the second one went wrong too.') )),
    (Line @( (Swear 'Damn' 'damn'), (Say 'and the third one as well.') )),
    (Line @( (Swear 'Damn' 'damn'), (Say 'the fourth was no better.') )),
    (Line @( (Swear 'Damn' 'damn'), (Say 'and the fifth finished the job.') ))
)

# 5. False positive: 'hoe' the garden tool. Detection must flag it and Smart
#    Cut must reject it, so the span is ground truth for a hit that should NOT
#    survive refinement.
$manifest += New-Fixture -Name 'false_positive' -Extension '.mp3' -ExtraFFmpegArgs $mp3 -Lines @(
    (Line @( (Say 'After the rain stopped, the farmer grabbed a'), (Swear 'hoe'), (Say 'to fix the flower beds.') ))
)

# 6. Video: frames must survive the edit untouched, and 'remove' must fall back
#    to 'silence' (ADR-0004).
$manifest += New-Fixture -Name 'sample_video' -Extension '.mp4' -Kind 'video' -ExtraFFmpegArgs @(
    '-f', 'lavfi', '-i', 'testsrc2=size=320x240:rate=15',
    '-shortest', '-codec:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p',
    '-codec:a', 'aac', '-b:a', '96k'
) -Lines @(
    (Line @( (Say 'This clip has a video track as well as speech.') )),
    (Line @( (Swear 'Damn' 'damn'), (Say 'the picture must survive the edit untouched.') ))
)

# 7. Audiobook container: m4b is what the tool was built for, and the format
#    libmagic reported inconsistently (finding 7).
$manifest += New-Fixture -Name 'audiobook' -Extension '.m4b' -ExtraFFmpegArgs @('-codec:a', 'aac', '-b:a', '64k') -Lines @(
    (Line @( (Say 'Chapter one. The long road out of town.') )),
    (Line @( (Say 'It was, he thought, a'), (Swear 'damn'), (Say 'poor way to begin a journey.') )),
    (Line @( (Say 'The cart wheels turned slowly in the morning light.') )),
    (Line @( (Say 'He could'), (Swear 'go to hell'), (Say 'and the horse with him.') ))
)

# --- manifest ----------------------------------------------------------------

$manifestPath = Join-Path $OutputDir 'manifest.json'
[pscustomobject]@{
    generatedBy = 'tests/fixtures/generate-fixtures.ps1'
    voice       = $Voice
    gapSeconds  = $GapSeconds
    note        = 'Each profanity was synthesised as its own concatenated part, so start/end are exact by construction - the running sum of preceding part durations. Use them to score both detection recall and boundary error.'
    fixtures    = $manifest
} | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding utf8

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

Write-Host "`nWrote $($manifest.Count) fixtures + manifest.json to $OutputDir"
