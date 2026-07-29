# Audio Device Capability Validator Specification

## Problem Statement

PC and monitor combinations vary across GPU vendors, audio drivers, docks, adapters, and connection types. A tester currently has to navigate Windows Control Panel manually to inspect or change an HDMI/DisplayPort audio endpoint's default format and channel count. SoundVolumeCommandLine (SVCL) can change and save the current default format, but it cannot enumerate every format supported by a monitor and it cannot independently prove that Windows recognized all LPCM capabilities declared by the monitor.

The tester needs a portable Windows command-line tool that automatically reads the active monitor's EDID, derives the relevant LPCM format combinations, checks whether the selected audio endpoint declares Exclusive-mode support for every combination, temporarily applies each supported combination through SVCL, reads it back, restores the original format, and produces durable diagnostic reports. The tool must work across non-fixed PCs and monitors without requiring the .NET runtime or administrator privileges.

## Solution

Build an English-language C# command-line application for Windows 10/11 x64 using .NET 10. Publish the application as a self-contained single-file executable and distribute it alongside the unmodified SVCL 1.28-or-newer package.

By default, the application selects the current default active Render endpoint. It automatically discovers active displays, reads and strictly validates their EDID blocks, and pairs the endpoint with its monitor. If pairing is ambiguous, it asks the tester to choose; automation can avoid interaction by supplying both endpoint and monitor IDs.

The application parses CTA-861 LPCM Short Audio Descriptors, derives unique combinations of EDID-declared effective bit depth, sample rate, and the selected channel counts 2, 6, and 8, then tests them in ascending order. A channel count is included only when it does not exceed the SAD's declared maximum. It first calls WASAPI Exclusive `IAudioClient::IsFormatSupported`. Unsupported combinations are recorded without changing the system. For combinations declared supported by WASAPI, it uses SVCL `/SetDefaultFormat`, polls the format saved by `/SaveDeviceFormat`, and confirms that effective bit depth, sample rate, and channel count match within three seconds.

A format passes only when it is declared by EDID, receives `S_OK` from WASAPI Exclusive format support querying, and is applied and read back exactly. All EDID-derived combinations must pass for the overall test to pass. The original default format is saved before modifications and restored at completion, on handled errors, and on Ctrl+C. A failed format is followed by an immediate restore before testing continues; successful formats may proceed directly to the next combination. A restore failure stops testing and becomes a system error.

Every run writes timestamped JSON and CSV reports under a `Reports` directory next to the application. Reports include complete system, driver, endpoint, monitor, raw EDID, parsed capability, test, SVCL, restore, and error details. The console presents a concise supported-format table, an unsupported/failed summary, report paths, the overall status, and the process exit code semantics.

## User Stories

