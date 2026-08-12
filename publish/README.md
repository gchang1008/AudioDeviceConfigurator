# AudioDeviceConfigurator

Reads the selected Windows playback endpoint's Sound Control Panel speaker-channel and **Default Format** options, lets the user choose from those options, then applies and verifies the selection through `svcl.exe`.

Windows 10/11 x64. The application is a .NET 10 self-contained single-file executable with no third-party NuGet packages.

## Requirements

- **OS:** Windows 10 1809 (build 17763) or later, 64-bit. Windows 11 is supported. Windows 7 / 8 / 8.1 are not supported.
- **CPU:** 64-bit processor with `CMPXCHG16B`, `LAHF/SAHF`, and `AVX2` instructions (any CPU released after ~2015 qualifies).
- **.NET:** none required at runtime — the executable bundles the .NET 10 runtime. No Visual C++ Redistributable installation is required.
- **Audio:** A playback endpoint reachable through Windows Core Audio.
- **External files in the same folder:** `svcl.exe` (SVCL 1.28 or newer) and `test_audio.wav`.

If the executable fails to start with `0x80131506` (CoreCLR ExecutionEngine), confirm the OS build with `winver` and that the host machine satisfies the CPU and OS requirements above.

## Quick start

Keep these files together in a writable folder:

- `AudioDeviceConfigurator.exe`
- `svcl.exe` (SVCL 1.28 or newer)
- `test_audio.wav` (used by the GUI for loop-back verification)

```cmd
AudioDeviceConfigurator.exe
AudioDeviceConfigurator.exe --list
AudioDeviceConfigurator.exe --device-id "{0.0.0.00000000}.{...}"
AudioDeviceConfigurator.exe --cli     # force the interactive CLI
```

Double-clicking `AudioDeviceConfigurator.exe` (or running it with no arguments) launches the WPF main window. The CLI is the default whenever any of `--help`, `--list`, `--device-id`, or `--cli` is supplied, and when stdout is redirected.

## GUI workflow

The window opens with the current Windows default playback endpoint already selected. Speaker Channels, Sample Rate, and Bit Depth are visible from the start, before the endpoint's Control Panel data has loaded.

- **Three columns** — Speaker Channels, Sample Rate, and Bit Depth — sit side by side as switch groups. Common values stay visible, unsupported values are disabled.
- **Sample Rate** lists only the standard values between 32 kHz and 192 kHz (32, 44.1, 48, 88.2, 96, 176.4, 192 kHz).
- **Channel**, **Sample Rate**, and **Bit Depth** constrain each other using the exact pairs exposed by the Control Panel, so picking a sample rate clears the bit depth if that pair does not exist (and vice versa).
- The **Active** panel in the lower-right shows the device's current channel / sample rate / bit depth. It is populated as soon as an endpoint is selected, and refreshed after every successful **Apply** (or re-**Apply**). When the Active channel and exact sample-rate / bit-depth pair exist in the Control Panel options, the three switch groups are preselected automatically. Unsupported values stay unselected, and preselection never runs **Apply**.
- The endpoint list follows Core Audio hot-plug notifications while the GUI is open. Changes to other endpoints do not disturb the current selection or playback. If the selected endpoint is disconnected during playback, the loop enters default-follow mode and restarts on the current Windows default endpoint; later default-device changes move playback again without selecting an endpoint. The endpoint selection and stale switch options remain cleared without scanning default-endpoint capabilities, while **Active** follows its current SVCL format. With no endpoint selected, **Play** starts on the current default endpoint and **Stop** remains available during playback. If no default endpoint is available, playback stops and **Active** is cleared.
- **Apply** is locked while a switch is in progress, but stays enabled while audio is playing. Pressing **Apply** while playback is active first stops the WASAPI stream, then applies the new format. The new format starts playing automatically on success.
- **Play** does not require **Apply** first. It is enabled whenever a selected endpoint is available (or, with no selection, a Windows default endpoint exists) and playback is stopped. During playback, **Play** is disabled and **Stop** is enabled. Changing a radio-button selection while playing keeps **Apply** enabled, so the new selection can be re-applied without manually pressing **Stop** first.
- **Stop** releases the WASAPI stream.
- After **Apply** verifies a successful switch through `svcl.exe`, the GUI immediately loops `test_audio.wav` through the selected endpoint in WASAPI Shared Mode so you can hear whether the new format is in effect.
- Closing the window always stops playback and releases the WASAPI resources.
- Playback errors are surfaced in the status line and **never** roll back a verified audio configuration.
- The legacy Sound, Properties, and Speaker Setup windows opened during enumeration are closed automatically.

## CLI workflow

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

(no options)          Launch the WPF GUI.
--cli                 Force the interactive CLI flow (default when --list / --device-id / --help is present).
--device-id <id>      Configure a specific active render endpoint by endpoint ID.
--list                List active render endpoints, then exit without changes.
--help, -h            Show full help and exit.
```

## Side effects and safety

- Briefly opens the legacy Sound Control Panel, speaker setup page, and endpoint Properties dialog.
- A confirmed operation permanently changes the selected endpoint's speaker configuration and Default Format.
- Does not change the default playback endpoint.
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
- GUI playback converts `test_audio.wav` to the selected endpoint's WASAPI mix format before playback.