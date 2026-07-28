[CmdletBinding()]
param(
    [ValidateRange(1, 64)]
    [int]$BitsPerSample = 16,

    [ValidateRange(1, 768000)]
    [int]$SampleRate = 48000,

    [ValidateRange(1, 32)]
    [int]$Channels = 2,

    [string]$Device = 'DefaultRenderDevice'
)

$svclPath = Join-Path $PSScriptRoot 'svcl.exe'
$tempFormatPath = Join-Path ([System.IO.Path]::GetTempPath()) ("svcl-format-{0}.dat" -f [guid]::NewGuid())

if (-not (Test-Path -LiteralPath $svclPath -PathType Leaf)) {
    Write-Error "ERROR: svcl.exe was not found at $svclPath"
    exit 2
}

function Get-DefaultFormat {
    Remove-Item -LiteralPath $tempFormatPath -Force -ErrorAction SilentlyContinue
    & $svclPath /SaveDeviceFormat $Device $tempFormatPath
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        throw "Failed to save Default Format. SVCL exit code: $exitCode"
    }

    if (-not (Test-Path -LiteralPath $tempFormatPath -PathType Leaf)) {
        throw "SVCL did not find the device or save its format: $Device"
    }

    $data = [System.IO.File]::ReadAllBytes($tempFormatPath)
    if ($data.Length -lt 16) {
        throw "The saved device format is invalid or incomplete."
    }

    $formatTag = [BitConverter]::ToUInt16($data, 0)
    $containerBits = [BitConverter]::ToUInt16($data, 14)
    $validBits = $containerBits

    if ($formatTag -eq 0xFFFE) {
        if ($data.Length -lt 20) {
            throw "The saved WAVEFORMATEXTENSIBLE data is invalid or incomplete."
        }

        $validBits = [BitConverter]::ToUInt16($data, 18)
    }

    [pscustomobject]@{
        Channels      = [BitConverter]::ToUInt16($data, 2)
        SampleRate    = [BitConverter]::ToUInt32($data, 4)
        BitsPerSample = $validBits
    }
}

function Format-AudioFormat([object]$Format) {
    return "{0} channels, {1} bit, {2} Hz" -f $Format.Channels, $Format.BitsPerSample, $Format.SampleRate
}

try {
    $before = Get-DefaultFormat

    & $svclPath /SetDefaultFormat $Device $BitsPerSample $SampleRate $Channels
    $setExitCode = $LASTEXITCODE

    if ($setExitCode -ne 0) {
        throw "Failed to set Default Format. SVCL exit code: $setExitCode"
    }

    Start-Sleep -Milliseconds 500
    $after = Get-DefaultFormat

    $matched =
        $after.Channels -eq $Channels -and
        $after.BitsPerSample -eq $BitsPerSample -and
        $after.SampleRate -eq $SampleRate

    "Device: $Device"
    "Before: $(Format-AudioFormat $before)"
    "Requested: $Channels channels, $BitsPerSample bit, $SampleRate Hz"
    "After: $(Format-AudioFormat $after)"

    if ($matched) {
        'Result: PASS'
        exit 0
    }

    'Result: FAIL (the value read back does not match the request)'
    exit 1
}
catch {
    Write-Error "ERROR: $($_.Exception.Message)"
    exit 2
}
finally {
    Remove-Item -LiteralPath $tempFormatPath -Force -ErrorAction SilentlyContinue
}
