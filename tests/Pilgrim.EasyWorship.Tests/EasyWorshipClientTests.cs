using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Options;
using Pilgrim.EasyWorship;
using Pilgrim.EasyWorship.Events;
using Pilgrim.EasyWorship.Options;
using Pilgrim.EasyWorship.Transport;
using Pilgrim.EasyWorship.Wire;

namespace Pilgrim.EasyWorship.Tests;

public sealed class EasyWorshipClientTests
{
    [Test]
    public async Task Pairing_handshake_transitions_to_Connected_and_fires_event()
    {
        await using ClientFixture fixture = new();

        TaskCompletionSource<bool> pairedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.PairingChanged += (_, ev) => pairedTcs.TrySetResult(ev.Paired);

        await fixture.Client.StartAsync();

        await fixture.Transport.WaitForConnectAsync();
        string connectFrame = await fixture.Transport.ReadFrameAsync();
        await Assert.That(connectFrame).Contains("\"action\":\"connect\"");

        await fixture.Transport.PushAsync("""{"action":"paired"}""");

        bool paired = await pairedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(paired).IsTrue();
        await Assert.That(fixture.Client.State).IsEqualTo(ConnectionState.Connected);
    }

    [Test]
    public async Task Status_event_fires_with_parsed_fields()
    {
        await using ClientFixture fixture = new();
        TaskCompletionSource<EwStatusEvent> statusTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.StatusReceived += (_, ev) => statusTcs.TrySetResult(ev);

        await fixture.Client.StartAsync();
        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync();

        await fixture.Transport.PushAsync("""{"action":"paired"}""");
        await fixture.Transport.PushAsync("""{"action":"status","logo":1,"black":0,"clear":0,"slide_no":3,"pres_no":1,"requestrev":"42"}""");

        EwStatusEvent status = await statusTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(status.Logo).IsTrue();
        await Assert.That(status.SlideNo).IsEqualTo(3);
        await Assert.That(status.PresNo).IsEqualTo(1);
        await Assert.That(status.RequestRev).IsEqualTo(42);
        await Assert.That(fixture.Client.LastRequestRev).IsEqualTo(42);
    }

    [Test]
    public async Task SendNextSlide_writes_action_command_with_current_request_rev()
    {
        await using ClientFixture fixture = new();
        await fixture.Client.StartAsync();

        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync(); // consume connect
        await fixture.Transport.PushAsync("""{"action":"paired"}""");
        await fixture.Transport.PushAsync("""{"action":"status","logo":0,"black":0,"clear":0,"requestrev":"7"}""");

        // Wait for status to be processed (reflected in LastRequestRev).
        for (int i = 0; i < 50 && fixture.Client.LastRequestRev != 7; i++)
        {
            await Task.Delay(10);
        }
        await Assert.That(fixture.Client.LastRequestRev).IsEqualTo(7);

        await fixture.Client.SendNextSlideAsync();

        string frame = await fixture.Transport.ReadFrameContainingAsync("\"action\":\"nextSlide\"");
        await Assert.That(frame).Contains("\"requestrev\":\"7\"");
    }

    [Test]
    public async Task SendGotoSlide_serializes_slide_and_action()
    {
        await using ClientFixture fixture = new();
        await fixture.Client.StartAsync();

        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync();
        await fixture.Transport.PushAsync("""{"action":"paired"}""");

        await fixture.Client.SendGotoSlideAsync(5);

        string frame = await fixture.Transport.ReadFrameContainingAsync("\"action\":\"gotoSlide\"");
        await Assert.That(frame).Contains("\"slide\":5");
    }

