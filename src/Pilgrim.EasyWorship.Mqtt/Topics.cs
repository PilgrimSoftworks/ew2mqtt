namespace Pilgrim.EasyWorship.Mqtt;

internal sealed class Topics
{
    public Topics(string topicBase, string instanceId)
    {
        Root = $"{topicBase}/{instanceId}";
        Availability = $"{Root}/availability";
        Connection = $"{Root}/connection";
        State = $"{Root}/state";
        StateLogo = $"{State}/logo";
        StateBlack = $"{State}/black";
        StateClear = $"{State}/clear";
        StatePresentationNumber = $"{State}/presentation/number";
        StatePresentationRowId = $"{State}/presentation/rowid";
        StateSlideNumber = $"{State}/slide/number";
        StateSlideRowId = $"{State}/slide/rowid";
        StateScheduleRev = $"{State}/schedule/rev";
        StateLiveRev = $"{State}/live/rev";
        StateImageHash = $"{State}/imagehash";
        StateRequestRev = $"{State}/requestrev";
        StateSlideTitle = $"{State}/slide/title";
        StateSlideContent = $"{State}/slide/content";
        StateSlideIndex = $"{State}/slide/index";
        StateSlideTotal = $"{State}/slide/total";
        StatePresentationTitle = $"{State}/presentation/title";
        StatePresentationLoaded = $"{State}/presentation/loaded";
        StatePresentationSlides = $"{State}/presentation/slides";
        StateSchedule = $"{State}/schedule";
        EventsPairing = $"{Root}/events/pairing";
        EventsHeartbeat = $"{Root}/events/heartbeat";
        EventsUnknown = $"{Root}/events/unknown";
        EventsSlideChanged = $"{Root}/events/slide-changed";
        EventsPresentationLoaded = $"{Root}/events/presentation-loaded";
        CommandRoot = $"{Root}/cmd";
        CommandWildcard = $"{CommandRoot}/#";
    }

    public string Root { get; }
    public string Availability { get; }
    public string Connection { get; }
    public string State { get; }
    public string StateLogo { get; }
    public string StateBlack { get; }
    public string StateClear { get; }
    public string StatePresentationNumber { get; }
    public string StatePresentationRowId { get; }
    public string StateSlideNumber { get; }
    public string StateSlideRowId { get; }
    public string StateScheduleRev { get; }
    public string StateLiveRev { get; }
    public string StateImageHash { get; }
    public string StateRequestRev { get; }
    public string StateSlideTitle { get; }
    public string StateSlideContent { get; }
    public string StateSlideIndex { get; }
    public string StateSlideTotal { get; }
    public string StatePresentationTitle { get; }
    public string StatePresentationLoaded { get; }
    public string StatePresentationSlides { get; }
    public string StateSchedule { get; }
    public string EventsPairing { get; }
    public string EventsHeartbeat { get; }
    public string EventsUnknown { get; }
    public string EventsSlideChanged { get; }
    public string EventsPresentationLoaded { get; }
    public string CommandRoot { get; }
    public string CommandWildcard { get; }

    public string CommandTopic(string verb) => $"{CommandRoot}/{verb}";
}