1. As a hardware validation tester, I want the tool to work with changing PCs and monitors, so that test targets never need to be hard-coded.
2. As a hardware validation tester, I want the default active Render endpoint selected automatically, so that the common test path requires no device lookup.
3. As a hardware validation tester, I want to select a non-default active Render endpoint by ID, so that I can test a connected display without changing the system default device.
4. As an automation engineer, I want endpoint and monitor ID arguments, so that unattended runs do not depend on interactive selection.
5. As a tester, I want a list command for active displays and Render endpoints, so that I can obtain stable IDs for scripted runs.
6. As a tester, I want ambiguous monitor-to-endpoint pairing presented interactively, so that the tool does not silently test the wrong monitor.
7. As a tester, I want to cancel an ambiguous selection, so that I can stop safely without choosing an incorrect target.
8. As an automation engineer, I want cancellation represented by a distinct exit code, so that it is distinguishable from a test failure.
9. As a tester, I want only currently active display paths considered, so that stale EDID registry records cannot be selected accidentally.
10. As a tester, I want EDID acquired from the current Windows environment, so that the report reflects what the tested PC actually recognized.
11. As a tester, I want every declared EDID block validated for complete length and checksum, so that corrupt capability data cannot produce misleading tests.
12. As a tester, I want an EDID acquisition or validation failure to stop the test, so that the tool never silently falls back to assumed capabilities.
13. As a tester, I want CTA-861 Audio Data Blocks parsed, so that the candidate matrix comes from the monitor's declared audio capabilities.
14. As a tester, I want LPCM Short Audio Descriptors parsed, so that PCM bit depths, sample rates, and maximum channel counts are known.
15. As a tester, I want non-LPCM formats recorded but not tested in the first version, so that compressed formats do not contaminate PCM results.
16. As a tester, I want a valid EDID with no LPCM capability classified as N/A, so that a non-audio display is not treated as a software failure.
17. As an automation engineer, I want N/A represented by exit code 4, so that it is distinct from pass, fail, error, and cancellation.
18. As a tester, I want duplicate LPCM declarations merged into unique candidate formats, so that the same format is not tested repeatedly.
19. As a diagnostic engineer, I want source SAD references preserved in JSON, so that a generated candidate can be traced back to EDID.
20. As a tester, I want only EDID-declared formats tested, so that results answer whether the PC recognized the monitor's advertised capabilities.
21. As a tester, I want only 2-, 6-, and 8-channel candidates, so that the result scope matches the required validation targets.
22. As a tester, I want 2, 6, and 8 channels included according to each SAD's maximum, so that a maximum of 8 produces 2-, 6-, and 8-channel tests while a maximum of 6 produces 2- and 6-channel tests.
23. As a tester, I want 16-bit candidates represented with a 16-bit container, so that their WASAPI format matches the expected Windows representation.
24. As a tester, I want 20-bit candidates represented with a 24-bit container and 20 valid bits, so that valid and container bit depths are not conflated.
25. As a tester, I want 20-bit candidates generated only when EDID explicitly advertises 20-bit LPCM, so that undeclared capabilities are not inferred.
26. As a tester, I want 24-bit candidates represented with a 32-bit container and 24 valid bits, so that they match the selected common Windows representation.
27. As a tester, I want 32, 44.1, 48, 88.2, 96, 176.4, and 192 kHz considered only when the EDID advertises them, so that the matrix is complete but constrained.
28. As a tester, I want candidates sorted from lower to higher channel count, sample rate, and bit depth, so that console and report results are predictable.
29. As a tester, I want every EDID candidate queried through WASAPI Exclusive mode using WAVEFORMATEXTENSIBLE, and plain WAVEFORMATEX as a second diagnostic query when the lossless stereo PCM format is representable, so both Windows structures are observable.
30. As a tester, I want the exact HRESULT and result recorded separately for plain and extensible WASAPI queries, so format-structure differences are diagnosable.
31. As a tester, I want multi-channel and packed-depth formats (20-in-24 and 24-in-32) queried only as WAVEFORMATEXTENSIBLE, because plain WAVEFORMATEX cannot represent their valid-bit layout.
32. As a tester, I want EDID declaration and SVCL apply/readback to determine the final candidate result, so WASAPI false negatives remain diagnostic evidence and do not block a valid applied format.
33. As a tester, I want every EDID candidate applied through SVCL, so declared support is also checked against Windows default-format configurability.
34. As a tester, I want the applied format read from SVCL's saved raw format structure, so validation does not depend on localized Control Panel text.
35. As a tester, I want effective bit depth read separately from container bit depth for WAVEFORMATEXTENSIBLE, so that 24 valid bits in a 32-bit container are reported as 24-bit.
35. As a tester, I want ordinary WAVEFORMATEX formats to use their container bit depth as effective bit depth, so that formats without a separate valid-bit field are interpreted correctly.
36. As a tester, I want the tool to poll every 200 milliseconds for up to three seconds after setting a format, so that slower drivers are not falsely failed.
37. As a tester, I want effective bit depth, sample rate, and channel count to match exactly, so that partial or substituted changes do not pass.
38. As a tester, I want SVCL stdout, stderr, and process exit code retained as diagnostics, so that command behavior can be inspected even though readback determines application success.
39. As a tester, I want `No items found` treated as a command failure even when SVCL exits with code 0, so that a missing endpoint cannot pass.
40. As a tester, I want the original default format saved before the first modification, so that testing is reversible.
41. As a tester, I want a failed candidate followed by restoration and verification before testing continues, so that failures do not contaminate later results.
42. As a tester, I want successful candidates allowed to transition directly to the next candidate, so that the test avoids unnecessary restores.
43. As a tester, I want the original format restored and verified at normal completion, so that testing does not leave the PC modified.
44. As a tester, I want restoration attempted after handled exceptions, so that errors have minimal system impact.
45. As a tester, I want Ctrl+C intercepted and restoration attempted, so that manual cancellation does not normally leave a test format configured.
46. As a tester, I want a restore failure to stop all further testing, so that the tool does not continue changing a system it can no longer restore safely.
47. As an automation engineer, I want a restore failure reported as exit code 2, so that it is treated as an infrastructure/system error rather than a capability mismatch.
48. As a tester, I want the tool not to change the default playback endpoint, so that selecting a non-default endpoint does not disrupt routing.
49. As a tester, I want the tool not to call `/SetSpeakersConfig`, so that optional-speaker and full-range-speaker settings remain untouched.
50. As a tester, I want only the default format's channel count modified, so that the scope matches the required channel-count validation.
51. As a tester, I want other applications left running, so that the tool does not close or interfere with user processes.
52. As a tester, I want any resulting contention or modification failure reported honestly, so that environmental interference is visible.
53. As a tester, I want no mandatory administrator elevation, so that the tool can run in standard test accounts.
54. As a deployment engineer, I want the C# application published as a .NET 10 self-contained single-file win-x64 executable, so that no .NET runtime or SDK installation is required.
55. As a deployment engineer, I want SVCL 1.28 or newer checked at startup, so that required save, set, and channel behavior is available.
56. As a deployment engineer, I want missing or old SVCL classified as a system error, so that deployment defects are immediately clear.
57. As a compliance-conscious distributor, I want the unmodified SVCL executable, readme, and CHM retained together, so that the NirSoft package is not partially redistributed.
58. As a maintainer, I want no third-party NuGet dependencies, so that the application dependency surface remains limited to .NET and Windows APIs.
59. As a tester, I want all console text and report field names in English, so that reports are stable across localized Windows installations.
60. As a tester, I want channel values displayed numerically, so that results are not confused with untested speaker configuration settings.
61. As a tester, I want a concise console table of passing formats, so that available combinations are easy to inspect during a run.
62. As a tester, I want the console to summarize unsupported and failed format counts, so that problems are visible without flooding the screen.
63. As a diagnostic engineer, I want every candidate, including unsupported and failed candidates, retained in JSON and CSV, so that the full matrix is auditable.
64. As a diagnostic engineer, I want each CSV format represented by one row, so that results can be filtered and aggregated in Excel.
65. As a diagnostic engineer, I want system, monitor, endpoint, and driver metadata repeated in each CSV row, so that rows remain useful when files are combined.
66. As a diagnostic engineer, I want raw EDID bytes included in JSON, so that parsing and source capabilities can be independently reviewed.
67. As a diagnostic engineer, I want the PC name, Windows version, GPU and audio driver details, endpoint IDs, monitor IDs, and parsed EDID included, so that differences across PCs can be investigated.
68. As a tester, I want reports created for passes, failures, errors, N/A, and cancellations, so that every invocation leaves an audit record.
69. As a tester, I want an early failure or N/A CSV to contain a summary row, so that the CSV is informative even without format rows.
70. As a tester, I want reports stored under a `Reports` directory beside the executable, so that deployment and evidence remain together.
71. As a tester, I want report filenames to contain a sanitized PC name, monitor name, and local timestamp, so that files are recognizable and sortable.
72. As a tester, I want a sequence suffix when names collide within the same second, so that reports are never overwritten.
73. As a tester, I want report timestamps expressed in local PC time, so that they align with the test site's clock.
74. As an automation engineer, I want exit code 0 for a complete pass, so that success is machine-readable.
75. As an automation engineer, I want exit code 1 for one or more format mismatches, so that capability failures are distinguishable.
76. As an automation engineer, I want exit code 2 for EDID, pairing, SVCL, restore, report, or other system errors, so that infrastructure failures are distinguishable.
77. As an automation engineer, I want exit code 3 for user cancellation, so that aborted tests are distinguishable.
78. As an automation engineer, I want exit code 4 for a valid monitor with no LPCM capability, so that N/A is distinguishable.
79. As a tester, I want failure to create the Reports directory or write either required report classified as a system error, so that missing evidence cannot be mistaken for a valid completed run.
80. As a user, I want `--help` to document commands, arguments, report behavior, side effects, and exit codes, so that operation is self-explanatory.

