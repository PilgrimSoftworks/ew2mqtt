using Pilgrim.EasyWorship.Events;

namespace Pilgrim.EasyWorship;

public interface IEasyWorshipClient : IAsyncDisposable
{
    ConnectionState State { get; }
    long LastRequestRev { get; }
    EwLiveDataEvent? CurrentLiveData { get; }
    bool TryGetSlideInfo(long slideRowId, out EwSlideInfoEvent slideInfo);

    event EventHandler<ConnectionState>? StateChanged;
    event EventHandler<EwStatusEvent>? StatusReceived;
    event EventHandler<EwPairingEvent>? PairingChanged;
    event EventHandler<EwHeartbeatEvent>? HeartbeatReceived;
    event EventHandler<EwUnknownEvent>? UnknownEventReceived;
    event EventHandler<EwLiveDataEvent>? LiveDataReceived;
    event EventHandler<EwSlideInfoEvent>? SlideInfoReceived;
    event EventHandler<EwPresentationLoadedEvent>? PresentationLoaded;
    event EventHandler<EwScheduleDataEvent>? ScheduleDataReceived;
    IReadOnlyList<EwScheduleEntry>? CurrentSchedule { get; }
    bool TryGetScheduleTitle(long presRowId, out string title);

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    Task SendNextSlideAsync(CancellationToken ct = default);
    Task SendPrevSlideAsync(CancellationToken ct = default);
    Task SendGotoSlideAsync(int slideNumber, CancellationToken ct = default);
    Task SendGotoStartSlideAsync(CancellationToken ct = default);
    Task SendGotoStartPresentationAsync(CancellationToken ct = default);
    Task SendGotoScheduleAsync(int scheduleNumber, CancellationToken ct = default);
    Task SendNextScheduleAsync(CancellationToken ct = default);
    Task SendPrevScheduleAsync(CancellationToken ct = default);
    Task SendNextBuildAsync(CancellationToken ct = default);
    Task SendPrevBuildAsync(CancellationToken ct = default);
    Task SendPlayAsync(CancellationToken ct = default);
    Task SendPauseAsync(CancellationToken ct = default);
    Task SendToggleAsync(CancellationToken ct = default);
    Task SendStatusAsync(StatusOverlay overlay, CancellationToken ct = default);
    Task SendActivateSlideAsync(long presRowId, long slideRowId, CancellationToken ct = default);
}
