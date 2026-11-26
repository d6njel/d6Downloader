using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace D6Downloader.Tests;

/// <summary>
/// RAR-Testdaten: die Dateien aus libarchive (<c>Fixtures/rar</c>, uuencodiert) und selbst geschriebene RAR-4-Archive ohne
/// Kompression (zum Packen gibt es kein freies Programm).
/// </summary>
internal static class RarSamples
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "rar");

    /// <summary>Dekodiert <c>test_read_format_{name}.rar</c> in den Ordner und gibt den Pfad zurück.</summary>
    public static string Fixture(string name, string dir)
    {
        string file = "test_read_format_" + name + ".rar";
        var data = new MemoryStream();
        bool begun = false;
        foreach (string line in File.ReadLines(Path.Combine(Fixtures, file + ".uu")))
        {
            if (!begun)
            {
                begun = line.StartsWith("begin ", StringComparison.Ordinal);
                continue;
            }
            if (line == "end") break;
            if (line.Length == 0) continue;
            int count = (line[0] - 32) & 63, bits = 0, buffer = 0;
            for (int i = 1; count > 0; i++)
            {
                buffer = (buffer << 6) | (i < line.Length ? (line[i] - 32) & 63 : 0);
                bits += 6;
                if (bits < 8) continue;
                bits -= 8;
                data.WriteByte((byte)(buffer >> bits));
                count--;
            }
        }
        string path = Path.Combine(dir, file);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public static string[] Volumes(string dir, string format, IEnumerable<int> numbers) =>
        numbers.Select(n => Fixture(string.Format(format, n), dir)).ToArray();

    // ---------------------------------------------------------------- RAR 4, gespeichert

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static void Block(Stream to, byte type, int flags, byte[] body)
    {
        var header = new byte[7 + body.Length];
        header[2] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), (ushort)flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), (ushort)header.Length);
        body.CopyTo(header, 7);
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)Crc32(header.AsSpan(2)));
        to.Write(header);
    }

    private static readonly byte[] Signature4 = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };
    private static readonly byte[] Signature5 = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };

    /// <summary>
    /// RAR 4 oder 5 mit mehreren gespeicherten Dateien. <paramref name="ends"/>: die Länge nach jeder Datei (dort lässt sich
    /// genau zwischen zwei Dateien abschneiden). Der Ende-Block lässt sich weglassen.
    /// </summary>
    public static byte[] Several(int version, IReadOnlyList<(string Name, byte[] Content)> files, out List<int> ends, bool endBlock = true)
    {
        var to = new MemoryStream();
        ends = new List<int>();
        if (version == 4)
        {
            to.Write(Signature4);
            Block(to, 0x73, 0, new byte[6]);
        }
        else
        {
            to.Write(Signature5);
            Block5(to, 1, 0, new byte[] { 0 });
        }
        foreach (var (name, content) in files)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(name);
            var head = new MemoryStream();
            var w = new BinaryWriter(head);
            if (version == 4)
            {
                w.Write((uint)content.Length);
                w.Write((uint)content.Length);
                w.Write((byte)3);
                w.Write(Crc32(content));
                w.Write(0x58210000u);
                w.Write((byte)20);
                w.Write((byte)0x30);
                w.Write((ushort)encoded.Length);
                w.Write(0x81A4u);
                w.Write(encoded);
                Block(to, 0x74, 0x8000, head.ToArray());
            }
            else
            {
                VInt(head, 0x04);                 // CRC32 folgt
                VInt(head, (ulong)content.Length);
                VInt(head, 0x81A4);
                w.Write(Crc32(content));
                VInt(head, 0);                    // gespeichert
                VInt(head, 1);                    // Unix
                VInt(head, (ulong)encoded.Length);
                head.Write(encoded);
                Block5(to, 2, 0x02, head.ToArray(), content.Length);
            }
            to.Write(content);
            ends.Add((int)to.Length);
        }
        if (endBlock && version == 4) Block(to, 0x7B, 0x4000, Array.Empty<byte>());
        else if (endBlock) Block5(to, 5, 0, new byte[] { 0 });
        return to.ToArray();
    }

    private static void VInt(Stream to, ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            to.WriteByte(value != 0 ? (byte)(b | 0x80) : b);
        } while (value != 0);
    }

    private static void Block5(Stream to, int type, int flags, byte[] body, int dataSize = -1)
    {
        var fields = new MemoryStream();
        VInt(fields, (ulong)type);
        VInt(fields, (ulong)flags);
        if (dataSize >= 0) VInt(fields, (ulong)dataSize);
        fields.Write(body);
        var sized = new MemoryStream();
        VInt(sized, (ulong)fields.Length);
        sized.Write(fields.ToArray());
        byte[] bytes = sized.ToArray();
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32(bytes));
        to.Write(crc);
        to.Write(bytes);
    }

    /// <summary>
    /// Eine Datei auf <paramref name="volumes"/> Teile verteilt, als <c>name.part1.rar</c> … oder (<paramref name="oldNames"/>)
    /// <c>name.rar</c>, <c>name.r00</c> … RAR 4 trennt Ordner im Namen mit <c>\</c>.
    /// </summary>
    public static string[] Stored(string dir, string entryName, byte[] content, int volumes = 1, bool oldNames = false)
    {
        var paths = new string[volumes];
        byte[] name = Encoding.UTF8.GetBytes(entryName);
        int size = (content.Length + volumes - 1) / volumes;
        for (int v = 0; v < volumes; v++)
        {
            var part = content.Skip(v * size).Take(size).ToArray();
            bool first = v == 0, last = v == volumes - 1;
            paths[v] = Path.Combine(dir,
                volumes == 1 ? "set.rar" : oldNames ? (first ? "set.rar" : $"set.r{v - 1:D2}") : $"set.part{v + 1}.rar");
            using var file = File.Create(paths[v]);
            file.Write(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 });
            int main = volumes == 1 ? 0 : 0x0001 | (oldNames ? 0 : 0x0010) | (first ? 0x0100 : 0);
            Block(file, 0x73, main, new byte[6]);

            var head = new MemoryStream();
            var w = new BinaryWriter(head);
            w.Write((uint)part.Length);
            w.Write((uint)content.Length);
            w.Write((byte)3);                              // Unix
            w.Write(Crc32(last ? content : part));         // angeschnittene Teile tragen die Summe ihres Stücks
            w.Write(0x58210000u);                          // Zeit (DOS)
            w.Write((byte)20);
            w.Write((byte)0x30);                           // gespeichert
            w.Write((ushort)name.Length);
            w.Write(0x81A4u);                              // -rw-r--r--
            w.Write(name);
            Block(file, 0x74, 0x8000 | (first ? 0 : 0x01) | (last ? 0 : 0x02), head.ToArray());
            file.Write(part);
            Block(file, 0x7B, 0x4000 | (last ? 0 : 0x0001), Array.Empty<byte>());
        }
        return paths;
    }
}
