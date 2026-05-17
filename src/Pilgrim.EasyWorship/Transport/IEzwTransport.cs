using System.IO.Pipelines;

namespace Pilgrim.EasyWorship.Transport;

internal interface IEzwTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    PipeReader Reader { get; }
    PipeWriter Writer { get; }

    Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct);
    Task DisconnectAsync();
}

internal interface IEzwTransportFactory
{
    IEzwTransport Create();
}
