using System.IO;
using System.Linq;
using System.Threading.Tasks;
using D6Downloader.Debrid;
using D6Downloader.Platform.Linux;
using Xunit;

namespace D6Downloader.Tests;

public class KeyFileTests
{
    private const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Fact]
    public void Key_is_created_private_and_stays_the_same()
    {
        using var dir = new TempDir();
        string path = dir.File("neu/master.key");
        var provider = new KeyFileSecretKeyProvider(path);
        Assert.False(File.Exists(path)); // erst beim ersten Gebrauch

        string secret = SecretProtector.Protect("geheim", provider);

        Assert.Equal(32, new FileInfo(path).Length);
        Assert.Equal(Owner, File.GetUnixFileMode(path));
        Assert.Equal(Owner | UnixFileMode.UserExecute, File.GetUnixFileMode(dir.File("neu")));
        Assert.Equal("geheim", SecretProtector.Unprotect(secret, new KeyFileSecretKeyProvider(path)));
        // keine Reste außer der leeren Sperrdatei
        Assert.Equal(new[] { path, path + ".lock" }, Directory.GetFiles(dir.File("neu")).Order());
    }

    [Fact]
    public void Open_permissions_are_tightened()
    {
        using var dir = new TempDir();
        string path = dir.File("master.key");
        new KeyFileSecretKeyProvider(path).GetKey();
        File.SetUnixFileMode(path, Owner | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        new KeyFileSecretKeyProvider(path).GetKey();

        Assert.Equal(Owner, File.GetUnixFileMode(path));
    }

    [Fact]
    public void A_damaged_key_file_is_never_overwritten()
    {
        using var dir = new TempDir();
        string path = dir.File("master.key");
        File.WriteAllText(path, "kaputt");
        var provider = new KeyFileSecretKeyProvider(path);

        Assert.Throws<IOException>(() => provider.GetKey());
        Assert.Null(SecretProtector.Unprotect("v1:" + new string('A', 60), provider));
        Assert.Equal("kaputt", File.ReadAllText(path));
    }

    [Fact]
    public async Task Parallel_first_use_agrees_on_one_key()
    {
        using var dir = new TempDir();
        for (int round = 0; round < 50; round++)
        {
            string path = dir.File($"{round}/master.key");

            byte[][] keys = await Task.WhenAll(Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => new KeyFileSecretKeyProvider(path).GetKey())));

            Assert.All(keys, k => Assert.Equal(keys[0], k));
        }
    }
}
