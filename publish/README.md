# AudioDeviceConfigurator

Reads the selected Windows playback endpoint's Sound Control Panel speaker-channel and **Default Format** options, lets the user choose from those options, then applies and verifies the selection through `svcl.exe`.

Windows 10/11 x64. The application is a .NET 10 self-contained single-file executable with no third-party NuGet packages.

## Quick start

Keep these files together in a writable folder:

- `AudioDeviceConfigurator.exe`
- `svcl.exe` (SVCL 1.28 or newer)

```cmd
AudioDeviceConfigurator.exe
AudioDeviceConfigurator.exe --list
AudioDeviceConfigurator.exe --device-id "{0.0.0.00000000}.{...}"
```

With no options, the application configures the current default active playback endpoint. Use `--list` to obtain endpoint IDs and `--device-id` to configure another active endpoint.

## Workflow

1. Enumerates active playback endpoints through Windows Core Audio.
2. Opens the legacy Sound Control Panel and reads available speaker configurations and Default Format items without changing them.
3. Displays distinct selectable channel counts and parsed format items. Windows Control Panel is the only option source.
4. Prompts for a channel count and audio format, then displays a final summary.
5. Shows a `[Y/n]` confirmation; pressing Enter accepts the default `Y`, while another value cancels before modification.
6. Uses `/SaveDeviceFormat` to capture the original format and channel mask. If the mask is unavailable, the operation stops before any change.
7. Uses `/SetSpeakersConfig` and `/SetDefaultFormat`, waits 500 ms, then reads the result back.
8. Success requires exact channels, effective bits, sample rate, and channel-mask values.
9. On any partial failure or readback mismatch, restores and verifies the original speaker configuration and format.

For 4 channels, the application uses the standard Quadraphonic mask (`0x33`). The supported mappings are 2=`0x3`, 4=`0x33`, 6=`0x3f`, and 8=`0x63f`.

## Commands

```text
AudioDeviceConfigurator [options]

(no options)          Configure the current default active render endpoint interactively.
--device-id <id>      Configure a specific active render endpoint by endpoint ID.
--list                List active render endpoints, then exit without changes.
--help, -h            Show full help and exit.
```

## Side effects and safety

- Briefly opens the legacy Sound Control Panel, speaker setup page, and endpoint Properties dialog.
- A confirmed operation permanently changes the selected endpoint's speaker configuration and Default Format.
- Does not change the default playback endpoint and does not play audio.
- Only closes Control Panel windows created by the current run.
- Requires an interactive, unlocked Windows desktop session.
- Do not interact with the Sound Control Panel while enumeration runs.
- If rollback cannot be verified, exit code 2 is returned with a warning that settings may not be completely restored.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | **PASS** — selected settings were applied and read back successfully. |
| 1 | **FAIL** — apply/readback failed, but the original settings were restored and verified. |
| 2 | **ERROR** — discovery, SVCL, or rollback failed. |
| 3 | **CANCELLED** — cancelled before settings were changed. |
| 4 | **N/A** — no selectable Control Panel speaker channels or formats were found. |

## Limitations

- Works only in an interactive, unlocked Windows desktop session.
- `--device-id` is passed unchanged to SVCL and probed with `/SaveDeviceFormat`; no fallback to another endpoint occurs.
- Unparsed Control Panel text is displayed but cannot be selected for SVCL.
- Reads and configures one endpoint per run.
