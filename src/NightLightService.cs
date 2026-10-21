using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace BatteryHud;

/// <summary>
/// Windows Night Light. There is no public API: the state lives in a REG_BINARY blob under
/// CloudStore, Microsoft Bond CompactBinary v1 inside a CloudStore envelope.
///
/// The blob is NESTED - an outer envelope that itself contains a second Bond blob, and both
/// begin with the same 43 42 01 00 marker. Measured on Windows 11 25H2 ARM64 (build 26200):
///
///   OFF (41 bytes)
///     43420100 0a0201002a06 f697bad306 2a2b0e 13 43420100      d00a02c614 &lt;timestamp&gt; 000000
///   ON  (43 bytes)
///     43420100 0a0201002a06 f997bad306 2a2b0e 15 43420100 1000 d00a02c614 &lt;timestamp&gt; 000000
///     |outer                |ts varint         |len |inner    |ON flag
///
/// Three things move together when the state changes:
///
///   1. the two-byte flag 10 00 immediately after the INNER marker, present only when on;
///   2. the length prefix in the byte before the inner marker, 0x13 -&gt; 0x15, i.e. +2 for the
///      inserted flag;
///   3. the outer timestamp varint, which has to increase or Windows ignores the write.
///
/// Miss any one of them and the blob is rejected or, worse, silently corrupted. This is why
/// the widely copied "paste a fixed hex blob" recipes break between builds and flatten the
/// user's colour temperature when they do: everything after the flag is carried across here
/// untouched, so schedule and temperature survive.
/// </summary>
public static class NightLightService
{
    // Newer Windows 10 and all Windows 11.
    private const string CurrentPath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    // Seen on some Windows 11 builds.
    private const string CloudPath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Cloud\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    // Older Windows 10 builds.
    private const string CachePath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\Cache\DefaultAccount\$$windows.data.bluelightreduction.bluelightreductionstate\Current";

    private const string ValueName = "Data";

    private static readonly byte[] Marker = { 0x43, 0x42, 0x01, 0x00 };
    private static readonly byte[] OnFlag = { 0x10, 0x00 };

    /// <summary>Byte range holding the outer timestamp varint, which must increase on write.</summary>
    private const int TimestampStart = 10;
    private const int TimestampEnd = 15;

    /// <summary>
    /// False when the blob does not exist, which means night light has never been switched
    /// on. We deliberately do not synthesise a blob from scratch - too easy to write
    /// something Windows rejects - so the UI points the user at Settings instead.
    /// </summary>
    public static bool Supported => ReadBlob() is not null;

    public static bool? IsEnabled()
    {
        var data = ReadBlob();
        if (data is null) return null;
        int i = FindInnerMarker(data);
        if (i < 0) return null;
        return HasOnFlag(data, i);
    }

    public static bool SetEnabled(bool enabled)
    {
        var (data, hive, path) = ReadBlobWithSource();
        if (data is null || hive is null || path is null)
        {
            Log.Warn("night light: no CloudStore blob; enable it once in Settings first");
            return false;
        }

        int inner = FindInnerMarker(data);
        if (inner < 1)
        {
            Log.Warn($"night light: inner Bond marker not found in {data.Length} bytes. " +
                     $"blob={Hex(data)} path={path}");
            return false;
        }

        // The byte immediately before the inner marker is that blob's length prefix, and it
        // has to move with the flag. Bond varints are single-byte below 0x80, so refuse to
        // touch anything that would overflow into a second byte rather than mangle it.
        int lenAt = inner - 1;
        int declared = data[lenAt];
        if (declared >= 0x7E || inner + declared > data.Length)
        {
            Log.Warn($"night light: unexpected inner length {declared} at {lenAt} " +
                     $"(blob {data.Length} bytes); refusing to write. blob={Hex(data)}");
            return false;
        }

        bool currently = HasOnFlag(data, inner);
        Log.Info($"night light: want={enabled} current={currently} inner@{inner} len={declared} " +
                 $"blob={data.Length}B path={Short(path)}");

        if (currently == enabled) return true;

        int flagAt = inner + Marker.Length;
        var updated = new List<byte>(data.Length + OnFlag.Length);
        updated.AddRange(data.Take(flagAt));

        if (enabled)
        {
            updated.AddRange(OnFlag);
            updated.AddRange(data.Skip(flagAt));
        }
        else
        {
            updated.AddRange(data.Skip(flagAt + OnFlag.Length));
        }

        var blob = updated.ToArray();
        blob[lenAt] = (byte)(enabled ? declared + OnFlag.Length : declared - OnFlag.Length);
        BumpTimestamp(blob);

        Log.Info($"night light: before={Hex(data)}");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            if (key is null)
            {
                Log.Warn("night light: could not open the key for writing");
                return false;
            }
            key.SetValue(ValueName, blob, RegistryValueKind.Binary);
            key.Flush();
            Log.Info($"night light: wrote={Hex(blob)}");

            // Read straight back. Windows can overwrite our value with its own cached copy,
            // in which case the write "succeeded" but nothing changed.
            var check = ReadBlob();
            bool? now = check is null ? null : (FindInnerMarker(check) is int j && j >= 0 ? HasOnFlag(check, j) : null);
            if (now != enabled)
            {
                Log.Warn($"night light: wrote {enabled} but reads back as {(now?.ToString() ?? "unreadable")}. " +
                         $"after={(check is null ? "<none>" : Hex(check))}");
                return false;
            }

            Log.Info($"night light: now {(enabled ? "on" : "off")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"night light write: {ex.Message}");
            return false;
        }
    }