    [Test]
    public async Task LiveData_after_paired_is_parsed_and_triggers_slideInfo_requests()
    {
        await using ClientFixture fixture = new();
        TaskCompletionSource<EwLiveDataEvent> liveDataTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.LiveDataReceived += (_, ev) => liveDataTcs.TrySetResult(ev);
        TaskCompletionSource<EwSlideInfoEvent> slideInfoTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.SlideInfoReceived += (_, ev) => slideInfoTcs.TrySetResult(ev);

        await fixture.Client.StartAsync();
        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync(); // connect
        await fixture.Transport.PushAsync("""{"action":"paired"}""");

        // Client should auto-send GetLiveData after pairing.
        await fixture.Transport.ReadFrameContainingAsync("\"action\":\"GetLiveData\"");

        // Server replies with LiveData (JSON header) + binary payload.
        byte[] bin = BuildLiveData(liveRev: 4188675586953317377L, presRowId: 42L, titleRevision: 7,
            slides: new (long, long)[] { (-100165, 1), (-100164, 2) });
        await fixture.Transport.PushBinaryFrameAsync($$"""{"action":"LiveData","size":{{bin.Length}},"requestrev":"1"}""", bin);

        EwLiveDataEvent live = await liveDataTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(live.Slides.Count).IsEqualTo(2);
        await Assert.That(live.Slides[0].SlideRowId).IsEqualTo(-100165L);

        // Client should now request slideInfo for each slide.
        string slideInfoReq = await fixture.Transport.ReadFrameContainingAsync("\"slide_rowid\":-100165");
        await Assert.That(slideInfoReq).Contains("\"action\":\"getSlideInfo\"");

        // Server responds with slideInfo (pure JSON, no binary).
        await fixture.Transport.PushAsync("""{"action":"slideInfo","slide_rowid":-100165,"title":"Verse 1","content":"Amazing grace, how sweet"}""");

        EwSlideInfoEvent info = await slideInfoTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(info.SlideRowId).IsEqualTo(-100165L);
        await Assert.That(info.Title).IsEqualTo("Verse 1");
        await Assert.That(info.Content).IsEqualTo("Amazing grace, how sweet");

        bool hasInfo = fixture.Client.TryGetSlideInfo(-100165, out EwSlideInfoEvent? cached);
        await Assert.That(hasInfo).IsTrue();
        await Assert.That(cached.Content).IsEqualTo("Amazing grace, how sweet");
    }

