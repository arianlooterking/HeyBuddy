using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Clicky.Core;
using Clicky.Runtime;
using Xunit;

namespace Clicky.Runtime.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ClickyDownloadTests", Guid.NewGuid().ToString("N"));
    public DownloadTests() => Directory.CreateDirectory(directory);
    [Fact]
    public async Task ResumesAPartialAndOnlyPromotesAfterChecksumVerification()
    {
        var data = Encoding.UTF8.GetBytes("verified download");
        var target = Path.Combine(directory, "model.gguf");
        await File.WriteAllBytesAsync(target + ".part", data[..5]);
        using var server = new MiniServer(data);
        var serverTask = server.ServeAsync();
        await using var manager = new ModelManager(new());
        var asset = new ModelAsset("model.gguf", server.Url, Convert.ToHexString(SHA256.HashData(data)), data.Length);
        await manager.DownloadVerifiedAsync(asset, target, null, default);
        await serverTask;
        Assert.Equal(5, server.RequestedOffset);
        Assert.Equal(data, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"));
    }
    [Fact]
    public async Task BadChecksumNeverOverwritesAnExistingFile()
    {
        var data = Encoding.UTF8.GetBytes("bad content");
        var target = Path.Combine(directory, "model.gguf");
        await File.WriteAllTextAsync(target, "preserved original");
        using var server = new MiniServer(data);
        var serverTask = server.ServeAsync();
        await using var manager = new ModelManager(new());
        var asset = new ModelAsset("model.gguf", server.Url, new string('0', 64), data.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.DownloadVerifiedAsync(asset, target, null, default));
        await serverTask;
        Assert.Equal("preserved original", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(directory, "*.rejected-*"));
    }
    [Fact]
    public async Task CancellationPreservesResumableBytes()
    {
        var target = Path.Combine(directory, "model.gguf");
        await File.WriteAllTextAsync(target + ".part", "partial");
        await using var manager = new ModelManager(new());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.DownloadVerifiedAsync(new("model.gguf", "https://example.com/model", new string('0', 64), 100), target, null, cancelled.Token));
        Assert.Equal("partial", await File.ReadAllTextAsync(target + ".part"));
        Assert.False(File.Exists(target));
    }
    public void Dispose() => Directory.Delete(directory, true);

    private sealed class MiniServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly byte[] data;
        public string Url
        {
            get;
        }
        public int RequestedOffset
        {
            get; private set;
        }
        public MiniServer(byte[] data)
        {
            this.data = data;
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/asset";
        }
        public async Task ServeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                    RequestedOffset = int.Parse(line[13..].TrimEnd('-'));
            var count = data.Length - RequestedOffset;
            var header = RequestedOffset > 0 ? $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {RequestedOffset}-{data.Length - 1}/{data.Length}\r\n" : "HTTP/1.1 200 OK\r\n";
            header += $"Content-Length: {count}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token);
            await stream.WriteAsync(data.AsMemory(RequestedOffset), timeout.Token);
        }
        public void Dispose() => listener.Stop();
    }
}