    /// <summary>Hex dump for the log. These blobs are short - well under 100 bytes.</summary>
    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    private static string Short(string path) =>
        path.Contains("Cache") ? "Cache" : path.Contains(@"\Cloud\") ? "Cloud" : "Current";

    public static bool Toggle()
    {
        var state = IsEnabled();
        if (state is null) return false;
        return SetEnabled(!state.Value);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Windows ignores a blob whose timestamp is not newer than the one it already holds.
    ///
    /// The timestamp is a Bond varint, not a plain FILETIME, so it cannot simply be
    /// overwritten with the current time - a longer encoding would shift every following
    /// byte. Incrementing the low byte keeps the length identical and is enough to make the
    /// value newer. Windows itself does the same thing: in the captured samples this byte
    /// went f6 -> f9 across a toggle while the encoded length stayed put.
    /// </summary>
    private static void BumpTimestamp(byte[] blob)
    {
        for (int i = TimestampStart; i < Math.Min(TimestampEnd, blob.Length); i++)
        {
            // In a varint the top bit is the continuation flag and only the low 7 bits carry
            // value, so the increment has to happen inside those 7 bits with the flag left
            // alone. Treating the byte as a plain number walks straight past 0x7F and
            // destroys the encoding - 0xF6 is not "246 about to overflow", it is a
            // continuation byte holding the value 0x76.
            int low = blob[i] & 0x7F;
            if (low != 0x7F)
            {
                blob[i] = (byte)((blob[i] & 0x80) | (low + 1));
                return;
            }
            // Low bits saturated: zero them, keep the continuation flag, carry onwards.
            blob[i] = (byte)(blob[i] & 0x80);
        }
        Log.Warn("night light: could not advance the timestamp varint");
    }

    private static bool HasOnFlag(byte[] data, int markerIndex)
    {
        int at = markerIndex + Marker.Length;
        return at + 1 < data.Length && data[at] == OnFlag[0] && data[at + 1] == OnFlag[1];
    }

    /// <summary>
    /// Finds the INNER Bond blob. The outer envelope starts with the same four bytes at
    /// offset 0, so the search deliberately starts past it - matching the outer marker was
    /// the original bug here, and it made the state always read as "off".
    /// </summary>
    private static int FindInnerMarker(byte[] data)
    {
        for (int i = Marker.Length; i + Marker.Length <= data.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < Marker.Length; j++)
            {
                if (data[i + j] != Marker[j]) { hit = false; break; }
            }
            if (hit) return i;
        }
        return -1;
    }

    private static byte[]? ReadBlob() => ReadBlobWithSource().Data;

    private static (byte[]? Data, RegistryKey? Hive, string? Path) ReadBlobWithSource()
    {
        foreach (var path in new[] { CurrentPath, CloudPath, CachePath })
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue(ValueName) is byte[] data && data.Length > 20)
                    return (data, Registry.CurrentUser, path);
            }
            catch (Exception ex) { Log.Warn($"night light read ({path}): {ex.Message}"); }
        }
        return (null, null, null);
    }
}