    private static byte[] BuildLiveData(long liveRev, long presRowId, long titleRevision, (long Rowid, long Rev)[] slides)
    {
        byte[] bytes = new byte[40 + slides.Length * 16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4, 8), liveRev);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12, 8), presRowId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(20, 8), titleRevision);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), slides.Length);
        for (int i = 0; i < slides.Length; i++)
        {
            int off = 40 + i * 16;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(off, 8), slides[i].Rowid);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(off + 8, 8), slides[i].Rev);
        }
        return bytes;
    }

    [Test]
    public async Task Status_arriving_before_LiveData_re_requests_with_correct_revision_when_LiveData_lands()
    {
        // Regression: EW silently drops getSlideInfo with revision=0. When status arrives
        // before LiveData, we don't know the slide's revision yet and our first request
        // uses 0. Once LiveData arrives, we MUST allow a re-request with the proper revision.
        await using ClientFixture fixture = new();
        await fixture.Client.StartAsync();
        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync(); // connect

        await fixture.Transport.PushAsync("""{"action":"paired"}""");
        await fixture.Transport.ReadFrameContainingAsync("\"action\":\"GetLiveData\"");

        // Status comes BEFORE LiveData and references slide 14 with no available revision.
        await fixture.Transport.PushAsync("""{"action":"status","logo":false,"black":false,"clear":false,"rectype":0,"pres_rowid":"4","slide_rowid":"14","requestrev":"0"}""");

        // Bridge sends a getSlideInfo request immediately with revision=0 (no LiveData yet).
        string firstReq = await fixture.Transport.ReadFrameContainingAsync("\"slide_rowid\":14");
        await Assert.That(firstReq).Contains("\"revision\":0");

        // Now LiveData arrives, putting slide 14 at index 2 with revision=7.
        byte[] bin = BuildLiveData(liveRev: 100, presRowId: 4, titleRevision: 1,
            slides: new (long, long)[] { (13, 1), (14, 7), (15, 1) });
        await fixture.Transport.PushBinaryFrameAsync($$"""{"action":"LiveData","size":{{bin.Length}},"requestrev":"0"}""", bin);

        // After LiveData lands, the bridge iterates Slides in order and re-requests anything
        // uncached. On-wire order: 13 (first request), 14 (re-request with correct rev=7), 15.
        await fixture.Transport.ReadFrameContainingAsync("\"slide_rowid\":13,\"revision\":1");
        string retry = await fixture.Transport.ReadFrameContainingAsync("\"slide_rowid\":14,\"revision\":7");
        await Assert.That(retry).Contains("\"action\":\"getSlideInfo\"");
        await fixture.Transport.ReadFrameContainingAsync("\"slide_rowid\":15,\"revision\":1");
    }

    [Test]
    public async Task Song_change_re_fetches_the_title_even_though_slide_rowid_0_is_already_cached()
    {
        // Regression: slide_rowid=0 is a per-presentation "title slot" — each song
        // reuses the same id with a different title. The cache must be invalidated
        // on every LiveData arrival so the title gets re-fetched on song change.
        await using ClientFixture fixture = new();
        await fixture.Client.StartAsync();
        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync(); // connect
        await fixture.Transport.PushAsync("""{"action":"paired"}""");
        await fixture.Transport.ReadFrameContainingAsync("\"action\":\"GetLiveData\"");

        // Song 1: status + LiveData
        await fixture.Transport.PushAsync("""{"action":"status","logo":false,"black":false,"clear":false,"rectype":1,"pres_rowid":"0","slide_rowid":"-100136","requestrev":"0"}""");
        byte[] bin1 = BuildLiveData(liveRev: 500, presRowId: 0, titleRevision: 1, slides: new (long, long)[] { (-100136, 1) });
        await fixture.Transport.PushBinaryFrameAsync($$"""{"action":"LiveData","size":{{bin1.Length}},"requestrev":"0"}""", bin1);

        // First title fetch goes out, EW responds with song 1's title.
        await fixture.Transport.ReadFrameContainingAsync("\"slide_rowid\":0");
        await fixture.Transport.PushAsync("""{"action":"slideInfo","slide_rowid":"0","title":"Amazing Grace","content":""}""");

        // Wait for cache to populate.
        for (int i = 0; i < 50 && !fixture.Client.TryGetSlideInfo(0, out _); i++) await Task.Delay(10);
        await Assert.That(fixture.Client.TryGetSlideInfo(0, out EwSlideInfoEvent? first)).IsTrue();
        await Assert.That(first.Title).IsEqualTo("Amazing Grace");

        // Song 2: status + new LiveData with a different liverev.
        await fixture.Transport.PushAsync("""{"action":"status","logo":false,"black":false,"clear":false,"rectype":1,"pres_rowid":"0","slide_rowid":"-100200","liverev":"501","requestrev":"0"}""");
        await fixture.Transport.ReadFrameContainingAsync("\"action\":\"GetLiveData\"");
        byte[] bin2 = BuildLiveData(liveRev: 501, presRowId: 0, titleRevision: 2, slides: new (long, long)[] { (-100200, 1) });
        await fixture.Transport.PushBinaryFrameAsync($$"""{"action":"LiveData","size":{{bin2.Length}},"requestrev":"0"}""", bin2);

        // Cache must have been invalidated — a NEW title fetch goes out.
        await fixture.Transport.ReadFrameContainingAsync("\"slide_rowid\":0");
        await fixture.Transport.PushAsync("""{"action":"slideInfo","slide_rowid":"0","title":"Away In A Manger","content":""}""");

        // Wait until the new title is cached (the cache may transiently be empty between
        // invalidation and the new slideInfo arrival, so check the title rather than presence).
        for (int i = 0; i < 100; i++)
        {
            if (fixture.Client.TryGetSlideInfo(0, out EwSlideInfoEvent? info) && info.Title == "Away In A Manger") break;
            await Task.Delay(10);
        }
        await Assert.That(fixture.Client.TryGetSlideInfo(0, out EwSlideInfoEvent? second)).IsTrue();
        await Assert.That(second.Title).IsEqualTo("Away In A Manger");
    }

    [Test]
    public async Task LiveData_triggers_song_title_fetch_with_correct_shape()
    {
        // Captured from the official EW Remote mobile app's traffic: the song title fetch
        // is getSlideInfo with slide_rowid=0, revision=1 (NOT title_revision), pres_rowid
        // from the current presentation, and rectype matching the last status frame's rectype.
        await using ClientFixture fixture = new();
        await fixture.Client.StartAsync();
        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync(); // connect
        await fixture.Transport.PushAsync("""{"action":"paired"}""");
        await fixture.Transport.ReadFrameContainingAsync("\"action\":\"GetLiveData\"");

        // Status arrives with rectype=1 (library-direct convention) before LiveData.
        await fixture.Transport.PushAsync("""{"action":"status","logo":false,"black":false,"clear":false,"rectype":1,"pres_rowid":"0","slide_rowid":"-100136","requestrev":"0"}""");

        byte[] bin = BuildLiveData(liveRev: 500, presRowId: 0, titleRevision: 13,
            slides: new (long, long)[] { (-100136, 1), (-100137, 1) });
        await fixture.Transport.PushBinaryFrameAsync($$"""{"action":"LiveData","size":{{bin.Length}},"requestrev":"0"}""", bin);

        // After per-slide requests, expect the title fetch with the captured shape.
        string titleReq = await fixture.Transport.ReadFrameContainingAsync("\"slide_rowid\":0");
        await Assert.That(titleReq).Contains("\"action\":\"getSlideInfo\"");
        await Assert.That(titleReq).Contains("\"revision\":1");      // NOT title_revision
        await Assert.That(titleReq).Contains("\"pres_rowid\":0");    // matches status
        await Assert.That(titleReq).Contains("\"rectype\":1");       // matches status's rectype
    }

    [Test]
    public async Task PresentationLoaded_fires_once_when_every_slide_in_LiveData_has_slideInfo()
    {
        await using ClientFixture fixture = new();
        List<EwPresentationLoadedEvent> loadedEvents = new();
        fixture.Client.PresentationLoaded += (_, ev) => loadedEvents.Add(ev);

        await fixture.Client.StartAsync();
        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync();
        await fixture.Transport.PushAsync("""{"action":"paired"}""");
        await fixture.Transport.ReadFrameContainingAsync("\"action\":\"GetLiveData\"");

        byte[] bin = BuildLiveData(liveRev: 7777L, presRowId: 42L, titleRevision: 1,
            slides: new (long, long)[] { (10, 1), (11, 1), (12, 1) });
        await fixture.Transport.PushBinaryFrameAsync($$"""{"action":"LiveData","size":{{bin.Length}},"requestrev":"1"}""", bin);

        await fixture.Transport.ReadFrameContainingAsync("\"slide_rowid\":10");
        await fixture.Transport.PushAsync("""{"action":"slideInfo","slide_rowid":10,"title":"V1","content":"a"}""");
        await Task.Delay(50);
        await Assert.That(loadedEvents.Count).IsEqualTo(0).Because("only 1/3 slideInfo received");

        await fixture.Transport.PushAsync("""{"action":"slideInfo","slide_rowid":11,"title":"V2","content":"b"}""");
        await Task.Delay(50);
        await Assert.That(loadedEvents.Count).IsEqualTo(0).Because("only 2/3 slideInfo received");

        await fixture.Transport.PushAsync("""{"action":"slideInfo","slide_rowid":12,"title":"V3","content":"c"}""");

        // The third slideInfo should fire PresentationLoaded.
        for (int i = 0; i < 50 && loadedEvents.Count == 0; i++)
        {
            await Task.Delay(20);
        }
        await Assert.That(loadedEvents.Count).IsEqualTo(1);
        await Assert.That(loadedEvents[0].PresRowId).IsEqualTo(42L);
        await Assert.That(loadedEvents[0].LiveRev).IsEqualTo(7777L);
        await Assert.That(loadedEvents[0].SlideCount).IsEqualTo(3);

        // A duplicate slideInfo for the same liverev must not refire.
        await fixture.Transport.PushAsync("""{"action":"slideInfo","slide_rowid":10,"title":"V1","content":"a"}""");
        await Task.Delay(50);
        await Assert.That(loadedEvents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SendActivateSlide_emits_status_with_target_pres_and_slide_rowid()
    {
        // Captured from the mobile app: activating a schedule slide is a `status` write
        // with pres_rowid + slide_rowid set to the TARGET (overriding the current live
        // state), and other fields mirrored from the last received status.
        await using ClientFixture fixture = new();
        await fixture.Client.StartAsync();
        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync(); // connect
        await fixture.Transport.PushAsync("""{"action":"paired"}""");
        await fixture.Transport.ReadFrameContainingAsync("\"action\":\"GetLiveData\"");

        // Current state: pres_rowid=3, slide_rowid=8 (per the dump).
        await fixture.Transport.PushAsync("""{"action":"status","logo":false,"black":false,"clear":false,"rectype":0,"pres_rowid":"3","slide_rowid":"8","pres_no":"0","slide_no":"0","schedulerev":"4188693812178783235","liverev":"4188693813473184771","imagehash":"e70aca2372ff8ec22f31667e449b2f1c641c2710","permissions":1,"requestrev":"1"}""");
        for (int i = 0; i < 50 && fixture.Client.LastRequestRev != 1; i++) await Task.Delay(10);
        await Assert.That(fixture.Client.LastRequestRev).IsEqualTo(1L);

        // Activate slide 13 of pres 4.
        await fixture.Client.SendActivateSlideAsync(presRowId: 4, slideRowId: 13);

        string frame = await fixture.Transport.ReadFrameContainingAsync("\"slide_rowid\":13");
        await Assert.That(frame).Contains("\"action\":\"status\"");
        await Assert.That(frame).Contains("\"pres_rowid\":4");
        await Assert.That(frame).Contains("\"slide_rowid\":13");
        await Assert.That(frame).Contains("\"rectype\":0");
        await Assert.That(frame).Contains("\"schedulerev\":\"4188693812178783235\"");
        await Assert.That(frame).Contains("\"liverev\":\"4188693813473184771\"");
        await Assert.That(frame).Contains("\"imagehash\":\"e70aca2372ff8ec22f31667e449b2f1c641c2710\"");
        // No overlay changes on activation.
        await Assert.That(frame).Contains("\"logo\":false");
        await Assert.That(frame).Contains("\"black\":false");
        await Assert.That(frame).Contains("\"clear\":false");
    }

    [Test]
    public async Task SendStatus_mirrors_last_received_status_fields()
    {
        // EW silently ignores status commands that don't echo all the mirrored fields
        // from the last incoming status. Verify the outgoing JSON includes them all.
        await using ClientFixture fixture = new();
        await fixture.Client.StartAsync();
        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync();
        await fixture.Transport.PushAsync("""{"action":"paired"}""");
        await fixture.Transport.ReadFrameContainingAsync("\"action\":\"GetLiveData\"");

        await fixture.Transport.PushAsync("""{"action":"status","logo":false,"black":false,"clear":false,"rectype":1,"pres_rowid":"4","slide_rowid":"-101494","pres_no":"0","slide_no":"0","schedulerev":"4188675676225997825","liverev":"4188676741401021441","imagehash":"abc123","permissions":0,"requestrev":"42"}""");

        // Wait for the status to be processed.
        for (int i = 0; i < 50 && fixture.Client.LastRequestRev != 42; i++) await Task.Delay(10);
        await Assert.That(fixture.Client.LastRequestRev).IsEqualTo(42L);

        await fixture.Client.SendStatusAsync(new StatusOverlay(Logo: false, Black: false, Clear: true));

        string frame = await fixture.Transport.ReadFrameContainingAsync("\"action\":\"status\"");
        await Assert.That(frame).Contains("\"logo\":false");
        await Assert.That(frame).Contains("\"black\":false");
        await Assert.That(frame).Contains("\"clear\":true");
        await Assert.That(frame).Contains("\"rectype\":1");
        // rowid/no fields echoed as JSON numbers (incoming were strings; we parse to long
        // and re-emit as numbers, which matches Companion's serializer output for any
        // value it was able to coerce to a number).
        await Assert.That(frame).Contains("\"pres_rowid\":4");
        await Assert.That(frame).Contains("\"slide_rowid\":-101494");
        await Assert.That(frame).Contains("\"pres_no\":0");
        await Assert.That(frame).Contains("\"slide_no\":0");
        await Assert.That(frame).Contains("\"schedulerev\":\"4188675676225997825\"");
        await Assert.That(frame).Contains("\"liverev\":\"4188676741401021441\"");
        await Assert.That(frame).Contains("\"imagehash\":\"abc123\"");
        await Assert.That(frame).Contains("\"permissions\":0");
        await Assert.That(frame).Contains("\"requestrev\":\"42\"");
    }

    [Test]
    public async Task NotPaired_response_is_emitted_and_state_stays_Pairing()
    {
        await using ClientFixture fixture = new();
        TaskCompletionSource<bool> notPairedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.PairingChanged += (_, ev) =>
        {
            if (!ev.Paired)
            {
                notPairedTcs.TrySetResult(false);
            }
        };

        await fixture.Client.StartAsync();
        await fixture.Transport.WaitForConnectAsync();
        await fixture.Transport.ReadFrameAsync();

        await fixture.Transport.PushAsync("""{"action":"notPaired"}""");

        await notPairedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(fixture.Client.State).IsNotEqualTo(ConnectionState.Connected);
    }
}

internal sealed class ClientFixture : IAsyncDisposable
{
    public FakeTransport Transport { get; }
    public FakeTransportFactory Factory { get; }
    public EasyWorshipClient Client { get; }

    public ClientFixture()
    {
        Transport = new FakeTransport();
        Factory = new FakeTransportFactory(Transport);
        EasyWorshipClientOptions options = new()
        {
            Host = "fake",
            Port = 1234,
            Uid = "00000000-0000-0000-0000-000000000001",
            HeartbeatIntervalSeconds = 60,
            PairingTimeoutSeconds = 30,
            ConnectTimeoutSeconds = 5,
            Reconnect = new ReconnectOptions { Phase1InitialMs = 50, Phase1CapMs = 100, Phase1DurationSeconds = 5 },
        };
        Client = new EasyWorshipClient(
            new OptionsWrapper<EasyWorshipClientOptions>(options),
            Factory,
            TimeProvider.System,
            null);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
    }
}

internal sealed class FakeTransportFactory : IEzwTransportFactory
{
    private readonly FakeTransport _transport;
    public FakeTransportFactory(FakeTransport transport) => _transport = transport;
    public IEzwTransport Create() => _transport;
}

internal sealed class FakeTransport : IEzwTransport
{
    private readonly Pipe _toClient = new();
    private readonly Pipe _fromClient = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsConnected { get; private set; }
    public PipeReader Reader => _toClient.Reader;
    public PipeWriter Writer => _fromClient.Writer;

    public Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        IsConnected = true;
        _connected.TrySetResult();
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        await _toClient.Writer.CompleteAsync();
        await _fromClient.Writer.CompleteAsync();
        await _toClient.Reader.CompleteAsync();
        await _fromClient.Reader.CompleteAsync();
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    public Task WaitForConnectAsync() => _connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public async Task PushAsync(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\r\n");
        await _toClient.Writer.WriteAsync(bytes);
        await _toClient.Writer.FlushAsync();
    }

    public async Task PushBinaryFrameAsync(string json, byte[] payload)
    {
        byte[] headerBytes = Encoding.UTF8.GetBytes(json + "\r\n");
        await _toClient.Writer.WriteAsync(headerBytes);
        await _toClient.Writer.WriteAsync(payload);
        await _toClient.Writer.FlushAsync();
    }

    public async Task<string> ReadFrameContainingAsync(string substring, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(2);
        DateTime deadline = DateTime.UtcNow + timeout.Value;
        while (DateTime.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            string frame = await ReadFrameAsync(remaining);
            if (frame.Contains(substring, StringComparison.Ordinal))
            {
                return frame;
            }
        }
        throw new TimeoutException($"no frame containing '{substring}' within timeout");
    }

    public async Task<string> ReadFrameAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(2);
        using CancellationTokenSource cts = new(timeout.Value);
        while (!cts.Token.IsCancellationRequested)
        {
            ReadResult read = await _fromClient.Reader.ReadAsync(cts.Token);
            ReadOnlySequence<byte> buffer = read.Buffer;
            if (EzwLineFramer.TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
            {
                string text = Encoding.UTF8.GetString(line.ToArray());
                // Advance consumed AND examined to buffer.Start: tell the pipe we've consumed
                // up to here AND haven't examined past it, so any remaining buffered data
                // surfaces on the next ReadAsync immediately.
                _fromClient.Reader.AdvanceTo(buffer.Start);
                return text;
            }
            // No full frame yet: keep waiting for new data.
            _fromClient.Reader.AdvanceTo(buffer.Start, read.Buffer.End);
            if (read.IsCompleted)
            {
                throw new EndOfStreamException();
            }
        }
        throw new TimeoutException("no frame read within timeout");
    }
}
