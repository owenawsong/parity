# During a test call, squeeze the selected mute gesture twice, with a pause in between.
# Observes Windows' default communications input and Pairside's existing device log.
[CmdletBinding()]
param([ValidateRange(1, 300)][int] $Seconds = 30)

$ErrorActionPreference = 'Stop'
if (-not ('Pairside.CallInputProbe' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace Pairside {
    public static class CallInputProbe {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DefaultEndpoint(IntPtr self, int flow, int role, out IntPtr endpoint);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DeviceId(IntPtr self, out IntPtr id);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Activate(IntPtr self, ref Guid iid, int context, IntPtr parameters, out IntPtr instance);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetMute(IntPtr self, out int muted);
        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr result);

        private static Delegate Slot(IntPtr target, int index, Type fn) {
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(target), index * IntPtr.Size), fn);
        }
        public static string Read(out string endpointId) {
            endpointId = null;
            Guid coclass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            Guid enumIID = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
            Guid volumeIID = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
            IntPtr enumerator = IntPtr.Zero, ep = IntPtr.Zero, volume = IntPtr.Zero, id = IntPtr.Zero;
            try {
                if (CoCreateInstance(ref coclass, IntPtr.Zero, 1, ref enumIID, out enumerator) < 0 ||
                    ((DefaultEndpoint)Slot(enumerator, 4, typeof(DefaultEndpoint)))(enumerator, 1, 2, out ep) < 0)
                    return "unavailable";
                if (((DeviceId)Slot(ep, 5, typeof(DeviceId)))(ep, out id) >= 0) endpointId = Marshal.PtrToStringUni(id);
                if (((Activate)Slot(ep, 3, typeof(Activate)))(ep, ref volumeIID, 23, IntPtr.Zero, out volume) < 0) return "unknown";
                int value;
                if (((GetMute)Slot(volume, 15, typeof(GetMute)))(volume, out value) < 0) return "unknown";
                return value == 0 ? "unmuted" : "muted";
            } finally {
                if (id != IntPtr.Zero) Marshal.FreeCoTaskMem(id);
                if (volume != IntPtr.Zero) Marshal.Release(volume);
                if (ep != IntPtr.Zero) Marshal.Release(ep);
                if (enumerator != IntPtr.Zero) Marshal.Release(enumerator);
            }
        }
    }
}
"@
}
$logPath = Join-Path $env:LOCALAPPDATA 'Pairside/Live/logs/wpods.log'
$reader = $null
try {
    if (Test-Path -LiteralPath $logPath) {
        $stream = [IO.File]::Open($logPath, 'Open', 'Read', [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        $null = $stream.Seek(0, [IO.SeekOrigin]::End)
        $reader = [IO.StreamReader]::new($stream)
    }
    Write-Output 'During a call, squeeze the mute gesture, wait two seconds, then squeeze again. Watch the mute icon inside the calling app, too.'
    $stopAt = [DateTime]::UtcNow.AddSeconds($Seconds)
    $last = ''
    while ([DateTime]::UtcNow -lt $stopAt) {
        $id = ''
        $mute = [Pairside.CallInputProbe]::Read([ref]$id)
        if ("$id|$mute" -ne $last) {
            $last = "$id|$mute"
            $name = 'Default communications microphone'
            if ($id -match '(\{[0-9a-f-]{36}\})$') {
                $key = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\$($Matches[1])\Properties"
                $props = Get-Item -LiteralPath $key -ErrorAction SilentlyContinue
                $label = if ($props) { $props.GetValue('{a45c254e-df1c-4efd-8020-67d146a850e0},14') }
                if (!$label) {
                    $node = Get-Item -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Enum\SWD\MMDEVAPI\$id" -ErrorAction SilentlyContinue
                    if ($node) { $label = $node.GetValue('FriendlyName') }
                }
                if ($label) { $name = $label }
            }
            Write-Output ("{0:T}  {1} — {2}" -f (Get-Date), $name, $mute)
        }
        if ($reader) {
            while ($null -ne ($line = $reader.ReadLine())) {
                $event = switch -Regex ($line) {
                    'RX aap: 040004001900' { 'AirPods reported a stem press'; break }
                    'RX aap: 04000400090027' { 'AirPods reported software-mute configuration'; break }
                    'RX aap: 04000400090024000([23])0000' { if ($Matches[1] -eq '3') {'AirPods confirm one press for mute'} else {'AirPods confirm two presses for mute'}; break }
                    'Hands-Free call audio (started|ended)' { "Hands-Free audio $($Matches[1])"; break }
                }
                if ($event) { Write-Output ("{0:T}  {1}" -f (Get-Date), $event) }
            }
        }
        Start-Sleep -Milliseconds 250
    }
} finally {
    if ($reader) { $reader.Dispose() }
}
