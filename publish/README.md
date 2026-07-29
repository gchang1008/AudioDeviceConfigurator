# AudioDeviceConfigurator

EDID-driven audio capability validator. Reads a monitor's EDID, derives
the LPCM formats the monitor's HDMI/DP audio path is *supposed* to
support, asks WASAPI whether the chosen render endpoint actually accepts
each one, and writes a JSON + CSV report of the result. The original
default format is restored before the program exits, even on error or
Ctrl+C.

Windows 10/11 x64. Single self-contained .exe (no .NET runtime install
required), bundled with NirSoft SoundVolumeCommandLine 1.28.

## Quick start

1. Copy the entire `publish/` folder to a writable location. The
   application writes reports next to itself, so it must live somewhere
   the current user can create files.
2. Open a terminal in that folder.
3. Run one of:

   ```cmd
   AudioDeviceConfigurator.exe --list
   AudioDeviceConfigurator.exe
   AudioDeviceConfigurator.exe --device-id "{0.0.0.00000000}.{...}" --monitor-id "\\?\DISPLAY#..."
   ```

4. Read the report. JSON has everything; CSV has one row per format
   candidate, with the system/endpoint/monitor metadata repeated on
   every row.

## What you get

- `Reports/<PC>_<Monitor>_<yyyyMMdd_HHmmss>.json` and `.csv`. One pair
  per run. The pair is regenerated on every run; nothing is appended.
- A summary on stdout: how many candidates were supported, how many
  were rejected by WASAPI, how many failed to apply or read back.
- An exit code. See `EXIT CODES` in `--help` (or the section below).

## Files in this folder

| File | Purpose |
|---|---|
| `AudioDeviceConfigurator.exe` | The application (73 MB, .NET 10 self-contained single file). |
| `svcl.exe` | NirSoft SoundVolumeCommandLine 1.28. **Not modified.** Required to be next to the .exe. |
| `svcl.chm` | NirSoft help file for `svcl.exe`. |
| `readme.txt` | NirSoft's own readme for `svcl.exe`. |
| `Reports/` | Created on first run; reports are written here. |

Do not delete `svcl.exe` or move it elsewhere — the application calls it
to read and write the default format and will fail if it is missing or
older than 1.28.

## Commands

```text
AudioDeviceConfigurator [options]

(no options)          Test the current default active render endpoint against
                      the EDID of its paired active monitor.
--device-id <id>      Test a specific active render endpoint by its endpoint ID.
--monitor-id <id>     Use a specific active monitor by its device ID. The leading
                      \\?\ prefix shown by --list may be omitted.
--list                List active displays and active render endpoints, then exit.
--help, -h            Show full help and exit.
```

Supplying both `--device-id` and `--monitor-id` makes the run fully
unattended. When pairing is ambiguous and IDs are not supplied, the
application lists the candidates and prompts you to pick.

### Scripted use

`--list` prints one section per active render endpoint and one per
active monitor, with stable IDs that can be fed back into
`--device-id` / `--monitor-id`:

```text
Active render endpoints:
  VX229 (NVIDIA High Definition Audio)
    Endpoint ID : {0.0.0.00000000}.{440ee392-a6b3-445d-b13a-ca7ed07d642f}
    Description : VX229
    Container   : {3EEC0E47-18D4-5403-803C-2D7DBFC7C5CD}
```

Round-trip these values back into a second run. Windows shells mangle
the `\\?\` prefix on monitor IDs differently, so the application
accepts both forms.

## Side effects

- Temporarily changes the **default format** of the selected render
  endpoint. The original format is restored on completion, on handled
  errors, and on Ctrl+C.
- Does **not** change the default playback device.
- Does **not** call `/SetSpeakersConfig` or change speaker
  configuration.
- Does **not** close other applications. The only process the
  application may terminate is `svcl.exe` itself if it hangs.
- Does **not** play audio. WASAPI is queried in Exclusive mode via
  `IsFormatSupported`; no stream is ever initialized.
- Does **not** require administrator privileges.

## Reports

Both reports are written for every outcome, including failures and the
"Not Applicable" exit.

JSON contains the full run record: timestamps, the raw EDID bytes
(hex), every parsed CTA-861 Audio Data Block entry, every candidate
format with its WASAPI HRESULT, every `svcl.exe` call with its exit
code, stdout, and stderr, and the restore attempt.

CSV has a header row plus one row per candidate, with the
system/endpoint/monitor/driver metadata repeated on every row so a
spreadsheet can be sorted without losing context.

Driver metadata (GPU name + version, HDMI audio driver name + version)
is fetched by spawning `powershell.exe` with an embedded script; the
script uses `Get-CimInstance Win32_PnPSignedDriver`. PowerShell is
present on Windows 10/11 by default. If PowerShell is unavailable or
returns malformed output, those fields are recorded as `null` and the
rest of the run still completes.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | **PASS** — every EDID-declared LPCM format was supported, applied, and read back. |
| 1 | **FAIL** — one or more formats were unsupported or could not be applied. |
| 2 | **ERROR** — EDID, pairing, SVCL, restore, or reporting problem. |
| 3 | **CANCELLED** — you declined an interactive selection. |
| 4 | **N/A** — the monitor's EDID is valid but declares no LPCM audio capability. |

Exit codes take precedence: a run that failed AND was cancelled AND
hit a system error reports the highest-severity one (2 > 3 > 1 > 4 > 0).

## Limitations

- HDMI/DP audio only. USB audio, Bluetooth, and motherboard analog
  outputs typically do not have an EDID-driven path; the program
  refuses to pair them with a monitor.
- Multi-streaming and 3D audio formats are out of scope.
- One endpoint and one monitor per run. Run the program multiple times
  to validate multiple outputs.
- No write to a network share. Reports are written to the local
  `Reports/` directory beside the executable.