## Implementation Decisions

- The application will be a .NET 10 C# command-line application targeting Windows 10/11 x64.
- Publication will use self-contained, single-file, win-x64 output. The C# application is one executable, while the deployment directory also contains the original SVCL package files.
- No third-party NuGet packages will be used. Required Windows display, SetupAPI/Configuration Manager, registry, COM, Core Audio, and WASAPI interfaces will be defined and called directly.
- The CLI will support default execution, `--list`, `--device-id`, `--monitor-id`, and `--help`. Candidate bit depths, rates, and channel counts cannot be overridden because the matrix must derive from EDID.
- Without explicit IDs, the current default active Render endpoint is preferred.
- Only currently active display paths and active Render endpoints are eligible.
- Endpoint-to-monitor pairing will be automatic when unique. Ambiguity invokes an English interactive selection. Supplying both IDs avoids interaction. Pairing choices are not persisted.
- Interactive cancellation is supported and produces reports.
- EDID retrieval must reflect the currently active monitor recognized by Windows. Failure to acquire or associate EDID is a system error; historical EDID is not a fallback.
- The parser will validate the EDID header, complete block lengths, declared extension count, and checksum of every block. Any validation error stops capability testing.
- The first version parses CTA-861 Audio Data Blocks and LPCM Short Audio Descriptors. Non-LPCM descriptors are retained as diagnostics but are not queried or applied.
- A valid EDID with no LPCM SAD produces N/A rather than pass or fail.
- Candidate formats are generated per LPCM SAD, then deduplicated while preserving source SAD references.
- Supported sample-rate bits map to 32, 44.1, 48, 88.2, 96, 176.4, and 192 kHz.
- Supported effective bit depths map to 16, 20, and 24 bits. Only advertised depths are generated.
- Candidate channel counts are restricted to 2, 6, and 8, included when less than or equal to the SAD's maximum channel count.
- Format construction uses PCM WAVEFORMATEXTENSIBLE where required. The selected representations are 16 container/16 valid, 24 container/20 valid, and 32 container/24 valid bits.
- Standard channel masks will be used for 2-, 6-, and 8-channel WASAPI queries. The application will not modify Windows Speakers Config.
- Candidates are ordered by channel count, sample rate, and effective bit depth from low to high.
- The WASAPI query mode is Exclusive only. Every EDID candidate receives an extensible query; lossless stereo formats whose container and valid bits match receive an additional plain WAVEFORMATEX query.
- Plain and extensible WASAPI HRESULTs/results are retained separately. Multi-channel, 20-in-24, and 24-in-32 candidates are extensible-only. WASAPI results are diagnostic and do not gate SVCL apply/readback.
- The tool requires `svcl.exe` version 1.28 or newer in the application directory.
- Before modification, `/SaveDeviceFormat` captures the original raw default format. The tool parses WAVEFORMATEX and WAVEFORMATEXTENSIBLE, retaining format tag, container bits, valid bits, rate, channels, channel mask, and raw bytes.
- `/SetDefaultFormat` receives effective bit depth, sample rate, and channel count. `/SetSpeakersConfig` is never called.
- After setting, the application polls `/SaveDeviceFormat` every 200 ms for up to three seconds. Readback effective bit depth, sample rate, and channel count must exactly equal the candidate.
- SVCL process exit code, stdout, and stderr are diagnostic inputs. A zero process exit code is not proof of successful application. `No items found`, absence of an expected saved file, malformed data, timeout, or mismatched readback causes the apply stage to fail.
- A candidate passes only if EDID declared it and SVCL readback matched all three requested values; WASAPI results remain diagnostic evidence.
- Every EDID-derived candidate must pass for overall PASS. Any candidate-level unsupported or apply mismatch produces overall FAIL unless a higher-priority system error occurs.
- The original format is restored after the full run. After a failed apply/readback, it is restored and verified before testing continues. Successful candidates can proceed directly to the next candidate.
- Restoration is attempted on normal completion, handled exceptions, and Ctrl+C. If restoration or restore verification fails, testing stops and the overall result is a system error. Forced process termination, OS crash, or power loss cannot be guaranteed recoverable.
- The application never changes the system default endpoint, closes audio applications, plays audio, initializes a WASAPI stream, or modifies speaker optional/full-range settings.
- Console and report text use English. Channels are shown only as numeric counts.
- Console output contains target identity, a concise table of passing formats, counts of unsupported/apply-failed combinations, overall status, report paths, and exit-code meaning.
- JSON contains complete hierarchical diagnostics. CSV contains one row per candidate with repeated run/system/device/monitor fields; runs without candidates contain one summary row.
- Reports are always attempted, including errors, cancellation, and N/A. Failure to create both required reports is a system error.
- Reports are written beneath `Reports` beside the executable. Filenames use sanitized PC name, monitor name, and local `yyyyMMdd_HHmmss`, with a sequence suffix on collision.
- Exit codes are: 0 PASS, 1 format mismatch/unsupported, 2 system or reporting error, 3 cancelled, and 4 N/A.

