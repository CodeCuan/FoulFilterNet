# Catching the native crash

Watching a web video sometimes ends the Web process outright, with exit code
`3221226505` (`0xC0000409`, `STATUS_STACK_BUFFER_OVERRUN`). That is a native
*fast-fail* from inside whisper.cpp: it is not an exception, nothing managed
runs afterwards, and a buffered log loses exactly the lines that would say where
it happened.

`CrashTrace` exists for that. When it is on, every step around the native calls
is written straight through to a file — so the **last line in the file is the
last thing the process did**.

## Running with the trace on

In Visual Studio or Rider, pick the **`FoulFilterNet.Web (crash trace)`** launch
profile instead of the usual one. It sets everything below for you. From a
terminal, the same thing by hand:

```powershell
$env:FFN_CRASH_TRACE = "F:\SourceCode\FoulFilterNet\crash-trace.log"
$env:Watch__HeadSeconds = "120"
$env:DOTNET_DbgEnableMiniDump = "1"
$env:DOTNET_DbgMiniDumpType = "4"
$env:DOTNET_DbgMiniDumpName = "F:\SourceCode\FoulFilterNet\dumps\ffn.%d.dmp"
dotnet run --project src/FoulFilterNet.Web
```

Then watch a video until it dies, and send back `crash-trace.log` (and the
`.dmp` if one was written).

Three things that profile turns on:

- **`FFN_CRASH_TRACE`** — where the trace goes. Also settable as
  `Diagnostics:CrashTracePath` in configuration. Unset means no tracing and no
  cost; the startup log says `Crash trace is being written to ...` when it is on.
- **`Watch__HeadSeconds=120`** — puts the 120 s head back. The shipped default
  is now `0` as a workaround (`appsettings.json`), which skips the code the
  crash last logged; **to reproduce the crash, it has to be on**.
- **The `DOTNET_DbgMiniDump*` variables** — a full dump at the moment of death,
  which is the only way to see the native stack. Note a dump is ~1–3 GB, and
  `dumps/` and `crash-trace.log` are both gitignored.

Debugging from the IDE may catch the fast-fail before the dump is written; a
plain `dotnet run`, or Ctrl+F5 without the debugger, is the surest way to get one.

## Reading the trace

```powershell
Get-Content F:\SourceCode\FoulFilterNet\crash-trace.log -Tail 40 -Wait
```

`Get-Content` shares the file with the writer. Some editors will not open it
while the process is running.

Each line is `time +uptime thread step detail`:

```
04:12:07.881 +   41.102s t014 window.begin index=3/6 from=44.000 to=72.000 priority=High
04:12:07.884 +   41.105s t014 window.native.begin samples=448000 seconds=28.000 cancelled=False
04:12:08.598 +   41.819s t014 window.native.end segments=7 tokens=94
04:12:08.599 +   41.820s t014 window.end index=3 segments=7 words=61
```

The steps, and what a trace that **stops** at each one would mean:

| Last line | What died |
|---|---|
| `engine.model.load.begin` | Loading the weights onto the card |
| `engine.processor.build.begin` | Allocating whisper's per-window state — on CUDA, VRAM |
| `window.native.begin` | Inference itself, inside whisper.cpp |
| `window.processor.dispose.begin` | Tearing a processor down, which is where a cancelled window waits |
| `audio.dispose.begin` | Closing an analysis file |
| `session.head.open` | The W17 head handover: a second open while the whole file converts |
| `heartbeat` | Nothing was running — the process sat still and then died |

`heartbeat` lines every 3 seconds carry managed and working-set memory, thread
count, and the card's memory, utilisation and temperature from `nvidia-smi`
(left out if it is not on the `PATH`). A steadily climbing `vram=` before the
end would point at exhaustion rather than corruption.

`process.exit` is written on a clean shutdown. A file that simply stops with no
`process.exit` is a process that was killed.

## Turning it off again

Use the plain `FoulFilterNet.Web` profile, or clear `FFN_CRASH_TRACE`. Tracing
costs a synchronous disk write per step, so it is not meant to be left on.
