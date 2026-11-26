using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace D6Downloader.Packages;

/// <summary>
/// Belegt, dass ein RAR (4 oder 5) vollständig ist. <c>unar</c> meldet auch dann Erfolg, wenn ein Archiv genau zwischen zwei
/// Dateien abgeschnitten ist. Geprüft wird nur die Struktur, nicht der Inhalt (den prüft der Entpacker): Jeder Teil ist eine
/// lückenlose Kette von Blöcken mit gültiger Kopf-Prüfsumme bis zum Ende-Block, nur der letzte Teil ohne „weitere Volumes“.
/// Alles, was sich so nicht belegen lässt (kein Ende-Block, verschlüsselte Köpfe, Unbekanntes), gilt als nicht belegt.
/// </summary>
internal static class RarCompleteness
{
    /// <summary>So weit hinten darf die Signatur in einem selbstentpackenden Archiv stehen.</summary>
    private const int MaxStub = 2 * 1024 * 1024;

    private const int MaxBlocks = 1_000_000;

    private static readonly byte[] Magic = { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07 };

    /// <summary>Was ein Teil über seine Nachbarn sagt.</summary>
    private readonly record struct Volume(int Version, bool IsVolume, bool More, bool SplitBefore, bool SplitAfter);

    /// <summary>Beginnt die Datei als RAR?</summary>
    public static bool IsRar(string path)
    {
        try
        {
            using var file = Open(path);
            var head = new byte[Magic.Length];
            return file.ReadAtLeast(head, head.Length, false) == head.Length && head.AsSpan().SequenceEqual(Magic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsProven(IReadOnlyList<string> parts)
    {
        try
        {
            // Nach der Nummer des Teils: name.part1.rar, name.part2.rar … bzw. name.rar, name.r00 …
            var ordered = parts.Distinct(StringComparer.Ordinal)
                .OrderBy(p => PackageDetector.Parse(Path.GetFileName(p))?.Order ?? 0).ToList();
            if (ordered.Count == 0) return false;
            int version = 0;
            bool splitOpen = false;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (Read(ordered[i]) is not { } volume) return false;
                bool last = i == ordered.Count - 1;
                if (volume.More == last) return false;
                if (ordered.Count > 1 && !volume.IsVolume) return false;
                if (volume.SplitBefore != splitOpen) return false; // eine angeschnittene Datei muss im nächsten Teil weitergehen
                if (i > 0 && volume.Version != version) return false;
                version = volume.Version;
                splitOpen = volume.SplitAfter;
            }
            return !splitOpen;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static Volume? Read(string path)
    {
        using var file = Open(path);
        var start = new byte[(int)Math.Min(file.Length, MaxStub)];
        file.ReadExactly(start);
        int at = start.AsSpan().IndexOf(Magic);
        if (at < 0) return null;
        // Etwas davor gibt es nur bei einem selbstentpackenden Programm (Windows „MZ“ oder Linux „ELF“).
        bool program = start.AsSpan().StartsWith("MZ"u8) || start.AsSpan().StartsWith("\u007fELF"u8);
        if (at > 0 && !program) return null;
        // Die Signatur kann auch im Programmteil selbst vorkommen: die erste Stelle, hinter der eine gültige Kette steht.
        while (at >= 0)
        {
            var rest = start.AsSpan(at);
            if (rest.Length >= 7 && rest[6] == 0x00 && Rar4(file, at + 7) is { } four) return four;
            if (rest.Length >= 8 && rest[6] == 0x01 && rest[7] == 0x00 && Rar5(file, at + 8) is { } five) return five;
            if (!program) return null;
            int next = start.AsSpan(at + 1).IndexOf(Magic);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return null;
    }

    /// <summary>Hinter dem Ende-Block dürfen höchstens Nullen stehen.</summary>
    private static bool OnlyPadding(FileStream file, long from)
    {
        if (file.Length - from > 64 * 1024) return false;
        file.Position = from;
        int b;
        while ((b = file.ReadByte()) >= 0)
            if (b != 0) return false;
        return true;
    }

    // ---------------------------------------------------------------- RAR 4

    private static Volume? Rar4(FileStream file, long position)
    {
        bool main = false, isVolume = false, splitBefore = false, splitAfter = false, anyFile = false;
        var fixedPart = new byte[7];
        for (int n = 0; n < MaxBlocks; n++)
        {
            file.Position = position;
            if (file.ReadAtLeast(fixedPart, 7, false) < 7) return null;
            int type = fixedPart[2];
            int flags = fixedPart[3] | fixedPart[4] << 8;
            int size = fixedPart[5] | fixedPart[6] << 8;
            if (size < 7) return null;
            var header = new byte[size];
            file.Position = position;
            if (file.ReadAtLeast(header, size, false) < size) return null;
            if ((ushort)Crc32.Append(0, header[2..], size - 2) != (header[0] | header[1] << 8)) return null;

            bool isFile = type == 0x74, isSub = type == 0x7A;
            long data = 0;
            if (isFile || isSub || (flags & 0x8000) != 0)
            {
                if (size < 11) return null;
                data = BitConverter.ToUInt32(header, 7);
                if ((isFile || isSub) && (flags & 0x100) != 0)
                {
                    if (size < 40) return null;
                    data = checked(data + ((long)BitConverter.ToUInt32(header, 32) << 32));
                }
            }

            if (!main)
            {
                if (type != 0x73 || (flags & 0x0080) != 0) return null; // 0x80: verschlüsselte Köpfe
                main = true;
                isVolume = (flags & 0x0001) != 0;
            }
            else if (isFile)
            {
                if (!anyFile) splitBefore = (flags & 0x01) != 0;
                anyFile = true;
                splitAfter = (flags & 0x02) != 0;
            }

            long next = checked(position + size + data);
            if (next > file.Length) return null;
            if (type == 0x7B)
                return OnlyPadding(file, next) ? new Volume(4, isVolume, (flags & 0x0001) != 0, splitBefore, splitAfter) : null;
            position = next;
        }
        return null;
    }

    // ---------------------------------------------------------------- RAR 5

    private static bool VInt(ReadOnlySpan<byte> data, ref int at, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && at < data.Length; shift += 7)
        {
            byte b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    private static Volume? Rar5(FileStream file, long position)
    {
        bool main = false, isVolume = false, splitBefore = false, splitAfter = false, anyFile = false;
        var start = new byte[7]; // CRC32 und höchstens drei Bytes Kopfgröße
        for (int n = 0; n < MaxBlocks; n++)
        {
            file.Position = position;
            int read = file.ReadAtLeast(start, start.Length, false);
            int at = 4;
            if (read < 6 || !VInt(start.AsSpan(0, read), ref at, out ulong headerSize) || headerSize is 0 or > 2 * 1024 * 1024) return null;
            int total = at + (int)headerSize;
            var block = new byte[total];
            file.Position = position;
            if (file.ReadAtLeast(block, total, false) < total) return null;
            if (Crc32.Append(0, block[4..], total - 4) != BitConverter.ToUInt32(block, 0)) return null;

            if (!VInt(block, ref at, out ulong type) || !VInt(block, ref at, out ulong flags)) return null;
            ulong extra = 0, data = 0;
            if ((flags & 1) != 0 && !VInt(block, ref at, out extra)) return null;
            if ((flags & 2) != 0 && !VInt(block, ref at, out data)) return null;
            long next = checked(position + total + (long)data);
            if (data > (ulong)file.Length || next > file.Length) return null;

            if (!main)
            {
                // Art 4 wäre die Verschlüsselung der Köpfe: dann ist nichts zu belegen.
                if (type != 1 || !VInt(block, ref at, out ulong archive)) return null;
                main = true;
                isVolume = (archive & 1) != 0;
            }
            else if (type == 2)
            {
                if (!anyFile) splitBefore = (flags & 0x08) != 0;
                anyFile = true;
                splitAfter = (flags & 0x10) != 0;
            }
            else if (type == 5)
            {
                if (!VInt(block, ref at, out ulong end)) return null;
                return OnlyPadding(file, next) ? new Volume(5, isVolume, (end & 1) != 0, splitBefore, splitAfter) : null;
            }
            else if (type is not 3) return null;
            position = next;
        }
        return null;
    }
}
