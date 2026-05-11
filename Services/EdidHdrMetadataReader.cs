using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace HdrImageViewer.Services;

public sealed record EdidHdrMetadata(
    double MaxLuminanceInNits,
    double? MaxFrameAverageLuminanceInNits,
    string Source);

public static class EdidHdrMetadataReader
{
    private const int DisplayDeviceActive = 0x1;

    public static bool TryReadForDisplay(string? displayDeviceName, out EdidHdrMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(displayDeviceName)
            && TryGetMonitorHardwareId(displayDeviceName, out var monitorHardwareId)
            && TryReadForMonitorHardwareId(monitorHardwareId, out metadata))
        {
            return true;
        }

        return TryReadBestAttachedMetadata(out metadata);
    }

    private static bool TryGetMonitorHardwareId(string displayDeviceName, out string monitorHardwareId)
    {
        monitorHardwareId = string.Empty;

        try
        {
            for (uint index = 0; index < 16; index++)
            {
                var device = new DisplayDevice
                {
                    cb = Marshal.SizeOf<DisplayDevice>()
                };

                if (!EnumDisplayDevices(displayDeviceName, index, ref device, 0))
                {
                    break;
                }

                if ((device.StateFlags & DisplayDeviceActive) == 0)
                {
                    continue;
                }

                var hardwareId = ExtractMonitorHardwareId(device.DeviceID);
                if (!string.IsNullOrWhiteSpace(hardwareId))
                {
                    monitorHardwareId = hardwareId;
                    return true;
                }
            }
        }
        catch
        {
            // Fall back to scanning display EDIDs below.
        }

        return false;
    }

    private static string ExtractMonitorHardwareId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return string.Empty;
        }

        var parts = deviceId
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], "MONITOR", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[i], "DISPLAY", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return parts.FirstOrDefault(static part => part.Length >= 6) ?? string.Empty;
    }

    private static bool TryReadForMonitorHardwareId(string monitorHardwareId, out EdidHdrMetadata metadata)
    {
        metadata = default!;

        try
        {
            using var displayKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            using var monitorKey = displayKey?.OpenSubKey(monitorHardwareId);
            if (monitorKey is null)
            {
                return false;
            }

            EdidHdrMetadata? best = null;
            foreach (var instanceName in monitorKey.GetSubKeyNames())
            {
                using var instanceKey = monitorKey.OpenSubKey(instanceName);
                if (TryReadFromInstanceKey(instanceKey, $"{monitorHardwareId}\\{instanceName}", out var candidate)
                    && (best is null || candidate.MaxLuminanceInNits > best.MaxLuminanceInNits))
                {
                    best = candidate;
                }
            }

            if (best is not null)
            {
                metadata = best;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryReadBestAttachedMetadata(out EdidHdrMetadata metadata)
    {
        metadata = default!;

        try
        {
            using var displayKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            if (displayKey is null)
            {
                return false;
            }

            EdidHdrMetadata? best = null;
            foreach (var monitorHardwareId in displayKey.GetSubKeyNames())
            {
                using var monitorKey = displayKey.OpenSubKey(monitorHardwareId);
                if (monitorKey is null)
                {
                    continue;
                }

                foreach (var instanceName in monitorKey.GetSubKeyNames())
                {
                    using var instanceKey = monitorKey.OpenSubKey(instanceName);
                    if (TryReadFromInstanceKey(instanceKey, $"{monitorHardwareId}\\{instanceName}", out var candidate)
                        && (best is null || candidate.MaxLuminanceInNits > best.MaxLuminanceInNits))
                    {
                        best = candidate;
                    }
                }
            }

            if (best is not null)
            {
                metadata = best;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryReadFromInstanceKey(RegistryKey? instanceKey, string source, out EdidHdrMetadata metadata)
    {
        metadata = default!;

        using var parametersKey = instanceKey?.OpenSubKey("Device Parameters");
        if (parametersKey?.GetValue("EDID") is not byte[] edid)
        {
            return false;
        }

        if (!TryParseHdrStaticMetadata(edid, out var maxLuminance, out var maxFrameAverageLuminance))
        {
            return false;
        }

        metadata = new EdidHdrMetadata(maxLuminance, maxFrameAverageLuminance, source);
        return true;
    }

    private static bool TryParseHdrStaticMetadata(
        ReadOnlySpan<byte> edid,
        out double maxLuminance,
        out double? maxFrameAverageLuminance)
    {
        maxLuminance = 0.0;
        maxFrameAverageLuminance = null;

        if (edid.Length < 128)
        {
            return false;
        }

        var extensionCount = edid[126];
        var availableExtensionCount = Math.Min(extensionCount, (edid.Length / 128) - 1);
        for (var blockIndex = 1; blockIndex <= availableExtensionCount; blockIndex++)
        {
            var blockOffset = blockIndex * 128;
            if (edid[blockOffset] != 0x02)
            {
                continue;
            }

            var detailedTimingOffset = edid[blockOffset + 2];
            var dataEnd = detailedTimingOffset is > 4 and <= 127
                ? blockOffset + detailedTimingOffset
                : blockOffset + 127;

            for (var offset = blockOffset + 4; offset < dataEnd;)
            {
                var header = edid[offset];
                var tag = header >> 5;
                var length = header & 0x1f;
                var nextOffset = offset + 1 + length;
                if (length == 0 || nextOffset > blockOffset + 128)
                {
                    offset = Math.Max(offset + 1, nextOffset);
                    continue;
                }

                if (tag == 0x7 && length >= 4 && edid[offset + 1] == 0x06)
                {
                    var desiredMaxLuminance = DecodeDesiredLuminance(edid[offset + 4]);
                    if (desiredMaxLuminance is > 0.0)
                    {
                        maxLuminance = desiredMaxLuminance.Value;
                        maxFrameAverageLuminance = length >= 5
                            ? DecodeDesiredLuminance(edid[offset + 5])
                            : null;
                        return true;
                    }
                }

                offset = nextOffset;
            }
        }

        return false;
    }

    private static double? DecodeDesiredLuminance(byte code)
    {
        return code == 0
            ? null
            : 50.0 * Math.Pow(2.0, code / 32.0);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplayDevices(
        string lpDevice,
        uint iDevNum,
        ref DisplayDevice lpDisplayDevice,
        uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
