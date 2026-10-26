using System.IO.Compression;
using System.Net;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Infrastructure.Darktide;

namespace HD2Mimnt.Tests.Darktide;

public sealed class DarktideReleaseClientTests
{
    [Fact]
    public async Task DownloadPackage_WritesValidatedZipAndClosesFileHandle()
    {
        var root = Path.Combine(Path.GetTempPath(), "DarktideMimntTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zipBytes = CreateZip();
            using var http = new HttpClient(new StubHandler(zipBytes));
            var client = new DarktideReleaseClient(http);
            var package = new DarktideReleasePackage(DarktideReleasePackageType.Full, "DML.zip",
                "https://github.com/user-attachments/files/1/DML.zip", "test");

            var downloaded = await client.DownloadPackageAsync(package, root);

            Assert.True(File.Exists(downloaded.FilePath));
            Assert.Equal(zipBytes.Length, downloaded.Size);
            Assert.Equal(64, downloaded.Sha256.Length);
            using var exclusive = new FileStream(downloaded.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(exclusive.Length > 0);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static byte[] CreateZip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("binaries/mod_loader").Open());
            writer.Write("return {}");
        }
        return stream.ToArray();
    }

    private sealed class StubHandler(byte[] responseBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes),
                RequestMessage = request
            };
            response.Content.Headers.ContentLength = responseBytes.Length;
            return Task.FromResult(response);
        }
    }
}