## Testing Decisions

- The primary automated test seam is the complete CLI workflow. Tests invoke the same application orchestration used by production while replacing the Windows display/EDID, endpoint/WASAPI, process/SVCL, clock, console interaction, and filesystem boundaries with deterministic implementations.
- Tests assert externally visible behavior: console output, interaction prompts, SVCL commands, side-effect ordering, generated JSON/CSV, restoration attempts, and process exit code. Tests must not depend on internal class structure.
- EDID parser behavior will be exercised through the CLI seam using complete binary fixtures. Fixtures cover valid base-plus-CTA EDID, multiple CTA blocks, duplicate SADs, LPCM and non-LPCM descriptors, every supported rate/depth bit, maximum channel counts, invalid header, truncated blocks, extension-count mismatch, and checksum failures in every block position.
- Candidate generation tests will verify SAD-scoped combinations, 2/6/8 channel filtering, deduplication, source traceability, selected valid/container representations, standard channel masks, and ascending deterministic ordering.
- Pairing tests will cover unique automatic mapping, ambiguous interactive selection, cancellation, explicit IDs, unknown IDs, inactive displays/endpoints, same-model monitors, and default endpoint selection.
- WASAPI tests will cover both plain and extensible queries for lossless stereo PCM, extensible-only queries for multi-channel and packed-depth formats, exact HRESULT capture, and mixed matrices. They will verify SVCL apply/readback remains authoritative even for non-`S_OK` diagnostics.
- SVCL workflow tests will cover minimum-version enforcement, missing package files, `No items found` with exit code 0, nonzero exit, missing save output, malformed WAVEFORMATEX, malformed WAVEFORMATEXTENSIBLE, delayed readback, exact match, substituted bit depth/rate/channels, and timeout.
- Format parsing tests through the CLI will distinguish a 32-bit container with 24 valid bits from a true 32-bit effective format and a 24-bit container with 20 valid bits.
- State restoration tests will verify original capture before modification; final restore after pass and fail; immediate restore after an apply failure; continued testing only after verified restoration; restore on handled exception and Ctrl+C; and immediate abort with exit code 2 when restoration fails.
- Reporting tests will verify JSON completeness, one-row-per-candidate CSV, summary rows for early error/cancel/N/A, English stable field names, local timestamps, filename sanitization, collision suffixes, raw EDID preservation, HRESULT formatting, SVCL diagnostics, and report-write failure escalation.
- Exit-code tests will cover all five defined values and precedence rules, especially system/restore/report errors overriding capability failure.
- No prior application test patterns exist in the current directory; the existing PowerShell script serves only as behavioral prior art for SVCL set/save/readback validation and valid-bit parsing.
- Real-hardware integration acceptance will run the published deployment on representative Windows 10/11 x64 PCs, GPU vendors, HDMI/DisplayPort paths, monitors with 2/6/8-channel LPCM declarations where available, and at least one no-LPCM display. It will verify Control Panel readback, reports, and final restoration.
- A good test validates observable contract behavior and safety properties rather than private methods or exact implementation decomposition.

