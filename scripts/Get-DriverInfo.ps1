<#
.SYNOPSIS
  Reports the GPU and audio HDMI driver metadata that the spec's story 67 requires.

.DESCRIPTION
  Emits a single JSON object on stdout. The .NET application spawns this script through
  PowerShell and reads its stdout. Any failure (PowerShell missing, WMI unavailable,
  network/registry access denied) leaves stdout empty; the caller treats that as "no
  driver metadata available" and falls back to null fields.

  The JSON shape is stable:
    {
      "Gpu":       { "Name": "...", "Version": "...", "Provider": "..." },
      "AudioHdmi": [ { "Name": "...", "Version": "...", "Provider": "..." }, ... ]
    }

  GPU is the first Win32_PnPSignedDriver whose DeviceClass is "Display".
  AudioHdmi is every Win32_PnPSignedDriver whose DeviceClass is "MEDIA" and whose
  DeviceName contains "High Definition Audio" (matches NVIDIA HDMI audio and the
  built-in Realtek HDA endpoints).
#>

$ErrorActionPreference = 'SilentlyContinue'

$gpus = Get-CimInstance Win32_PnPSignedDriver |
    Where-Object { $_.DeviceClass -eq 'Display' } |
    Select-Object -First 1 -Property DeviceName, DriverVersion, DriverProvider, InfName

$audio = Get-CimInstance Win32_PnPSignedDriver |
    Where-Object { $_.DeviceClass -eq 'MEDIA' -and $_.DeviceName -match 'High Definition Audio' } |
    Select-Object -Property DeviceName, DriverVersion, DriverProvider, InfName

$payload = @{
    Gpu       = if ($gpus) {
        @{
            Name     = [string]$gpus.DeviceName
            Version  = [string]$gpus.DriverVersion
            Provider = [string]$gpus.DriverProvider
        }
    } else { $null }
    AudioHdmi = if ($audio) {
        ,@($audio | ForEach-Object {
            @{
                Name     = [string]$_.DeviceName
                Version  = [string]$_.DriverVersion
                Provider = [string]$_.DriverProvider
            }
        })
    } else { ,@() }
}

ConvertTo-Json -InputObject $payload -Compress