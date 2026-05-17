using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pilgrim.EasyWorship.Events;
using Pilgrim.EasyWorship.Options;
using Pilgrim.EasyWorship.State;
using Pilgrim.EasyWorship.Transport;
using Pilgrim.EasyWorship.Wire;

namespace Pilgrim.EasyWorship;

public sealed class EasyWorshipClient : IEasyWorshipClient
{
    private readonly EasyWorshipClientOptions _options;
    private readonly IEzwTransportFactory _transportFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EasyWorshipClient> _logger;
    private readonly Channel<string> _sendChannel;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private long _lastRequestRev;
    private int _state;
    private EwLiveDataEvent? _currentLiveData;
    private long _lastLiveDataLiveRev;
    private long _lastFetchedSlideRowId;
    private long _presentationLoadedFiredForLiveRev;
    private StatusSnapshot? _lastStatus;
    private readonly ConcurrentDictionary<long, EwSlideInfoEvent> _slideInfoCache = new();
    private readonly HashSet<long> _slideInfoRequested = new();
    private readonly object _slideInfoRequestLock = new();
    private IReadOnlyList<EwScheduleEntry>? _currentSchedule;
    private readonly ConcurrentDictionary<long, string> _scheduleTitleCache = new();
    private readonly HashSet<long> _scheduleTitleRequested = new();
    private readonly object _scheduleTitleLock = new();

    private sealed record StatusSnapshot(
        int RecType,
        long PresRowId,
        long SlideRowId,
        int PresNo,
        int SlideNo,
        long ScheduleRev,
        long LiveRev,
        string ImageHash,
        int Permissions);

    public EasyWorshipClient(
        IOptions<EasyWorshipClientOptions> options,
        ILogger<EasyWorshipClient>? logger = null)
        : this(options, new TcpEzwTransportFactory(), TimeProvider.System, logger)
    {
    }

    internal EasyWorshipClient(
        IOptions<EasyWorshipClientOptions> options,
        IEzwTransportFactory transportFactory,
        TimeProvider? timeProvider,
        ILogger<EasyWorshipClient>? logger)
    {
        _options = options.Value;
        _transportFactory = transportFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<EasyWorshipClient>.Instance;
        _sendChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);
    public long LastRequestRev => Interlocked.Read(ref _lastRequestRev);
    public EwLiveDataEvent? CurrentLiveData => _currentLiveData;

    public bool TryGetSlideInfo(long slideRowId, out EwSlideInfoEvent slideInfo)
    {
        if (_slideInfoCache.TryGetValue(slideRowId, out EwSlideInfoEvent? cached))
        {
            slideInfo = cached;
            return true;
        }
        slideInfo = null!;
        return false;
    }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<EwStatusEvent>? StatusReceived;
    public event EventHandler<EwPairingEvent>? PairingChanged;
    public event EventHandler<EwHeartbeatEvent>? HeartbeatReceived;
    public event EventHandler<EwUnknownEvent>? UnknownEventReceived;
    public event EventHandler<EwLiveDataEvent>? LiveDataReceived;
    public event EventHandler<EwSlideInfoEvent>? SlideInfoReceived;
    public event EventHandler<EwPresentationLoadedEvent>? PresentationLoaded;
    public event EventHandler<EwScheduleDataEvent>? ScheduleDataReceived;

    public IReadOnlyList<EwScheduleEntry>? CurrentSchedule => _currentSchedule;

