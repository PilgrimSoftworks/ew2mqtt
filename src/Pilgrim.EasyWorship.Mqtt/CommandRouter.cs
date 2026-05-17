using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Pilgrim.EasyWorship;
using Pilgrim.EasyWorship.Events;

namespace Pilgrim.EasyWorship.Mqtt;

internal sealed class CommandRouter
{
    private readonly Topics _topics;
    private readonly IEasyWorshipClient _client;
    private readonly ILogger _logger;
    // Overlay state is mirrored from EW's StatusReceived (EW dispatch thread) and
    // read-modify-written by MQTT command handlers (MQTTnet thread); guard the
    // triple so a concurrent status update can't tear a TOGGLE computation.
    private readonly object _overlayLock = new();
    private bool _logoState;
    private bool _blackState;
    private bool _clearState;

    public CommandRouter(Topics topics, IEasyWorshipClient client, ILogger logger)
    {
        _topics = topics;
        _client = client;
        _logger = logger;
        _client.StatusReceived += (_, ev) =>
        {
            lock (_overlayLock)
            {
                _logoState = ev.Logo;
                _blackState = ev.Black;
                _clearState = ev.Clear;
            }
        };
    }

    public Task RouteAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        string topic = args.ApplicationMessage.Topic ?? string.Empty;
        string payload = args.ApplicationMessage.Payload.Length == 0
            ? string.Empty
            : Encoding.UTF8.GetString(args.ApplicationMessage.Payload);
        return RouteAsync(topic, payload);
    }

    public async Task RouteAsync(string topic, string payload)
    {
        if (!topic.StartsWith(_topics.CommandRoot, StringComparison.Ordinal))
        {
            return;
        }
        string rest = topic.Length == _topics.CommandRoot.Length
            ? string.Empty
            : topic[(_topics.CommandRoot.Length + 1)..];

        _logger.LogDebug("command received: {Verb} payload={Payload}", rest, payload);

        try
        {
            await DispatchAsync(rest, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "command failed for topic {Topic}", topic);
        }
    }

    private Task DispatchAsync(string rest, string payload)
    {
        if (rest.Length == 0)
        {
            return DispatchEnvelopeAsync(payload);
        }

        return rest switch
        {
            "nextSlide" => _client.SendNextSlideAsync(),
            "prevSlide" => _client.SendPrevSlideAsync(),
            "gotoStartSlide" => _client.SendGotoStartSlideAsync(),
            "gotoStartPresentation" => _client.SendGotoStartPresentationAsync(),
            "nextSchedule" => _client.SendNextScheduleAsync(),
            "prevSchedule" => _client.SendPrevScheduleAsync(),
            "nextBuild" => _client.SendNextBuildAsync(),
            "prevBuild" => _client.SendPrevBuildAsync(),
            "play" or "Play" => _client.SendPlayAsync(),
            "pause" or "Pause" => _client.SendPauseAsync(),
            "toggle" or "Toggle" => _client.SendToggleAsync(),
            "gotoSlide" => _client.SendGotoSlideAsync(ParseIndex(payload, "slide")),
            "gotoSchedule" => _client.SendGotoScheduleAsync(ParseIndex(payload, "schedule")),
            "overlay" => DispatchOverlayAtomicAsync(payload),
            "overlay/logo" => DispatchOverlayBitAsync("logo", payload),
            "overlay/black" => DispatchOverlayBitAsync("black", payload),
            "overlay/clear" => DispatchOverlayBitAsync("clear", payload),
            "activate" => DispatchActivateAsync(payload),
            _ => UnknownCommand(rest),
        };
    }

    private Task UnknownCommand(string verb)
    {
        _logger.LogWarning("ignoring unknown command {Verb}", verb);
        return Task.CompletedTask;
    }

    private Task DispatchEnvelopeAsync(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Task.CompletedTask;
        }
        using JsonDocument doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("action", out JsonElement actionEl))
        {
            _logger.LogWarning("envelope missing action: {Payload}", payload);
            return Task.CompletedTask;
        }
        string action = actionEl.GetString() ?? string.Empty;

        switch (action)
        {
            case "gotoSlide":
                return _client.SendGotoSlideAsync(GetIntProperty(doc.RootElement, "slide"));
            case "gotoSchedule":
                return _client.SendGotoScheduleAsync(GetIntProperty(doc.RootElement, "schedule"));
            case "status":
                return _client.SendStatusAsync(new StatusOverlay(
                    GetBoolProperty(doc.RootElement, "logo"),
                    GetBoolProperty(doc.RootElement, "black"),
                    GetBoolProperty(doc.RootElement, "clear")));
            default:
                return DispatchAsync(action, string.Empty);
        }
    }

    private Task DispatchActivateAsync(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            _logger.LogWarning("activate command requires a JSON payload");
            return Task.CompletedTask;
        }
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        // Form 1: explicit {"pres_rowid":N,"slide_rowid":M}
        if (root.TryGetProperty("pres_rowid", out _) && root.TryGetProperty("slide_rowid", out _))
        {
            long presRowId = (long)GetIntProperty(root, "pres_rowid");
            long slideRowId = (long)GetIntProperty(root, "slide_rowid");
            return _client.SendActivateSlideAsync(presRowId, slideRowId);
        }

        // Form 2: {"schedule_index":N[,"slide_index":M]} — look up in CurrentSchedule.
        if (root.TryGetProperty("schedule_index", out _))
        {
            int scheduleIndex = GetIntProperty(root, "schedule_index"); // 1-based
            int slideIndex = root.TryGetProperty("slide_index", out _) ? GetIntProperty(root, "slide_index") : 1;
            IReadOnlyList<EwScheduleEntry>? schedule = _client.CurrentSchedule;
            if (schedule is null || scheduleIndex < 1 || scheduleIndex > schedule.Count)
            {
                _logger.LogWarning("activate: schedule_index {Idx} is out of range (schedule has {Count} items)",
                    scheduleIndex, schedule?.Count ?? 0);
                return Task.CompletedTask;
            }
            EwScheduleEntry entry = schedule[scheduleIndex - 1];
            if (slideIndex < 1 || slideIndex > entry.Slides.Count)
            {
                _logger.LogWarning("activate: slide_index {Idx} is out of range (entry has {Count} slides)",
                    slideIndex, entry.Slides.Count);
                return Task.CompletedTask;
            }
            EwScheduleSlide slide = entry.Slides[slideIndex - 1];
            return _client.SendActivateSlideAsync(entry.PresRowId, slide.SlideRowId);
        }

        _logger.LogWarning("activate: payload missing pres_rowid+slide_rowid or schedule_index: {Payload}", payload);
        return Task.CompletedTask;
    }

    private Task DispatchOverlayAtomicAsync(string payload)
    {
        using JsonDocument doc = JsonDocument.Parse(payload);
        bool logo = doc.RootElement.TryGetProperty("logo", out JsonElement l) && l.GetBoolean();
        bool black = doc.RootElement.TryGetProperty("black", out JsonElement b) && b.GetBoolean();
        bool clear = doc.RootElement.TryGetProperty("clear", out JsonElement c) && c.GetBoolean();
        lock (_overlayLock)
        {
            _logoState = logo;
            _blackState = black;
            _clearState = clear;
        }
        return _client.SendStatusAsync(new StatusOverlay(logo, black, clear));
    }

    private Task DispatchOverlayBitAsync(string which, string payload)
    {
        ToggleResult requested = ParseToggle(payload);
        StatusOverlay overlay;
        lock (_overlayLock)
        {
            (bool logo, bool black, bool clear) = (_logoState, _blackState, _clearState);
            bool newValue = requested switch
            {
                ToggleResult.On => true,
                ToggleResult.Off => false,
                _ => which switch
                {
                    "logo" => !logo,
                    "black" => !black,
                    "clear" => !clear,
                    _ => false,
                },
            };
            switch (which)
            {
                case "logo": logo = newValue; break;
                case "black": black = newValue; break;
                case "clear": clear = newValue; break;
            }
            _logoState = logo;
            _blackState = black;
            _clearState = clear;
            overlay = new StatusOverlay(logo, black, clear);
        }
        return _client.SendStatusAsync(overlay);
    }

    private static int ParseIndex(string payload, string property)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return 0;
        }
        string trimmed = payload.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            return n;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(trimmed);
            return GetIntProperty(doc.RootElement, property);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static int GetIntProperty(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
        {
            return 0;
        }
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetInt32(),
            JsonValueKind.String when int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) => n,
            _ => 0,
        };
    }

    private static bool GetBoolProperty(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
        {
            return false;
        }
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.GetInt32() != 0,
            JsonValueKind.String => ParseToggle(el.GetString() ?? "") == ToggleResult.On,
            _ => false,
        };
    }

    private static ToggleResult ParseToggle(string payload) =>
        payload.Trim() switch
        {
            "ON" or "on" or "1" or "true" or "True" => ToggleResult.On,
            "OFF" or "off" or "0" or "false" or "False" => ToggleResult.Off,
            "TOGGLE" or "toggle" => ToggleResult.Toggle,
            _ => ToggleResult.Toggle,
        };

    private enum ToggleResult { On, Off, Toggle }
}
