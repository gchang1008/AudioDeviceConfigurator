# AudioDeviceConfigurator Specification

## Purpose

Configure one active Windows playback endpoint using only speaker-channel and Default Format options exposed by the legacy Sound Control Panel. Windows `mmsys.cpl` is the sole catalog authority; EDID, WASAPI, drivers, hard-coded vendor data, and SVCL must not add selectable items.

## Supported platform

- Windows 10 and Windows 11 x64
- Interactive, unlocked desktop session
- .NET 10 self-contained single-file executable
- No third-party NuGet packages
- SVCL 1.28 or newer beside the application executable

## Workflow

1. Resolve the default active render endpoint, or the exact endpoint selected with `--device-id`.
2. Read the endpoint's speaker configurations and Default Format ComboBox items through the Control Panel provider.
3. Display the Control Panel options in their original text and order. Only parsed formats with effective bits and sample rate are selectable.
4. Present distinct 2/4/6/8 channel counts and selectable formats by numeric index. Free-form values are not accepted.
5. Present the selected endpoint, channels, and format. The confirmation prompt is `[Y/n]`; an empty input accepts the default `Y`, while EOF or any non-`Y` value cancels before modification.
6. Verify SVCL and run `/SaveDeviceFormat` with the exact endpoint ID. The saved original format must include a non-zero channel mask; otherwise stop before modification.
7. Apply the standard speaker mask through `/SetSpeakersConfig`, then apply effective bits, sample rate, and channels through `/SetDefaultFormat`.
8. Wait 500 ms and run `/SaveDeviceFormat`. Success requires exact channels, effective bits, sample rate, and channel mask.
9. A successful switch remains active.
10. On a partial failure, readback mismatch, or cancellation after setters start, restore the original channel mask and default format, then read back and verify all four values. Rollback failure is a system error and must not be reported as a clean restore.

Speaker masks:

| Channels | Mask |
|---:|---:|
| 2 | `0x3` |
| 4 | `0x33` (Quadraphonic) |
| 6 | `0x3f` |
| 8 | `0x63f` |

## CLI

```text
AudioDeviceConfigurator [options]

(no options)          Configure the default active render endpoint interactively.
--device-id <id>      Configure a specific active render endpoint.
--list                List active render endpoints and exit without changes.
--help, -h            Show help and exit.
```

Unknown options, including `--monitor-id`, return exit code 2.

## SVCL requirements

- A non-zero process exit code is failure.
- Output containing `No items found` is failure even when exit code is zero.
- `/SaveDeviceFormat` must create a complete WAVEFORMATEX or WAVEFORMATEXTENSIBLE file.
- WAVEFORMATEXTENSIBLE reports effective bits from `wValidBitsPerSample` and channel mask from `dwChannelMask`.
- The Core Audio endpoint ID is passed unchanged; token failure stops the operation and never falls back to another endpoint.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Selected settings applied and exact readback verified. |
| 1 | Apply/readback failed, but original settings were restored and verified. |
| 2 | Endpoint/Control Panel/SVCL/rollback system error. |
| 3 | User cancelled before settings changed. |
| 4 | No selectable Control Panel channel count or format. |

## Safety constraints

- Do not change the Windows default playback endpoint.
- Do not play audio.
- Do not accept values outside the Control Panel catalog.
- Do not close pre-existing or reused Control Panel windows.
- Close and verify disappearance of Sound, Properties, and Speaker Setup windows created by the current run.
- Require an interactive, unlocked Windows desktop.