    public bool TryGetScheduleTitle(long presRowId, out string title)
    {
        if (_scheduleTitleCache.TryGetValue(presRowId, out string? t))
        {
            title = t;
            return true;
        }
        title = string.Empty;
        return false;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_runTask is not null)
        {
            throw new InvalidOperationException("client already started");
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts = _cts;
        Task? runTask = _runTask;
        if (cts is null)
        {
            return;
        }
        _cts = null;
        _runTask = null;

        await cts.CancelAsync().ConfigureAwait(false);
        if (runTask is not null)
        {
            try
            {
                await runTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The caller stopped waiting, but RunAsync may still observe the
                // token. Defer disposal until it has actually exited so it never
                // touches a disposed CancellationTokenSource.
                _ = runTask.ContinueWith(static (_, s) => ((CancellationTokenSource)s!).Dispose(),
                    cts, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                SetState(ConnectionState.Disconnected);
                return;
            }
        }
        cts.Dispose();
        SetState(ConnectionState.Disconnected);
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    public Task SendNextSlideAsync(CancellationToken ct = default) => EnqueueAction("nextSlide", ct);
    public Task SendPrevSlideAsync(CancellationToken ct = default) => EnqueueAction("prevSlide", ct);
    public Task SendGotoStartSlideAsync(CancellationToken ct = default) => EnqueueAction("gotoStartSlide", ct);
    public Task SendGotoStartPresentationAsync(CancellationToken ct = default) => EnqueueAction("gotoStartPresentation", ct);
    public Task SendNextScheduleAsync(CancellationToken ct = default) => EnqueueAction("nextSchedule", ct);
    public Task SendPrevScheduleAsync(CancellationToken ct = default) => EnqueueAction("prevSchedule", ct);
    public Task SendNextBuildAsync(CancellationToken ct = default) => EnqueueAction("nextBuild", ct);
    public Task SendPrevBuildAsync(CancellationToken ct = default) => EnqueueAction("prevBuild", ct);
    public Task SendPlayAsync(CancellationToken ct = default) => EnqueueAction("Play", ct);
    public Task SendPauseAsync(CancellationToken ct = default) => EnqueueAction("Pause", ct);
    public Task SendToggleAsync(CancellationToken ct = default) => EnqueueAction("Toggle", ct);

    public Task SendGotoSlideAsync(int slideNumber, CancellationToken ct = default)
    {
        EzwGotoSlideCommand cmd = new()
        {
            Slide = slideNumber,
            RequestRev = CurrentRequestRevString(),
        };
        return Enqueue(JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwGotoSlideCommand), ct);
    }

    public Task SendGotoScheduleAsync(int scheduleNumber, CancellationToken ct = default)
    {
        EzwGotoScheduleCommand cmd = new()
        {
            Schedule = scheduleNumber,
            RequestRev = CurrentRequestRevString(),
        };
        return Enqueue(JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwGotoScheduleCommand), ct);
    }

