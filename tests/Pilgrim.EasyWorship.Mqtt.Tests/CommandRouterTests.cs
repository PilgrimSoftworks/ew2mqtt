using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pilgrim.EasyWorship;
using Pilgrim.EasyWorship.Events;

namespace Pilgrim.EasyWorship.Mqtt.Tests;

public sealed class CommandRouterTests
{
    [Test]
    public async Task NextSlide_topic_invokes_SendNextSlideAsync()
    {
        IEasyWorshipClient client = Substitute.For<IEasyWorshipClient>();
        Topics topics = new("ew2mqtt", "default");
        CommandRouter router = new(topics, client, NullLogger.Instance);

        await router.RouteAsync("ew2mqtt/default/cmd/nextSlide", "");

        await client.Received(1).SendNextSlideAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GotoSlide_with_int_payload_calls_SendGotoSlideAsync()
    {
        IEasyWorshipClient client = Substitute.For<IEasyWorshipClient>();
        Topics topics = new("ew2mqtt", "default");
        CommandRouter router = new(topics, client, NullLogger.Instance);

        await router.RouteAsync("ew2mqtt/default/cmd/gotoSlide", "5");

        await client.Received(1).SendGotoSlideAsync(5, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GotoSlide_with_json_payload_calls_SendGotoSlideAsync()
    {
        IEasyWorshipClient client = Substitute.For<IEasyWorshipClient>();
        Topics topics = new("ew2mqtt", "default");
        CommandRouter router = new(topics, client, NullLogger.Instance);

        await router.RouteAsync("ew2mqtt/default/cmd/gotoSlide", """{"slide":12}""");

        await client.Received(1).SendGotoSlideAsync(12, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Overlay_atomic_topic_calls_SendStatusAsync_with_combined_overlay()
    {
        IEasyWorshipClient client = Substitute.For<IEasyWorshipClient>();
        Topics topics = new("ew2mqtt", "default");
        CommandRouter router = new(topics, client, NullLogger.Instance);

        await router.RouteAsync("ew2mqtt/default/cmd/overlay", """{"logo":true,"black":false,"clear":true}""");

        await client.Received(1).SendStatusAsync(
            Arg.Is<StatusOverlay>(o => o.Logo && !o.Black && o.Clear),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Overlay_bit_ON_sets_only_that_bit_preserving_others()
    {
        IEasyWorshipClient client = Substitute.For<IEasyWorshipClient>();
        Topics topics = new("ew2mqtt", "default");
        CommandRouter router = new(topics, client, NullLogger.Instance);

        client.StatusReceived += Raise.Event<EventHandler<EwStatusEvent>>(
            client,
            new EwStatusEvent(Logo: true, Black: false, Clear: false,
                PresRowId: 0, SlideRowId: 0, PresNo: 0, SlideNo: 0,
                ScheduleRev: 0, LiveRev: 0, ImageHash: "", RequestRev: 0));

        await router.RouteAsync("ew2mqtt/default/cmd/overlay/black", "ON");

        await client.Received(1).SendStatusAsync(
            Arg.Is<StatusOverlay>(o => o.Logo && o.Black && !o.Clear),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Envelope_topic_dispatches_action_field()
    {
        IEasyWorshipClient client = Substitute.For<IEasyWorshipClient>();
        Topics topics = new("ew2mqtt", "default");
        CommandRouter router = new(topics, client, NullLogger.Instance);

        await router.RouteAsync("ew2mqtt/default/cmd", """{"action":"prevSlide"}""");

        await client.Received(1).SendPrevSlideAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Topic_outside_command_root_is_ignored()
    {
        IEasyWorshipClient client = Substitute.For<IEasyWorshipClient>();
        Topics topics = new("ew2mqtt", "default");
        CommandRouter router = new(topics, client, NullLogger.Instance);

        await router.RouteAsync("other/topic/something", "hello");

        await client.DidNotReceiveWithAnyArgs().SendNextSlideAsync(default);
    }

}
