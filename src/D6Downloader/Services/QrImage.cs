using System.IO;
using Avalonia.Media.Imaging;
using QRCoder;

namespace D6Downloader.Services;

/// <summary>Ein QR-Code als Bild. Er enthält den Kopplungs-Schlüssel: nur anzeigen, nie speichern.</summary>
public static class QrImage
{
    public static Bitmap Create(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(8);
        return new Bitmap(new MemoryStream(png));
    }
}
