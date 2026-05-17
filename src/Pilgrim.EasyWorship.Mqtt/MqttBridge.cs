using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using Pilgrim.EasyWorship;
using Pilgrim.EasyWorship.Events;
using Pilgrim.EasyWorship.Mqtt.Options;
using Pilgrim.EasyWorship.Options;

namespace Pilgrim.EasyWorship.Mqtt;

internal sealed class MqttBridge : BackgroundService
{
    private readonly Ew2MqttOptions _options;
    private readonly IOptions<EasyWorshipClientOptions> _ewClientOptions;
    private readonly UidStore _uidStore;
    private readonly EndpointResolver _endpointResolver;
    private readonly IEasyWorshipClient _easyWorship;
    private readonly ILogger<MqttBridge> _logger;
    private readonly Topics _topics;

    private IMqttClient? _mqtt;
    private MqttClientOptions? _mqttClientOptions;
    private CommandRouter? _commandRouter;
    private CancellationToken _stoppingToken;
    private volatile bool _stopping;
    private int _reconnecting;

    public MqttBridge(
        IOptions<Ew2MqttOptions> options,
        IOptions<EasyWorshipClientOptions> ewClientOptions,
        UidStore uidStore,
        EndpointResolver endpointResolver,
        IEasyWorshipClient easyWorship,
        ILogger<MqttBridge> logger)
    {
        _options = options.Value;
        _ewClientOptions = ewClientOptions;
        _uidStore = uidStore;
        _endpointResolver = endpointResolver;
        _easyWorship = easyWorship;
        _logger = logger;
        _topics = new Topics(_options.Mqtt.TopicBase, _options.InstanceId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        await ConnectMqttWithRetriesAsync(stoppingToken).ConfigureAwait(false);

        (string Host, int Port)? endpoint = await ResolveEndpointWithRetriesAsync(stoppingToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            // Only reached on shutdown — the resolver retries indefinitely otherwise.
            return;
        }

        ConfigureEasyWorshipClient(endpoint.Value.Host, endpoint.Value.Port);
        WireEvents();

        _commandRouter = new CommandRouter(_topics, _easyWorship, _logger);
        _mqtt!.ApplicationMessageReceivedAsync += OnMqttMessageAsync;
        await SubscribeCommandsAsync(stoppingToken).ConfigureAwait(false);

        await _easyWorship.StartAsync(stoppingToken).ConfigureAwait(false);
        await PublishAvailabilityAsync("online", stoppingToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the auto-reconnect handler from fighting the intentional shutdown.
        _stopping = true;
        if (_mqtt is not null)
        {
            _mqtt.DisconnectedAsync -= OnMqttDisconnectedAsync;
        }

        try
        {
            await _easyWorship.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "error stopping EasyWorship client");
        }

        if (_mqtt is { IsConnected: true })
        {
            try
            {
                await PublishAvailabilityAsync("offline", cancellationToken).ConfigureAwait(false);
                await _mqtt.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "error disconnecting MQTT client");
            }
        }
        _mqtt?.Dispose();
        _mqtt = null;

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ConfigureEasyWorshipClient(string host, int port)
    {
        EasyWorshipServiceOptions ew = _options.EasyWorship;
        string uid = _uidStore.LoadOrCreate(ew.Uid);
        EasyWorshipClientOptions clientOptions = _ewClientOptions.Value;
        clientOptions.Host = host;
        clientOptions.Port = port;
        clientOptions.Uid = uid;
        clientOptions.DeviceName = ew.DeviceName;
        clientOptions.DeviceType = ew.DeviceType;
        clientOptions.HeartbeatIntervalSeconds = ew.HeartbeatIntervalSeconds;
        clientOptions.RequestRevEncoding = ew.RequestRevEncoding;
        clientOptions.Reconnect = ew.Reconnect;
        _logger.LogInformation("EasyWorship client configured for {Host}:{Port}", host, port);
    }

    private void WireEvents()
    {
        _easyWorship.StatusReceived += OnStatusAsync;
        _easyWorship.PairingChanged += OnPairingAsync;
        _easyWorship.HeartbeatReceived += OnHeartbeatAsync;
        _easyWorship.UnknownEventReceived += OnUnknownAsync;
        _easyWorship.StateChanged += OnStateChangedAsync;
        _easyWorship.LiveDataReceived += OnLiveDataAsync;
        _easyWorship.SlideInfoReceived += OnSlideInfoAsync;
        _easyWorship.PresentationLoaded += OnPresentationLoadedAsync;
        _easyWorship.ScheduleDataReceived += OnScheduleDataAsync;
        _easyWorship.SlideInfoReceived += OnScheduleTitleAsync;
    }

    private async void OnScheduleDataAsync(object? sender, EwScheduleDataEvent ev)
    {
        try
        {
            await PublishScheduleAsync(default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to publish schedule");
        }
    }

    private async void OnScheduleTitleAsync(object? sender, EwSlideInfoEvent ev)
    {
        // When a slideInfo arrives for slide_rowid=0 referencing one of the schedule
        // entries we know about, the schedule's title cache just got a new entry.
        // Republish the schedule JSON so consumers see the title appear.
        if (ev.SlideRowId != 0) return;
        if (_easyWorship.CurrentSchedule is null) return;
        try
        {
            await PublishScheduleAsync(default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to republish schedule");
        }
    }

    private async Task PublishScheduleAsync(CancellationToken ct)
    {
        IReadOnlyList<EwScheduleEntry>? schedule = _easyWorship.CurrentSchedule;
        if (schedule is null) return;
        List<ScheduleItemPayload> items = new(schedule.Count);
        for (int i = 0; i < schedule.Count; i++)
        {
            EwScheduleEntry entry = schedule[i];
            _easyWorship.TryGetScheduleTitle(entry.PresRowId, out string? title);
            List<ScheduleSlidePayload> slides = new(entry.Slides.Count);
            for (int s = 0; s < entry.Slides.Count; s++)
            {
                slides.Add(new ScheduleSlidePayload(
                    Index: s + 1,
                    SlideRowId: entry.Slides[s].SlideRowId,
                    Revision: entry.Slides[s].Revision));
            }
            items.Add(new ScheduleItemPayload(
                Index: i + 1,
                PresRowId: entry.PresRowId,
                Revision: entry.Revision,
                Title: title ?? string.Empty,
                Slides: slides));
        }
        string payload = JsonSerializer.Serialize(
            new SchedulePayload(schedule.Count, items), ServiceJsonContext.Default.SchedulePayload);
        await PublishAsync(_topics.StateSchedule, payload, _options.Mqtt.RetainState, MqttQualityOfServiceLevel.AtMostOnce, ct).ConfigureAwait(false);
    }

    private long _currentSlideRowId;

    private async Task ConnectMqttWithRetriesAsync(CancellationToken ct)
    {
        MqttClientFactory factory = new();
        _mqtt = factory.CreateMqttClient();

        (string? host, int port, bool useTls) = ParseMqttServer(_options.Mqtt.Server, _options.Mqtt.Tls);
        MqttClientOptionsBuilder builder = new MqttClientOptionsBuilder()
            .WithClientId(_options.Mqtt.ClientId)
            .WithTcpServer(host, port)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.Mqtt.KeepAliveSeconds))
            .WithCleanSession(true)
            .WithWillTopic(_topics.Availability)
            .WithWillPayload(Encoding.UTF8.GetBytes("offline"))
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (useTls)
        {
            builder = builder.WithTlsOptions(o => { });
        }
        if (!string.IsNullOrEmpty(_options.Mqtt.Username))
        {
            builder = builder.WithCredentials(_options.Mqtt.Username, _options.Mqtt.Password ?? string.Empty);
        }

        _mqttClientOptions = builder.Build();
        _mqtt.DisconnectedAsync += OnMqttDisconnectedAsync;

        await ConnectWithBackoffAsync(ct).ConfigureAwait(false);
    }

    private async Task ConnectWithBackoffAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested && !_stopping)
        {
            try
            {
                await _mqtt!.ConnectAsync(_mqttClientOptions!, ct).ConfigureAwait(false);
                _logger.LogInformation("Connected to MQTT broker {Server}", _options.Mqtt.Server);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                TimeSpan delay = TimeSpan.FromSeconds(Math.Min(_options.Mqtt.ReconnectIntervalSeconds * Math.Pow(1.5, attempt - 1), 60));
                _logger.LogWarning(ex, "MQTT connect failed; retrying in {Delay}", delay);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private Task SubscribeCommandsAsync(CancellationToken ct) =>
        _mqtt!.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(_topics.CommandWildcard, MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(),
            ct);

    // MQTTnet raises DisconnectedAsync after any drop. WithCleanSession(true) means
    // the broker forgets our subscription, so a bare TCP reconnect would leave the
    // command tree dead — we must reconnect AND re-subscribe AND re-assert
    // availability/connection. The reconnect is offloaded so we never block (or
    // re-enter) MQTTnet's disconnect pipeline, and guarded so overlapping drops
    // don't spawn parallel reconnect loops.
    private Task OnMqttDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (_stopping || _stoppingToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }
        _logger.LogWarning("MQTT disconnected ({Reason}); reconnecting", e.Reason);
        _ = Task.Run(ReconnectAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ReconnectAsync()
    {
        try
        {
            await ConnectWithBackoffAsync(_stoppingToken).ConfigureAwait(false);
            if (_stopping || _stoppingToken.IsCancellationRequested || _mqtt is not { IsConnected: true })
            {
                return;
            }
            await SubscribeCommandsAsync(_stoppingToken).ConfigureAwait(false);
            await PublishAvailabilityAsync("online", _stoppingToken).ConfigureAwait(false);
            await PublishConnectionAsync(
                _easyWorship.State,
                paired: _easyWorship.State == ConnectionState.Connected,
                host: _ewClientOptions.Value.Host,
                port: _ewClientOptions.Value.Port,
                _stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("MQTT reconnected and re-subscribed");
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT reconnect failed");
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    // Discovery must never permanently give up: the service routinely starts
    // before EasyWorship (on boot), and since a BackgroundService that parks
    // itself stays "running", systemd/Docker restart policies wouldn't recover
    // it. So retry forever with a capped backoff; only cancellation (shutdown)
    // returns null.
    private async Task<(string Host, int Port)?> ResolveEndpointWithRetriesAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            (string Host, int Port)? ep = await _endpointResolver.ResolveAsync(_options.EasyWorship, ct).ConfigureAwait(false);
            if (ep is not null)
            {
                return ep;
            }

            attempt++;
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(10 * Math.Pow(1.5, attempt - 1), 60));
            _logger.LogWarning("EasyWorship not found (attempt {Attempt}); retrying in {Delay}", attempt, delay);
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static (string Host, int Port, bool Tls) ParseMqttServer(string server, bool defaultTls)
    {
        if (Uri.TryCreate(server, UriKind.Absolute, out Uri? uri))
        {
            bool tls = string.Equals(uri.Scheme, "mqtts", StringComparison.OrdinalIgnoreCase);
            int port = uri.IsDefaultPort ? (tls ? 8883 : 1883) : uri.Port;
            return (uri.Host, port, tls || defaultTls);
        }
        string[] parts = server.Split(':', 2);
        string host = parts[0];
        int p = parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 1883;
        return (host, p, defaultTls);
    }

    private Task OnMqttMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        return _commandRouter?.RouteAsync(args) ?? Task.CompletedTask;
    }

    private async void OnStateChangedAsync(object? sender, ConnectionState state)
    {
        try
        {
            await PublishConnectionAsync(state, paired: state == ConnectionState.Connected, host: _ewClientOptions.Value.Host, port: _ewClientOptions.Value.Port, default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to publish state {State}", state);
        }
    }

    private async void OnStatusAsync(object? sender, EwStatusEvent ev)
    {
        try
        {
            // Update the tracker BEFORE any awaits so OnSlideInfoAsync sees the current
            // slide regardless of dispatcher interleaving.
            long previous = Interlocked.Exchange(ref _currentSlideRowId, ev.SlideRowId);

            await PublishStatusAsync(ev, default).ConfigureAwait(false);

            if (previous != ev.SlideRowId)
            {
                await PublishCurrentSlideAsync(ev.SlideRowId, default).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to publish status");
        }
    }

    private async void OnPairingAsync(object? sender, EwPairingEvent ev)
    {
        try
        {
            string payload = JsonSerializer.Serialize(
                new PairingPayload(ev.Paired), ServiceJsonContext.Default.PairingPayload);
            await PublishAsync(_topics.EventsPairing, payload, retain: false, qos: MqttQualityOfServiceLevel.AtLeastOnce, default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to publish pairing event");
        }
    }

    private async void OnHeartbeatAsync(object? sender, EwHeartbeatEvent ev)
    {
        try
        {
            string payload = JsonSerializer.Serialize(
                new HeartbeatPayload(ev.RequestRev, DateTimeOffset.UtcNow), ServiceJsonContext.Default.HeartbeatPayload);
            await PublishAsync(_topics.EventsHeartbeat, payload, retain: false, qos: MqttQualityOfServiceLevel.AtMostOnce, default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "failed to publish heartbeat event");
        }
    }

    private async void OnUnknownAsync(object? sender, EwUnknownEvent ev)
    {
        try
        {
            string payload = JsonSerializer.Serialize(
                new UnknownPayload(ev.Action, ev.RawJson, ev.RequestRev), ServiceJsonContext.Default.UnknownPayload);
            await PublishAsync(_topics.EventsUnknown, payload, retain: false, qos: MqttQualityOfServiceLevel.AtMostOnce, default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to publish unknown event");
        }
    }

    private async void OnLiveDataAsync(object? sender, EwLiveDataEvent ev)
    {
        try
        {
            bool retain = _options.Mqtt.RetainState;
            await PublishAsync(_topics.StateSlideTotal, ev.Slides.Count.ToString(CultureInfo.InvariantCulture), retain, MqttQualityOfServiceLevel.AtMostOnce, default).ConfigureAwait(false);
            // A new presentation has started loading; mark "loaded" false until all slideInfo arrive.
            await PublishAsync(_topics.StatePresentationLoaded, "false", retain, MqttQualityOfServiceLevel.AtMostOnce, default).ConfigureAwait(false);
            // If we already know the current slide, recompute its index now that the list arrived.
            long currentSlide = Interlocked.Read(ref _currentSlideRowId);
            if (currentSlide != 0)
            {
                await PublishCurrentSlideAsync(currentSlide, default).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to publish LiveData state");
        }
    }

    private async void OnPresentationLoadedAsync(object? sender, EwPresentationLoadedEvent ev)
    {
        try
        {
            bool retain = _options.Mqtt.RetainState;
            await PublishAsync(_topics.StatePresentationLoaded, "true", retain, MqttQualityOfServiceLevel.AtMostOnce, default).ConfigureAwait(false);

            // Publish the full song so consumers see the entire presentation, even when
            // `state/slide/title` is unknown (e.g. bridge connected while EW was already
            // showing a slide and never sent a `status` for the current slide).
            EwLiveDataEvent? live = _easyWorship.CurrentLiveData;
            if (live is not null)
            {
                List<PresentationSlidePayload> slides = new(live.Slides.Count);
                for (int i = 0; i < live.Slides.Count; i++)
                {
                    long rowid = live.Slides[i].SlideRowId;
                    _easyWorship.TryGetSlideInfo(rowid, out EwSlideInfoEvent? info);
                    slides.Add(new PresentationSlidePayload(
                        Index: i + 1,
                        SlideRowId: rowid,
                        Title: info?.Title ?? string.Empty,
                        Content: info?.Content ?? string.Empty));
                }
                string slidesJson = JsonSerializer.Serialize(
                    new PresentationSlidesPayload(ev.PresRowId, ev.LiveRev, slides),
                    ServiceJsonContext.Default.PresentationSlidesPayload);
                await PublishAsync(_topics.StatePresentationSlides, slidesJson, retain, MqttQualityOfServiceLevel.AtMostOnce, default).ConfigureAwait(false);
            }

            string payload = JsonSerializer.Serialize(
                new PresentationLoadedPayload(ev.PresRowId, ev.LiveRev, ev.SlideCount, DateTimeOffset.UtcNow),
                ServiceJsonContext.Default.PresentationLoadedPayload);
            await PublishAsync(_topics.EventsPresentationLoaded, payload, retain: false, MqttQualityOfServiceLevel.AtLeastOnce, default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to publish presentation-loaded");
        }
    }

    private async void OnSlideInfoAsync(object? sender, EwSlideInfoEvent ev)
    {
        try
        {
            bool retain = _options.Mqtt.RetainState;
            // slide_rowid 0 conventionally carries the song/presentation title.
            if (ev.SlideRowId == 0)
            {
                await PublishAsync(_topics.StatePresentationTitle, ev.Title ?? string.Empty, retain, MqttQualityOfServiceLevel.AtMostOnce, default).ConfigureAwait(false);
            }
            // If this is the slide currently on screen, refresh title/content.
            if (ev.SlideRowId == Interlocked.Read(ref _currentSlideRowId))
            {
                await PublishAsync(_topics.StateSlideTitle, ev.Title ?? string.Empty, retain, MqttQualityOfServiceLevel.AtMostOnce, default).ConfigureAwait(false);
                await PublishAsync(_topics.StateSlideContent, ev.Content ?? string.Empty, retain, MqttQualityOfServiceLevel.AtMostOnce, default).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to publish SlideInfo state");
        }
    }

    private async Task PublishCurrentSlideAsync(long slideRowId, CancellationToken ct)
    {
        bool retain = _options.Mqtt.RetainState;
        EwLiveDataEvent? live = _easyWorship.CurrentLiveData;
        int index = 0;
        int total = 0;
        if (live is { Slides: var slides })
        {
            total = slides.Count;
            for (int i = 0; i < slides.Count; i++)
            {
                if (slides[i].SlideRowId == slideRowId) { index = i + 1; break; }
            }
        }
        await PublishAsync(_topics.StateSlideIndex, index.ToString(CultureInfo.InvariantCulture), retain, MqttQualityOfServiceLevel.AtMostOnce, ct).ConfigureAwait(false);
        await PublishAsync(_topics.StateSlideTotal, total.ToString(CultureInfo.InvariantCulture), retain, MqttQualityOfServiceLevel.AtMostOnce, ct).ConfigureAwait(false);

        string title = string.Empty;
        string content = string.Empty;
        if (_easyWorship.TryGetSlideInfo(slideRowId, out EwSlideInfoEvent? info))
        {
            title = info.Title ?? string.Empty;
            content = info.Content ?? string.Empty;
        }
        await PublishAsync(_topics.StateSlideTitle, title, retain, MqttQualityOfServiceLevel.AtMostOnce, ct).ConfigureAwait(false);
        await PublishAsync(_topics.StateSlideContent, content, retain, MqttQualityOfServiceLevel.AtMostOnce, ct).ConfigureAwait(false);

        string changed = JsonSerializer.Serialize(
            new SlideChangedPayload(slideRowId, index, total, title, content),
            ServiceJsonContext.Default.SlideChangedPayload);
        await PublishAsync(_topics.EventsSlideChanged, changed, retain: false, MqttQualityOfServiceLevel.AtLeastOnce, ct).ConfigureAwait(false);
    }

    private async Task PublishStatusAsync(EwStatusEvent ev, CancellationToken ct)
    {
        bool retain = _options.Mqtt.RetainState;
        await Task.WhenAll(
            PublishAsync(_topics.StateLogo, ev.Logo ? "ON" : "OFF", retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.StateBlack, ev.Black ? "ON" : "OFF", retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.StateClear, ev.Clear ? "ON" : "OFF", retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.StatePresentationNumber, ev.PresNo.ToString(CultureInfo.InvariantCulture), retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.StatePresentationRowId, ev.PresRowId.ToString(CultureInfo.InvariantCulture), retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.StateSlideNumber, ev.SlideNo.ToString(CultureInfo.InvariantCulture), retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.StateSlideRowId, ev.SlideRowId.ToString(CultureInfo.InvariantCulture), retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.StateScheduleRev, ev.ScheduleRev.ToString(CultureInfo.InvariantCulture), retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.StateLiveRev, ev.LiveRev.ToString(CultureInfo.InvariantCulture), retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.StateImageHash, ev.ImageHash, retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.StateRequestRev, ev.RequestRev.ToString(CultureInfo.InvariantCulture), retain, MqttQualityOfServiceLevel.AtMostOnce, ct),
            PublishAsync(_topics.State, JsonSerializer.Serialize(new StatePayload(
                Logo: ev.Logo,
                Black: ev.Black,
                Clear: ev.Clear,
                Presentation: new StateNumberRowId(ev.PresNo, ev.PresRowId),
                Slide: new StateNumberRowId(ev.SlideNo, ev.SlideRowId),
                ScheduleRev: ev.ScheduleRev,
                LiveRev: ev.LiveRev,
                ImageHash: ev.ImageHash,
                RequestRev: ev.RequestRev), ServiceJsonContext.Default.StatePayload),
                retain, MqttQualityOfServiceLevel.AtMostOnce, ct)
        ).ConfigureAwait(false);
    }

    private Task PublishAvailabilityAsync(string value, CancellationToken ct) =>
        PublishAsync(_topics.Availability, value, retain: true, MqttQualityOfServiceLevel.AtLeastOnce, ct);

    private Task PublishConnectionAsync(ConnectionState state, bool paired, string? host, int port, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(
            new ConnectionPayload(state.ToString(), paired, host, port, _easyWorship.LastRequestRev),
            ServiceJsonContext.Default.ConnectionPayload);
        return PublishAsync(_topics.Connection, payload, retain: true, MqttQualityOfServiceLevel.AtLeastOnce, ct);
    }

    private async Task PublishAsync(string topic, string payload, bool retain, MqttQualityOfServiceLevel qos, CancellationToken ct)
    {
        if (_mqtt is null || !_mqtt.IsConnected)
        {
            _logger.LogWarning("dropping publish to {Topic}: MQTT not connected", topic);
            return;
        }
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            string preview = payload.Length > 120 ? payload[..120] + "…" : payload;
            _logger.LogTrace("publish {Topic} retain={Retain} bytes={Bytes} payload={Preview}",
                topic, retain, payload.Length, preview);
        }
        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(qos)
            .Build();
        await _mqtt.PublishAsync(message, ct).ConfigureAwait(false);
    }
}
