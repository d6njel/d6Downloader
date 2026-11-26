namespace D6Downloader.Packages;

/// <summary>CRC-32 wie in ZIP (Polynom 0xEDB88320). <c>System.IO.Compression</c> prüft sie beim Lesen nicht selbst.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>Weiterrechnen: Beginn mit 0, das Ergebnis geht in den nächsten Aufruf.</summary>
    public static uint Append(uint crc, byte[] buffer, int count)
    {
        crc = ~crc;
        for (int i = 0; i < count; i++) crc = Table[(byte)(crc ^ buffer[i])] ^ (crc >> 8);
        return ~crc;
    }
}
