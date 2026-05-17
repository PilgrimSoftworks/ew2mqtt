using System.Net;
using System.Net.Sockets;

namespace Pilgrim.EasyWorship.Discovery;

/// <summary>
/// Injectable seam for "can a TCP connection actually be established to this
/// address:port?". Mirrors the <see cref="IZeroconfShim"/>/<see cref="IProcessRunner"/>
/// pattern so reachability selection can be unit-tested without sockets.
/// </summary>
internal interface ITcpProbe
{
    /// <summary>
    /// Attempts a bounded TCP connect. Returns <c>true</c> only if the peer
    /// accepted the connection within <paramref name="timeout"/>. Never throws
    /// for connection failures (refused/unreachable/timed-out → <c>false</c>);
    /// only a genuine caller cancellation via <paramref name="ct"/> propagates.
    /// </summary>
    Task<bool> CanConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct);
}

internal sealed class TcpProbe : ITcpProbe
{
    public async Task<bool> CanConnectAsync(
        IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        using TcpClient client = new(address.AddressFamily);
        try
        {
            await client.ConnectAsync(address, port, linked.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation — let it propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own per-address timeout elapsed.
            return false;
        }
        catch (SocketException)
        {
            // Refused / no route / unreachable.
            return false;
        }
        catch (ObjectDisposedException)
        {
            // Socket torn down by the timeout race.
            return false;
        }
    }
}
