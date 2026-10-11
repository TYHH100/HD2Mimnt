using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace HD2Mimnt.UI.Services;

/// <summary>同一用户/数据目录只保留一个窗口，协议参数通过管道留在内存中。</summary>
public sealed class BrowserProtocolHost : IDisposable
{
    private const int MaxBytes = 8192;
    private readonly Mutex _instance;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<string> _pending = new();
    private readonly object _gate = new();
    private Action<string>? _receiver;
    private readonly Task _listener;
    public bool IsPrimary { get; }

    public BrowserProtocolHost(string scope)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        _pipeName = "HD2Mimnt-" + hash;
        _instance = new Mutex(false, @"Local\" + _pipeName, out var created);
        IsPrimary = created;
        _listener = created ? ListenAsync() : Task.CompletedTask;
    }

    public static string GetScope()
    {
        var data = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
        var directory = string.IsNullOrWhiteSpace(data)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD2Mimnt")
            : Path.GetFullPath(data);
        return Environment.UserDomainName + "\\" + Environment.UserName + ":" + directory.TrimEnd('\\', '/').ToUpperInvariant();
    }

    public static string GetProtocolArgument(IEnumerable<string> args)
        => args.FirstOrDefault(value => IsProtocolArgument(value)) ?? "";

    private static bool IsProtocolArgument(string value) => Encoding.UTF8.GetByteCount(value) <= MaxBytes &&
        !value.Any(char.IsControl) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "nxm";

    public void Receive(string value)
    {
        if (value.Length != 0 && !IsProtocolArgument(value)) return;
        lock (_gate)
        {
            if (_receiver != null) _receiver(value);
            else if (_pending.Count < 8) _pending.Enqueue(value);
        }
    }

    public void SetReceiver(Action<string> receiver)
    {
        lock (_gate)
        {
            _receiver = receiver;
            while (_pending.TryDequeue(out var value)) receiver(value);
        }
    }

    public async Task<bool> ForwardAsync(string value)
    {
        if (value.Length != 0 && !IsProtocolArgument(value)) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token);
            var payload = Encoding.UTF8.GetBytes(value);
            var size = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, payload.Length);
            await client.WriteAsync(size, timeout.Token);
            await client.WriteAsync(payload, timeout.Token);
            var ack = new byte[1];
            await client.ReadExactlyAsync(ack, timeout.Token);
            return ack[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var size = new byte[4];
                await server.ReadExactlyAsync(size, timeout.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(size);
                if (length < 0 || length > MaxBytes) continue;
                var payload = new byte[length];
                await server.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
                var value = new UTF8Encoding(false, true).GetString(payload);
                if (value.Length != 0 && !IsProtocolArgument(value)) continue;
                Receive(value);
                await server.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or DecoderFallbackException)
            {
                if (!_stop.IsCancellationRequested) await Task.Delay(100, _stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.GetAwaiter().GetResult();
        lock (_gate) { _receiver = null; _pending.Clear(); }
        _stop.Dispose();
        _instance.Dispose();
    }
}