## Out of Scope

- Shared-mode WASAPI capability testing or closest-match reporting.
- Calling `IAudioClient::Initialize`, starting a stream, transmitting silence, playing tones, playing audio files, or proving audible speaker output.
- HDMI/DisplayPort protocol analysis or proving the real on-wire audio format with measurement hardware.
- Testing compressed CTA audio formats such as AC-3, DTS, Dolby Digital Plus, or IEC 61937 payloads.
- Parsing audio capabilities outside CTA-861 LPCM SADs, including DisplayID audio extensions in the first version.
- Testing arbitrary candidate formats not declared by EDID.
- Testing channel counts other than 2, 6, and 8.
- Changing or validating optional speakers, full-range speakers, or any `/SetSpeakersConfig` values.
- Changing the system default playback endpoint.
- Closing, pausing, or otherwise controlling applications currently using audio.
- Persisting endpoint-to-monitor pairing decisions.
- Using historical/offline monitor registry records as EDID fallback.
- Supporting Windows versions older than Windows 10, x86, or ARM64 in the first release.
- A graphical user interface.
- Administrator-only operation.
- Guaranteeing restoration after forced process termination, kernel failure, OS crash, or power loss.
- Eliminating SVCL as an external deployment dependency.

## Further Notes

- "Supported" is intentionally a compound validation result in this product: EDID declaration, WASAPI Exclusive `S_OK`, and successful SVCL application/readback. Reports must preserve each stage independently so users can distinguish monitor declaration, driver recognition, and Windows setting behavior.
- EDID advertises sink capability, while WASAPI reflects the current PC, GPU driver, adapter/dock/KVM path, and Windows endpoint. A mismatch is a useful test result rather than proof that the monitor EDID itself is wrong.
- The Windows Control Panel display of 24-bit can correspond to 24 valid bits in a 32-bit container. User-facing bit depth is therefore effective/valid bit depth; container depth remains diagnostic metadata.
- SVCL returning process exit code 0 does not imply an item was found or a setting was applied. The existing SVCL 1.28 package has been observed returning `No items found` with exit code 0, making readback verification mandatory.
- The deployment remains runtime-independent for .NET, but it is not physically a one-file product because the chosen design retains SVCL and its original documentation package.
