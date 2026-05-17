using System.Globalization;
using Microsoft.Extensions.Options;
using Pilgrim.EasyWorship;
using Pilgrim.EasyWorship.Events;
using Pilgrim.EasyWorship.Mqtt.Tests.Fixtures;
using Pilgrim.EasyWorship.Options;
using Pilgrim.EasyWorship.Transport;

namespace Pilgrim.EasyWorship.Mqtt.Tests;

public sealed class EndToEndTests
{
    [Test]
    public async Task Client_pairs_and_round_trips_status_with_real_tcp_server()
    {
        await using FakeEasyWorshipServer server = new();
        server.Start();

        EasyWorshipClientOptions options = new()
        {
            Host = "127.0.0.1",
            Port = server.Endpoint.Port,
            Uid = Guid.NewGuid().ToString(),
            HeartbeatIntervalSeconds = 60,
            PairingTimeoutSeconds = 30,
            ConnectTimeoutSeconds = 5,
        };
        await using EasyWorshipClient client = new(
            new OptionsWrapper<EasyWorshipClientOptions>(options),
            null);

        TaskCompletionSource<bool> pairedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PairingChanged += (_, ev) => pairedTcs.TrySetResult(ev.Paired);
        TaskCompletionSource<EwStatusEvent> statusTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusReceived += (_, ev) => statusTcs.TrySetResult(ev);

        await client.StartAsync();
        await server.ClientConnected.WaitAsync(TimeSpan.FromSeconds(5));
        string? connectFrame = await server.WaitForFrameAsync("\"action\":\"connect\"", TimeSpan.FromSeconds(5));
        await Assert.That(connectFrame).IsNotNull();

        await server.SendAsync("""{"action":"paired","requestrev":"1"}""");
        bool paired = await pairedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(paired).IsTrue();
        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);

        await server.SendAsync("""{"action":"status","logo":1,"black":0,"clear":0,"slide_no":4,"pres_no":2,"requestrev":"7"}""");
        EwStatusEvent status = await statusTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(status.Logo).IsTrue();
        await Assert.That(status.SlideNo).IsEqualTo(4);
        await Assert.That(status.PresNo).IsEqualTo(2);
        await Assert.That(status.RequestRev).IsEqualTo(7);

        await client.SendNextSlideAsync();
        string? nextFrame = await server.WaitForFrameAsync("\"action\":\"nextSlide\"", TimeSpan.FromSeconds(5));
        await Assert.That(nextFrame).IsNotNull();
        await Assert.That(nextFrame!).Contains("\"requestrev\":\"7\"");

        await client.StopAsync();
    }

    [Test]
    public async Task Client_reconnects_when_server_drops_connection()
    {
        await using FakeEasyWorshipServer server = new();
        server.Start();

        EasyWorshipClientOptions options = new()
        {
            Host = "127.0.0.1",
            Port = server.Endpoint.Port,
            Uid = Guid.NewGuid().ToString(),
            HeartbeatIntervalSeconds = 60,
            PairingTimeoutSeconds = 30,
            ConnectTimeoutSeconds = 5,
            Reconnect = new ReconnectOptions
            {
                Phase1InitialMs = 50,
                Phase1Multiplier = 1.0,
                Phase1CapMs = 50,
                Phase1DurationSeconds = 600,
            },
        };
        await using EasyWorshipClient client = new(
            new OptionsWrapper<EasyWorshipClientOptions>(options),
            null);

        await client.StartAsync();
        await server.ClientConnected.WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendAsync("""{"action":"paired"}""");
        for (int i = 0; i < 50 && client.State != ConnectionState.Connected; i++)
        {
            await Task.Delay(20);
        }

        await Assert.That(client.State).IsEqualTo(ConnectionState.Connected);

        // Server dies; client should detect and exit Connected state.
        await server.DisposeAsync();

        for (int i = 0; i < 100 && client.State == ConnectionState.Connected; i++)
        {
            await Task.Delay(50);
        }
        await Assert.That(client.State).IsNotEqualTo(ConnectionState.Connected);

        await client.StopAsync();
    }
}
