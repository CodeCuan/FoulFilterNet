<#
.SYNOPSIS
  Generates crosstalk fixtures: two speakers talking over each other, with
  profanity planted in the overlap.

.DESCRIPTION
  Each fixture is a set of utterances, each placed at an absolute time on one
  of two speakers' tracks, then mixed. Speaker B is a second SAPI voice pitched
  down so the two are distinct. Every utterance is assembled from individually
  synthesised parts (as generate-fixtures.ps1 does), so a profanity's span is
  exact by construction: the utterance's start plus the parts before it.

  Output is its own manifest, so the evaluator can score it separately:

    dotnet run --project src/FoulFilterNet.Evaluation -- --manifest tests/fixtures/crosstalk/manifest.json

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests/fixtures/generate-crosstalk-fixtures.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$VoiceA = 'Microsoft Zira Desktop',
    [string]$VoiceB = 'Microsoft Hazel Desktop'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech

if (-not $OutputDir) {
    $root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $OutputDir = Join-Path $root 'crosstalk'
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("ff-crosstalk-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work, $OutputDir | Out-Null
$inv = [System.Globalization.CultureInfo]::InvariantCulture

# --- primitives --------------------------------------------------------------

function Get-Duration {
    param([string]$Path)
    $d = & ffprobe -v error -show_entries format=duration -of csv=p=0 $Path
    [double]::Parse($d, $inv)
}

# One trimmed part in one voice. Speaker B is pitched down ~12% without
# changing its duration, so timings stay exact.
function New-Part {
    param([string]$Text, [string]$Speaker, [string]$Path)

    $raw = Join-Path $work ("raw-" + [guid]::NewGuid().ToString('N') + '.wav')
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $synth.SelectVoice($(if ($Speaker -eq 'B') { $VoiceB } else { $VoiceA }))
        $synth.Rate = 0
        $synth.SetOutputToWaveFile($raw)
        $synth.Speak($Text)
        $synth.SetOutputToNull()
    }
    finally { $synth.Dispose() }

    $trim = "silenceremove=start_periods=1:start_silence=0.02:start_threshold=-45dB:detection=peak,areverse,silenceremove=start_periods=1:start_silence=0.02:start_threshold=-45dB:detection=peak,areverse"
    $filter = if ($Speaker -eq 'B') {
        "$trim,aresample=22050,asetrate=22050*0.88,aresample=22050,atempo=1/0.88"
    } else { "$trim,aresample=22050" }

    & ffmpeg -y -loglevel error -i $raw -af $filter -ar 22050 -ac 1 -acodec pcm_s16le $Path
    Remove-Item $raw -Force -ErrorAction SilentlyContinue
}

# An utterance: parts butt-joined, returned with its length and hit offsets.
function New-Utterance {
    param([hashtable]$U, [string]$Tag)

    $parts = @()
    $hits = @()
    $cursor = 0.0
    $i = 0
    foreach ($p in $U.parts) {
        $wav = Join-Path $work ("$Tag-{0:d2}.wav" -f $i)
        New-Part -Text $p.t -Speaker $U.speaker -Path $wav
        $len = Get-Duration $wav
        if ($p.hit) {
            $hits += [pscustomobject]@{ phrase = $p.hit; start = $cursor; end = $cursor + $len }
        }
        $parts += $wav
        $cursor += $len
        $i++
    }

    $list = Join-Path $work "$Tag.txt"
    Set-Content -Path $list -Encoding ascii -Value ($parts | ForEach-Object { "file '$($_ -replace '\\', '/')'" })
    $joined = Join-Path $work "$Tag.wav"
    & ffmpeg -y -loglevel error -f concat -safe 0 -i $list -acodec pcm_s16le $joined

    [pscustomobject]@{ path = $joined; length = $cursor; hits = $hits }
}

function New-Fixture {
    param([string]$Name, [object[]]$Utterances)

    Write-Host ("  {0,-26}" -f "$Name.mp3") -NoNewline

    $inputs = @()
    $chains = @()
    $truth = @()
    $k = 0
    $previousEnd = 0.0
    foreach ($u in $Utterances) {
        $rendered = New-Utterance -U $u -Tag "$Name-u$k"
        # An utterance with an overlap starts that long before the previous
        # one ends, however long that turned out to be.
        if ($null -ne $u.overlap) { $u.at = [math]::Max(0.0, $previousEnd - $u.overlap) }
        $previousEnd = $u.at + $rendered.length
        $delay = [int][math]::Round($u.at * 1000)
        $gain = if ($u.gain) { $u.gain } else { 0 }
        $inputs += @('-i', $rendered.path)
        $chains += ("[{0}:a]adelay={1}|{1},volume={2}dB[a{0}]" -f $k, $delay, $gain.ToString($inv))
        foreach ($h in $rendered.hits) {
            $truth += [pscustomobject]@{
                phrase = $h.phrase
                start  = [math]::Round($u.at + $h.start, 3)
                end    = [math]::Round($u.at + $h.end, 3)
            }
        }
        $k++
    }

    $labels = (0..($k - 1) | ForEach-Object { "[a$_]" }) -join ''
    $graph = ($chains -join ';') + ";${labels}amix=inputs=${k}:duration=longest:normalize=0,alimiter=limit=0.95[out]"

    $out = Join-Path $OutputDir "$Name.mp3"
    $ffArgs = @('-y', '-loglevel', 'error') + $inputs + @('-filter_complex', $graph, '-map', '[out]', '-ar', '22050', '-ac', '1', '-codec:a', 'libmp3lame', '-b:a', '96k', $out)
    & ffmpeg @ffArgs

    $duration = Get-Duration $out
    Write-Host (" {0,5:n1}s  {1} hit(s)" -f $duration, $truth.Count)

    [pscustomobject]@{
        file     = "$Name.mp3"
        kind     = 'audio'
        duration = [math]::Round($duration, 3)
        hits     = @($truth | Sort-Object start)
    }
}

function A { param([double]$At, [object[]]$Parts, [double]$Gain = 0) @{ speaker = 'A'; at = $At; parts = $Parts; gain = $Gain } }
function B { param([double]$At, [object[]]$Parts, [double]$Gain = 0) @{ speaker = 'B'; at = $At; parts = $Parts; gain = $Gain } }
function Over { param([string]$Speaker, [double]$Overlap, [object[]]$Parts, [double]$Gain = 0) @{ speaker = $Speaker; at = 0.0; overlap = $Overlap; parts = $Parts; gain = $Gain } }
function Say { param([string]$Text) @{ t = $Text } }
function Swear { param([string]$Text, [string]$As) @{ t = $Text; hit = $(if ($As) { $As } else { $Text.ToLowerInvariant() }) } }

# --- the fixtures ------------------------------------------------------------

Write-Host "Generating crosstalk fixtures into $OutputDir`n"
$manifest = @()

# 1. A is mid-sentence; B interrupts at equal level and the F word lands
#    under A's speech.
$manifest += New-Fixture 'ct_interrupt_equal' @(
    (A 0.0 @( (Say 'So I was telling him that the meeting on Thursday has been moved to the afternoon, and that everyone needs to bring the quarterly numbers with them.') )),
    (B 3.2 @( (Say 'Oh come on, are you'), (Swear 'fucking'), (Say 'serious right now?') ))
)

# 2. The same, but the interrupter is quieter (-6 dB) - the usual case for an
#    aside or a co-host off mic.
$manifest += New-Fixture 'ct_interrupt_quiet' @(
    (A 0.0 @( (Say 'The recipe calls for two cups of flour, a pinch of salt, and about half a cup of warm water, mixed slowly until it comes together.') )),
    (B 3.0 @( (Say 'That is way too'), (Swear 'fucking'), (Say 'much salt.') ) -6)
)

# 3. The swear is the dominant speaker's; B talks over it.
$manifest += New-Fixture 'ct_dominant_swears' @(
    (A 0.0 @( (Say 'Honestly the traffic this morning was'), (Swear 'fucking'), (Say 'unbelievable, it took me an hour to get across the bridge.') )),
    (B 1.4 @( (Say 'Yeah I heard there was an accident near the tunnel entrance.') ) -3)
)

# 4. Normal conversation, one bare 'fuck' thrown in during a moderate overlap.
$manifest += New-Fixture 'ct_single_fuck' @(
    (A 0.0 @( (Say 'We should probably head out soon if we want to catch the early train.') )),
    (B 3.4 @( (Say 'Right, let me just grab my coat and my keys.') )),
    (A 6.6 @( (Say 'Did you remember to lock the back door this time?') )),
    (B 8.3 @( (Say 'Oh'), (Swear 'fuck'), (Say 'no, I completely forgot about it.') )),
    (A 9.6 @( (Say 'Okay well hurry up, we have about five minutes.') ) -2)
)

# 5. Overlapping speech with no profanity - anything flagged is a false positive.
$manifest += New-Fixture 'ct_clean_overlap' @(
    (A 0.0 @( (Say 'The garden looks lovely this year, the roses came back even stronger than last spring.') )),
    (B 2.8 @( (Say 'I think it was all that rain in April, it really helped everything grow.') ) -2)
)

# 6. A longer back-and-forth, several overlaps, several words from the list.
$manifest += New-Fixture 'ct_argument' @(
    (A 0.0  @( (Say 'I told you three times that the deadline was Friday, not Monday.') )),
    (B 3.0  @( (Say 'And I told you that is'), (Swear 'bullshit'), (Say 'because the email said Monday.') ) -2),
    (A 6.2  @( (Say 'Show me the email then, go on, show me where it says that.') )),
    (B 8.4  @( (Say 'Give me a second, I am'), (Swear 'fucking'), (Say 'looking for it.') )),
    (A 10.4 @( (Say 'This is exactly what happened last time with the budget report.') ) -1),
    (B 13.0 @( (Say 'Oh'), (Swear 'shit'), (Say 'you are right, it does say Friday.') )),
    (A 15.2 @( (Say 'Of course I am right, I am always right about these things.') ))
)

# 7. A long, calm stretch (crossing a 28 s window boundary) with one F word
#    overlapped near the boundary.
$manifest += New-Fixture 'ct_long_one_fword' @(
    (A 0.0  @( (Say 'Welcome back to the show. Today we are talking about home gardening, and in particular about how to keep tomatoes healthy through a hot summer.') )),
    (B 8.5  @( (Say 'Which is something I have failed at every single year, so I am here to learn.') )),
    (A 13.0 @( (Say 'Well the first thing is watering. Deep watering in the morning, a couple of times a week, is much better than a little every evening.') )),
    (B 21.5 @( (Say 'Really? I have always done it at night after work.') )),
    (A 24.0 @( (Say 'That is the most common mistake, the leaves stay wet all night and that invites disease.') )),
    (B 26.8 @( (Say 'Well that explains a'), (Swear 'fucking'), (Say 'lot about my garden.') ) -3),
    (A 31.0 @( (Say 'The second thing is mulch, a good layer keeps the roots cool and the soil moist.') ))
)

# 8. A sweep of interruptions: the same shape at three interrupter levels and
#    several placements, so a change is judged on more than a handful of words.
$hosts = @(
    'I think the most important thing about this season is that the whole team finally started playing for each other instead of for themselves.',
    'When we visited the museum last weekend the guide spent almost twenty minutes explaining how the old clock tower was built.',
    'The plan for next month is to finish the kitchen first, then move on to the bathroom once the new tiles arrive from the supplier.',
    'According to the forecast it should stay dry until Wednesday, and then there is a band of heavy rain coming in from the west.'
)
$asides = @(
    @( (Say 'Yeah but they'), (Swear 'fucking'), (Say 'lost on Sunday.') ),
    @( (Say 'That was so'), (Swear 'fucking'), (Say 'boring.') ),
    @( (Say 'Oh'), (Swear 'fuck'), (Say 'I forgot to order them.') ),
    @( (Say 'What the'), (Swear 'fuck'), (Say 'is it again?') )
)
$levels = @(0, -3, -6)
for ($h = 0; $h -lt $hosts.Count; $h++) {
    foreach ($level in $levels) {
        $at = 2.2 + 0.6 * $h
        $manifest += New-Fixture ("ct_sweep_{0}_{1}db" -f $h, [math]::Abs($level)) @(
            (A 0.0 @( (Say $hosts[$h]) )),
            (B $at $asides[$h] $level)
        )
    }
}

# 9. Overlap with no profanity at all, for false positives a prompt might invite.
$clean = @(
    @('The train was delayed by twenty minutes because of a signal failure outside the station.', 'Oh no, did you miss your connection?'),
    @('We finally painted the spare room a pale shade of green and it looks much bigger now.', 'Green is such a good choice for a small room.'),
    @('The kids spent the whole afternoon building a fort out of cardboard boxes in the garden.', 'Honestly that sounds like the best afternoon ever.')
)
for ($c = 0; $c -lt $clean.Count; $c++) {
    $manifest += New-Fixture ("ct_clean_{0}" -f $c) @(
        (A 0.0 @( (Say $clean[$c][0]) )),
        (B 2.5 @( (Say $clean[$c][1]) ) -3)
    )
}

# 10. Edge overlap - "talking over each other a little bit": the next speaker
#     starts before the last has finished, and the swear falls in the overlap.
#     Either the newcomer swears straight away, or the one finishing swears as
#     they are talked over.
$edges = @(
    @('A', 'I really do not think we have time to stop for lunch before the meeting starts.', 'B', @( (Swear 'Fuck'), (Say 'that, I am starving.') )),
    @('A', 'So the plan is we leave at eight and get there before the traffic builds up.', 'B', @( (Say 'Oh'), (Swear 'fuck'), (Say 'off, eight is way too early.') )),
    @('A', 'Anyway that is why I ended up missing the last train home and walking.', 'B', @( (Say 'Are you'), (Swear 'fucking'), (Say 'kidding me?') ))
)
foreach ($overlap in @(0.3, 0.6, 1.0)) {
    for ($e = 0; $e -lt $edges.Count; $e++) {
        $manifest += New-Fixture ("ct_edge_{0}_{1}ms" -f $e, [int]($overlap * 1000)) @(
            (A 0.0 @( (Say $edges[$e][1]) )),
            (Over 'B' $overlap $edges[$e][3])
        )
    }
}
$tails = @(
    @( (Say 'And honestly the whole thing was a'), (Swear 'fucking'), (Say 'mess.') ),
    @( (Say 'I just want to go home, I am so'), (Swear 'fucking'), (Say 'tired.') )
)
foreach ($overlap in @(0.6, 1.2)) {
    for ($t = 0; $t -lt $tails.Count; $t++) {
        $manifest += New-Fixture ("ct_tail_{0}_{1}ms" -f $t, [int]($overlap * 1000)) @(
            (A 0.0 $tails[$t]),
            (Over 'B' $overlap @( (Say 'Well I did warn you about that last week.') ))
        )
    }
}

# --- manifest ----------------------------------------------------------------

$manifestPath = Join-Path $OutputDir 'manifest.json'
[pscustomobject]@{
    generatedBy = 'tests/fixtures/generate-crosstalk-fixtures.ps1'
    voice       = "$VoiceA / $VoiceB (pitched down)"
    note        = 'Two speakers mixed with overlap. Each profanity was synthesised as its own part, so start/end are exact by construction: the utterance start plus preceding part durations.'
    fixtures    = $manifest
} | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding utf8

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Write-Host "`nWrote $($manifest.Count) fixtures + manifest.json to $OutputDir"
