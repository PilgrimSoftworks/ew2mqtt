namespace Pilgrim.EasyWorship.Events;

public sealed record EwPairingEvent(bool Paired);

public sealed record EwHeartbeatEvent(long RequestRev);

public sealed record EwUnknownEvent(string Action, string RawJson, long? RequestRev);

public sealed record EwStatusEvent(
    bool Logo,
    bool Black,
    bool Clear,
    long PresRowId,
    long SlideRowId,
    int PresNo,
    int SlideNo,
    long ScheduleRev,
    long LiveRev,
    string ImageHash,
    long RequestRev);

public sealed record EwLiveDataEvent(
    long LiveRev,
    long PresRowId,
    long TitleRevision,
    IReadOnlyList<EwLiveDataSlide> Slides);

public readonly record struct EwLiveDataSlide(long SlideRowId, long Revision);

public sealed record EwSlideInfoEvent(
    long SlideRowId,
    string Title,
    string Content);

public sealed record EwPresentationLoadedEvent(
    long PresRowId,
    long LiveRev,
    int SlideCount);

public sealed record EwScheduleDataEvent(IReadOnlyList<EwScheduleEntry> Entries);

public sealed record EwScheduleEntry(long PresRowId, long Revision, IReadOnlyList<EwScheduleSlide> Slides);

public readonly record struct EwScheduleSlide(long SlideRowId, long Revision);