    public Task SendActivateSlideAsync(long presRowId, long slideRowId, CancellationToken ct = default)
    {
        // EW's "go live with this slide of this schedule item" command. Reverse-engineered
        // from the mobile app: same `status` action as overlay control, but with explicit
        // pres_rowid + slide_rowid that override the live state. Other fields are mirrored
        // from the last received status so EW accepts the command.
        StatusSnapshot? last = _lastStatus;
        EzwStatusCommand cmd = new()
        {
            Logo = false,
            Black = false,
            Clear = false,
            RecType = last?.RecType ?? 0,
            PresRowId = presRowId,
            SlideRowId = slideRowId,
            PresNo = 0,
            SlideNo = 0,
            ScheduleRev = (last?.ScheduleRev ?? 0).ToString(CultureInfo.InvariantCulture),
            LiveRev = (last?.LiveRev ?? 0).ToString(CultureInfo.InvariantCulture),
            ImageHash = last?.ImageHash ?? string.Empty,
            Permissions = last?.Permissions ?? 0,
            RequestRev = CurrentRequestRevString(),
        };
        return Enqueue(JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwStatusCommand), ct);
    }

    public Task SendStatusAsync(StatusOverlay overlay, CancellationToken ct = default)
    {
        // EW silently ignores `status` commands that don't match the format Companion sends.
        // That format echoes rectype/schedulerev/liverev/imagehash/permissions/requestrev from
        // the last received status, with logo/black/clear as JSON booleans on top.
        // pres_rowid/slide_rowid/pres_no/slide_no are NOT echoed (Companion's typeof guard).
        StatusSnapshot? last = _lastStatus;
        EzwStatusCommand cmd = new()
        {
            Logo = overlay.Logo,
            Black = overlay.Black,
            Clear = overlay.Clear,
            RecType = last?.RecType ?? 0,
            PresRowId = last?.PresRowId ?? 0,
            SlideRowId = last?.SlideRowId ?? 0,
            PresNo = last?.PresNo ?? 0,
            SlideNo = last?.SlideNo ?? 0,
            ScheduleRev = (last?.ScheduleRev ?? 0).ToString(CultureInfo.InvariantCulture),
            LiveRev = (last?.LiveRev ?? 0).ToString(CultureInfo.InvariantCulture),
            ImageHash = last?.ImageHash ?? string.Empty,
            Permissions = last?.Permissions ?? 0,
            RequestRev = CurrentRequestRevString(),
        };
        return Enqueue(JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwStatusCommand), ct);
    }

    private Task EnqueueAction(string action, CancellationToken ct)
    {
        EzwActionCommand cmd = new()
        {
            Action = action,
            RequestRev = CurrentRequestRevString(),
        };
        return Enqueue(JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwActionCommand), ct);
    }

    private async Task Enqueue(string json, CancellationToken ct)
    {
        await _sendChannel.Writer.WriteAsync(json, ct).ConfigureAwait(false);
    }

    private string CurrentRequestRevString() =>
        LastRequestRev.ToString(CultureInfo.InvariantCulture);

    private async Task RunAsync(CancellationToken ct)
    {
        BackoffSchedule backoff = new(_options.Reconnect);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrEmpty(_options.Host))
                {
                    SetState(ConnectionState.Faulted);
                    _logger.LogError("EasyWorship host is not configured");
                }
                else
                {
                    await ConnectAndRunAsync(ct).ConfigureAwait(false);
                    backoff.Reset();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EasyWorship connection ended with error");
            }

            SetState(ConnectionState.Disconnected);
            if (ct.IsCancellationRequested)
            {
                break;
            }

            TimeSpan delay = backoff.NextDelay();
            _logger.LogInformation("Reconnecting in {Delay}", delay);
            try
            {
                await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetState(ConnectionState.Disconnected);
    }

    private async Task ConnectAndRunAsync(CancellationToken ct)
    {
        SetState(ConnectionState.Connecting);

        // Reset per-connection state (preserve slide_info cache across reconnects)
        _currentLiveData = null;
        _lastLiveDataLiveRev = 0;
        _lastFetchedSlideRowId = 0;
        _presentationLoadedFiredForLiveRev = 0;
        _currentSchedule = null;
        _scheduleTitleCache.Clear();
        lock (_slideInfoRequestLock) { _slideInfoRequested.Clear(); }
        lock (_scheduleTitleLock) { _scheduleTitleRequested.Clear(); }

        await using IEzwTransport transport = _transportFactory.Create();
        await transport.ConnectAsync(_options.Host!, _options.Port, _options.ConnectTimeout, ct).ConfigureAwait(false);

        SetState(ConnectionState.Pairing);

        Channel<EzwInboundFrame> inbound = Channel.CreateUnbounded<EzwInboundFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        using CancellationTokenSource loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        TaskCompletionSource<bool> firstPairingResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);

        EzwReadLoop readLoop = new(transport.Reader, inbound.Writer);
        Task readTask = Task.Run(() => readLoop.RunAsync(loopCts.Token), CancellationToken.None);
        Task dispatchTask = Task.Run(() => DispatchInboundAsync(inbound.Reader, firstPairingResponse, loopCts.Token), CancellationToken.None);
        Task sendTask = Task.Run(() => SendLoopAsync(transport.Writer, loopCts.Token), CancellationToken.None);
        Task heartbeatTask = Task.Run(() => HeartbeatLoopAsync(loopCts.Token), CancellationToken.None);

        try
        {
            await SendConnectAsync(transport.Writer, loopCts.Token).ConfigureAwait(false);

            using (CancellationTokenSource pairTimeoutCts = new(_options.PairingTimeout, _timeProvider))
            using (pairTimeoutCts.Token.Register(() => firstPairingResponse.TrySetException(new TimeoutException("pairing timed out"))))
            {
                bool paired = await firstPairingResponse.Task.ConfigureAwait(false);
                if (paired)
                {
                    SetState(ConnectionState.Connected);
                }
                else
                {
                    _logger.LogWarning("EasyWorship returned notPaired; awaiting operator approval");
                }
            }

            Task completed = await Task.WhenAny(readTask, dispatchTask, sendTask, heartbeatTask).ConfigureAwait(false);
            string which = completed == readTask ? "read"
                : completed == dispatchTask ? "dispatch"
                : completed == sendTask ? "send"
                : "heartbeat";
            if (completed.IsFaulted)
            {
                _logger.LogWarning(completed.Exception, "{Which} loop faulted", which);
                await completed.ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("{Which} loop ended (server likely closed connection)", which);
            }
        }
        finally
        {
            await loopCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(readTask, dispatchTask, sendTask, heartbeatTask)
                    .WaitAsync(TimeSpan.FromSeconds(5), _timeProvider)
                    .ConfigureAwait(false);
            }
            catch
            {
                /* ignore — we're tearing down */
            }
            await transport.DisconnectAsync().ConfigureAwait(false);
        }
    }

    private async Task SendConnectAsync(PipeWriter writer, CancellationToken ct)
    {
        EzwConnectCommand connect = new()
        {
            DeviceType = _options.DeviceType,
            Uid = _options.Uid,
            DeviceName = _options.DeviceName,
        };
        string json = JsonSerializer.Serialize(connect, EzwJsonContext.Default.EzwConnectCommand);
        await WriteFrameAsync(writer, json, ct).ConfigureAwait(false);
    }

    private async Task SendLoopAsync(PipeWriter writer, CancellationToken ct)
    {
        try
        {
            await foreach (string? json in _sendChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await WriteFrameAsync(writer, json, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.HeartbeatInterval, _timeProvider, ct).ConfigureAwait(false);
                EzwActionCommand cmd = new()
                {
                    Action = "heartbeat",
                    RequestRev = CurrentRequestRevString(),
                };
                string json = JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwActionCommand);
                await _sendChannel.Writer.WriteAsync(json, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task DispatchInboundAsync(
        ChannelReader<EzwInboundFrame> reader,
        TaskCompletionSource<bool> firstPairingResponse,
        CancellationToken ct)
    {
        try
        {
            await foreach (EzwInboundFrame frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Dispatch(frame, firstPairingResponse);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void Dispatch(EzwInboundFrame frame, TaskCompletionSource<bool> firstPairingResponse)
    {
        EzwInboundMessage msg = frame.Message;
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("EW frame: {Raw}", frame.Raw);
        }
        if (msg.RequestRev.HasValue)
        {
            Interlocked.Exchange(ref _lastRequestRev, msg.RequestRev.Value);
        }

        switch (msg.Action)
        {
            case "paired":
                SetState(ConnectionState.Connected);
                firstPairingResponse.TrySetResult(true);
                Raise(PairingChanged, new EwPairingEvent(true));
                _ = EnqueueGetLiveDataAsync();
                _ = EnqueueGetScheduleDataAsync();
                break;

            case "notPaired":
                firstPairingResponse.TrySetResult(false);
                Raise(PairingChanged, new EwPairingEvent(false));
                break;

            case "status":
                int? previousPermissions = _lastStatus?.Permissions;
                _lastStatus = new StatusSnapshot(
                    msg.RecType,
                    msg.PresRowId, msg.SlideRowId,
                    msg.PresNo, msg.SlideNo,
                    msg.ScheduleRev, msg.LiveRev,
                    msg.ImageHash ?? string.Empty,
                    msg.Permissions);
                if (previousPermissions != msg.Permissions)
                {
                    if (msg.Permissions == 0)
                    {
                        _logger.LogWarning(
                            "EasyWorship reports permissions=0 (view-only). Outgoing commands "
                            + "(overlays, navigation, play/pause) will be silently dropped until the "
                            + "user grants control in EW's Remote panel (click the lock icon next to "
                            + "this device).");
                    }
                    else
                    {
                        _logger.LogInformation("EasyWorship permissions = {Permissions} (control granted)", msg.Permissions);
                    }
                }
                EwStatusEvent status = new(
                    msg.Logo, msg.Black, msg.Clear,
                    msg.PresRowId, msg.SlideRowId,
                    msg.PresNo, msg.SlideNo,
                    msg.ScheduleRev, msg.LiveRev,
                    msg.ImageHash ?? string.Empty,
                    msg.RequestRev ?? 0);
                Raise(StatusReceived, status);
                OnStatusForFetch(status, msg.RecType);
                break;

            case "heartbeat":
                Raise(HeartbeatReceived, new EwHeartbeatEvent(msg.RequestRev ?? 0));
                break;

            case "LiveData":
                if (LiveDataParser.TryParse(frame.Payload, out LiveDataPayload? live))
                {
                    EwLiveDataEvent ev = new(
                        live.LiveRev, live.PresRowId, live.TitleRevision,
                        live.Slides.Select(s => new EwLiveDataSlide(s.SlideRowId, s.Revision)).ToArray());
                    _currentLiveData = ev;
                    _lastLiveDataLiveRev = live.LiveRev;
                    _logger.LogDebug("Parsed LiveData: liveRev={LiveRev} pres_rowid={PresRowId} slides={Count}",
                        live.LiveRev, live.PresRowId, live.Slides.Count);
                    Raise(LiveDataReceived, ev);

                    // Earlier on-demand requests (triggered by status before LiveData arrived)
                    // may have used revision=0 and been silently dropped by EW. Allow a re-request
                    // for any slide that isn't yet cached so it goes out with the proper revision.
                    lock (_slideInfoRequestLock)
                    {
                        foreach (LiveDataSlide slide in live.Slides)
                        {
                            if (!_slideInfoCache.ContainsKey(slide.SlideRowId))
                            {
                                _slideInfoRequested.Remove(slide.SlideRowId);
                            }
                        }
                        // slide_rowid=0 is a per-presentation "title slot" — every song
                        // reuses the same id with a different title. Force a re-fetch on
                        // every LiveData arrival by clearing both the cache and the pending
                        // request flag.
                        _slideInfoCache.TryRemove(0, out _);
                        _slideInfoRequested.Remove(0);
                    }

                    foreach (LiveDataSlide slide in live.Slides)
                    {
                        TryRequestSlideInfo(slide.SlideRowId, slide.Revision, live.PresRowId);
                    }
                    // Ask EW for the song-title metadata. Captured from the mobile EW
                    // Remote app's traffic: slide_rowid=0, revision=1 (NOT title_revision),
                    // pres_rowid matches status (0 for library-direct, real value for
                    // scheduled), rectype matches status's rectype (1 for library-direct,
                    // 0 for scheduled). OnSlideInfoAsync publishes the response to
                    // state/presentation/title automatically.
                    TryRequestSlideInfo(0, revision: 1, live.PresRowId, recType: _lastStatusRecType);
                    CheckPresentationLoaded();
                }
                else
                {
                    _logger.LogWarning("Could not parse LiveData payload ({Bytes} bytes)", frame.Payload.Length);
                }
                break;

            case "slideInfo":
                {
                    long slideRowId = msg.SlideRowId;
                    EwSlideInfoEvent info = new(
                        slideRowId,
                        msg.Title ?? string.Empty,
                        msg.Content ?? string.Empty);
                    _slideInfoCache[slideRowId] = info;
                    _logger.LogDebug("Parsed slideInfo: rowid={SlideRowId} pres_rowid={PresRowId} title={Title}",
                        slideRowId, msg.PresRowId, info.Title);
                    // slideInfo for slide_rowid=0 with rectype=0 and non-zero pres_rowid is
                    // a schedule item's title. Cache it under pres_rowid.
                    if (slideRowId == 0 && msg.PresRowId > 0)
                    {
                        _scheduleTitleCache[msg.PresRowId] = info.Title;
                    }
                    Raise(SlideInfoReceived, info);
                    CheckPresentationLoaded();
                }
                break;

            case null:
            case "":
                _logger.LogDebug("Received frame with no action: {Raw}", frame.Raw);
                break;

            case "_malformed":
                _logger.LogWarning("Could not parse EW frame: {Raw}", frame.Raw);
                Raise(UnknownEventReceived, new EwUnknownEvent("_malformed", frame.Raw, null));
                break;

            case "ScheduleData":
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("ScheduleData payload ({Bytes} bytes) hex: {Hex}",
                        frame.Payload.Length, Convert.ToHexString(frame.Payload));
                }
                if (ScheduleDataParser.TryParse(frame.Payload, out ScheduleDataPayload? sched))
                {
                    EwScheduleEntry[] entries = sched.Entries
                        .Select(e => new EwScheduleEntry(
                            e.PresRowId,
                            e.Revision,
                            e.Slides.Select(s => new EwScheduleSlide(s.SlideRowId, s.Revision)).ToArray()))
                        .ToArray();
                    _currentSchedule = entries;
                    _logger.LogDebug("Parsed ScheduleData: {Count} entries (header={Hdr}, item_size={Item})",
                        entries.Length, sched.HeaderSize, sched.ItemSize);
                    Raise(ScheduleDataReceived, new EwScheduleDataEvent(entries));
                    foreach (EwScheduleEntry? entry in entries)
                    {
                        TryRequestScheduleTitle(entry.PresRowId, entry.Revision);
                    }
                }
                else
                {
                    _logger.LogWarning("Could not parse ScheduleData payload ({Bytes} bytes); hex dumped above for analysis", frame.Payload.Length);
                }
                break;

            case "currentImage":
            case "slideImage":
                _logger.LogDebug("Received {Action} with {Bytes} payload bytes (not parsed)", msg.Action, frame.Payload.Length);
                break;

            default:
                _logger.LogDebug("Received unknown action {Action}: {Raw}", msg.Action, frame.Raw);
                Raise(UnknownEventReceived, new EwUnknownEvent(msg.Action!, frame.Raw, msg.RequestRev));
                break;
        }
    }

    private int _lastStatusRecType = 1;

    private void OnStatusForFetch(EwStatusEvent status, int recType)
    {
        _lastStatusRecType = recType;

        // Refresh LiveData when EW signals a new revision.
        if (_lastLiveDataLiveRev != 0 && status.LiveRev != 0 && status.LiveRev != _lastLiveDataLiveRev)
        {
            _ = EnqueueGetLiveDataAsync();
        }

        if (status.SlideRowId != 0 && status.SlideRowId != _lastFetchedSlideRowId)
        {
            _lastFetchedSlideRowId = status.SlideRowId;
            // Look up revision in current LiveData; if absent, fall back to 0.
            long revision = 0;
            long presRowId = status.PresRowId;
            bool foundInLiveData = false;
            if (_currentLiveData is { Slides: var slides })
            {
                foreach (EwLiveDataSlide s in slides)
                {
                    if (s.SlideRowId == status.SlideRowId)
                    {
                        revision = s.Revision;
                        foundInLiveData = true;
                        break;
                    }
                }
                if (presRowId == 0) presRowId = _currentLiveData.PresRowId;
            }
            // If the current slide isn't in LiveData (e.g. a title/section slide that
            // lives outside the verse list), match status's rectype on the request.
            // LiveData slides use the default rectype=1.
            int recTypeForRequest = foundInLiveData ? 1 : recType;
            TryRequestSlideInfo(status.SlideRowId, revision, presRowId, recTypeForRequest);
        }
    }

    private void TryRequestSlideInfo(long slideRowId, long revision, long presRowId, int recType = 1)
    {
        if (_slideInfoCache.ContainsKey(slideRowId))
        {
            return;
        }
        lock (_slideInfoRequestLock)
        {
            if (!_slideInfoRequested.Add(slideRowId))
            {
                return;
            }
        }
        EzwGetSlideInfoCommand cmd = new()
        {
            SlideRowId = slideRowId,
            Revision = revision,
            RequestRev = CurrentRequestRevString(),
            PresRowId = presRowId,
            RecType = recType,
        };
        string json = JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwGetSlideInfoCommand);
        if (!_sendChannel.Writer.TryWrite(json))
        {
            // Channel full; clear flag so we retry later.
            lock (_slideInfoRequestLock) { _slideInfoRequested.Remove(slideRowId); }
        }
    }

    private void CheckPresentationLoaded()
    {
        EwLiveDataEvent? live = _currentLiveData;
        if (live is null || live.Slides.Count == 0)
        {
            return;
        }
        if (_presentationLoadedFiredForLiveRev == live.LiveRev)
        {
            return;
        }
        foreach (EwLiveDataSlide s in live.Slides)
        {
            if (!_slideInfoCache.ContainsKey(s.SlideRowId))
            {
                return;
            }
        }
        _presentationLoadedFiredForLiveRev = live.LiveRev;
        _logger.LogDebug("Presentation fully loaded: pres_rowid={PresRowId} slides={Count}",
            live.PresRowId, live.Slides.Count);
        Raise(PresentationLoaded, new EwPresentationLoadedEvent(live.PresRowId, live.LiveRev, live.Slides.Count));
    }

    private async Task EnqueueGetLiveDataAsync()
    {
        try
        {
            EzwGetLiveDataCommand cmd = new() { RequestRev = CurrentRequestRevString() };
            string json = JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwGetLiveDataCommand);
            await _sendChannel.Writer.WriteAsync(json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "could not enqueue GetLiveData");
        }
    }

    private async Task EnqueueGetScheduleDataAsync()
    {
        try
        {
            EzwGetScheduleDataCommand cmd = new() { RequestRev = CurrentRequestRevString() };
            string json = JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwGetScheduleDataCommand);
            await _sendChannel.Writer.WriteAsync(json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "could not enqueue GetScheduleData");
        }
    }

    private void TryRequestScheduleTitle(long presRowId, long revision)
    {
        if (presRowId <= 0) return;
        if (_scheduleTitleCache.ContainsKey(presRowId)) return;
        lock (_scheduleTitleLock)
        {
            if (!_scheduleTitleRequested.Add(presRowId)) return;
        }
        // Mobile app captured: getSlideInfo(slide_rowid=0, rectype=0, pres_rowid=N, revision=<title_revision>)
        // returns the schedule item's song title. rectype=0 = scheduled item.
        EzwGetSlideInfoCommand cmd = new()
        {
            SlideRowId = 0,
            Revision = revision,
            RequestRev = CurrentRequestRevString(),
            PresRowId = presRowId,
            RecType = 0,
        };
        string json = JsonSerializer.Serialize(cmd, EzwJsonContext.Default.EzwGetSlideInfoCommand);
        if (!_sendChannel.Writer.TryWrite(json))
        {
            lock (_scheduleTitleLock) { _scheduleTitleRequested.Remove(presRowId); }
        }
    }

    private async Task WriteFrameAsync(PipeWriter writer, string json, CancellationToken ct)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("EW out: {Json}", json);
        }
        int byteCount = Encoding.UTF8.GetByteCount(json) + 2;
        Memory<byte> memory = writer.GetMemory(byteCount);
        int written = Encoding.UTF8.GetBytes(json, memory.Span);
        memory.Span[written] = (byte)'\r';
        memory.Span[written + 1] = (byte)'\n';
        writer.Advance(written + 2);
        FlushResult result = await writer.FlushAsync(ct).ConfigureAwait(false);
        if (result.IsCompleted)
        {
            throw new IOException("transport closed");
        }
    }

    private void SetState(ConnectionState newState)
    {
        ConnectionState previous = (ConnectionState)Interlocked.Exchange(ref _state, (int)newState);
        if (previous != newState)
        {
            _logger.LogInformation("EasyWorship state: {Previous} -> {State}", previous, newState);
            Raise(StateChanged, newState);
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T arg)
    {
        if (handler is null)
        {
            return;
        }
        try
        {
            handler.Invoke(this, arg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event handler for {Event} threw", typeof(T).Name);
        }
    }
}
