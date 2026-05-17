using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Pilgrim.EasyWorship.Mqtt.Tests.Fixtures;

internal sealed class FakeEasyWorshipServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _clientConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private PipeReader? _reader;
    private Task? _accept;
    private readonly List<string> _received = new();
    private readonly object _lock = new();

    public FakeEasyWorshipServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
    }

    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

    public IReadOnlyList<string> Received
    {
        get
        {
            lock (_lock)
            {
                return _received.ToArray();
            }
        }
    }

    public Task ClientConnected => _clientConnected.Task;

    public void Start()
    {
        _accept = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(_cts.Token);
            _stream = _client.GetStream();
            _reader = PipeReader.Create(_stream);
            _clientConnected.TrySetResult();

            while (!_cts.Token.IsCancellationRequested)
            {
                ReadResult read = await _reader.ReadAsync(_cts.Token);
                ReadOnlySequence<byte> buffer = read.Buffer;
                while (TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
                {
                    string text = Encoding.UTF8.GetString(line.ToArray());
                    lock (_lock)
                    {
                        _received.Add(text);
                    }
                }
                _reader.AdvanceTo(buffer.Start, read.Buffer.End);
                if (read.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    public async Task SendAsync(string json)
    {
        if (_stream is null)
        {
            await _clientConnected.Task;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\r\n");
        await _stream!.WriteAsync(bytes, _cts.Token);
        await _stream.FlushAsync(_cts.Token);
    }

    public async Task<string?> WaitForFrameAsync(string contains, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_lock)
            {
                foreach (string f in _received)
                {
                    if (f.Contains(contains, StringComparison.Ordinal))
                    {
                        return f;
                    }
                }
            }
            await Task.Delay(20);
        }
        return null;
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _cts.CancelAsync(); } catch { }
        try
        {
            if (_accept is not null)
            {
                await _accept.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch { }
        _client?.Dispose();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        SequenceReader<byte> reader = new(buffer);
        if (reader.TryReadTo(out line, "\r\n"u8))
        {
            buffer = buffer.Slice(reader.Position);
            return true;
        }
        line = default;
        return false;
    }
}
