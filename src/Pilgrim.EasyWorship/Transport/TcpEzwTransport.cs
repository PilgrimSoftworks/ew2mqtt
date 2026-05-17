using System.IO.Pipelines;
using System.Net.Sockets;

namespace Pilgrim.EasyWorship.Transport;

internal sealed class TcpEzwTransport : IEzwTransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private PipeReader? _reader;
    private PipeWriter? _writer;

    public bool IsConnected => _client?.Connected == true;

    public PipeReader Reader => _reader ?? throw new InvalidOperationException("not connected");
    public PipeWriter Writer => _writer ?? throw new InvalidOperationException("not connected");

    public async Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        if (_client is not null)
        {
            throw new InvalidOperationException("already connected");
        }

        TcpClient client = new() { NoDelay = true };
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);

            _client = client;
            _stream = client.GetStream();
            _reader = PipeReader.Create(_stream);
            _writer = PipeWriter.Create(_stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_writer is not null)
            {
                await _writer.CompleteAsync().ConfigureAwait(false);
            }
            if (_reader is not null)
            {
                await _reader.CompleteAsync().ConfigureAwait(false);
            }
            _stream?.Dispose();
            _client?.Close();
        }
        finally
        {
            _stream = null;
            _client = null;
            _reader = null;
            _writer = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }
}

internal sealed class TcpEzwTransportFactory : IEzwTransportFactory
{
    public IEzwTransport Create() => new TcpEzwTransport();
}
