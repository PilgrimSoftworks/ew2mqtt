using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;

using Pilgrim.EasyWorship.Wire;

namespace Pilgrim.EasyWorship.Tests.Wire;

public sealed class EzwReadLoopTests
{
    [Test]
    public async Task Splits_multiple_frames_in_one_buffer()
    {
        (Pipe? pipe, Channel<EzwInboundFrame>? channel, Task? task) = StartLoop();
        byte[] bytes = Encoding.UTF8.GetBytes("""{"action":"paired"}""" + "\r\n" + """{"action":"heartbeat","requestrev":"3"}""" + "\r\n");

        await pipe.Writer.WriteAsync(bytes);
        await pipe.Writer.CompleteAsync();

        List<EzwInboundFrame> frames = await CollectAsync(channel.Reader);
        await task;

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].Message.Action).IsEqualTo("paired");
        await Assert.That(frames[1].Message.Action).IsEqualTo("heartbeat");
        await Assert.That(frames[1].Message.RequestRev).IsEqualTo(3);
    }

    [Test]
    public async Task Reassembles_frames_split_across_writes()
    {
        (Pipe? pipe, Channel<EzwInboundFrame>? channel, Task? task) = StartLoop();

        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("""{"action":"sta"""));
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("""tus","logo":1,"black":0,"clear":0}""" + "\r\n"));
        await pipe.Writer.CompleteAsync();

        List<EzwInboundFrame> frames = await CollectAsync(channel.Reader);
        await task;

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(frames[0].Message.Action).IsEqualTo("status");
        await Assert.That(frames[0].Message.Logo).IsTrue();
    }

    [Test]
    public async Task Captures_size_payload_bytes_after_json()
    {
        (Pipe? pipe, Channel<EzwInboundFrame>? channel, Task? task) = StartLoop();
        string json1 = """{"action":"LiveData","size":5,"requestrev":"1"}""";
        string json2 = """{"action":"paired"}""";
        byte[] payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };

        List<byte> buf = new();
        buf.AddRange(Encoding.UTF8.GetBytes(json1));
        buf.AddRange(Encoding.UTF8.GetBytes("\r\n"));
        buf.AddRange(payload);
        buf.AddRange(Encoding.UTF8.GetBytes(json2));
        buf.AddRange(Encoding.UTF8.GetBytes("\r\n"));

        await pipe.Writer.WriteAsync(buf.ToArray());
        await pipe.Writer.CompleteAsync();

        List<EzwInboundFrame> frames = await CollectAsync(channel.Reader);
        await task;

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].Message.Action).IsEqualTo("LiveData");
        await Assert.That(frames[0].Message.Size).IsEqualTo(5L);
        await Assert.That(frames[0].Payload).IsEquivalentTo(payload);
        await Assert.That(frames[1].Message.Action).IsEqualTo("paired");
        await Assert.That(frames[1].Payload.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Captures_payload_when_split_across_reads()
    {
        (Pipe? pipe, Channel<EzwInboundFrame>? channel, Task? task) = StartLoop();
        string json1 = """{"action":"LiveData","size":10,"requestrev":"1"}""";
        string json2 = """{"action":"status"}""";

        byte[] firstChunk = Encoding.UTF8.GetBytes(json1 + "\r\n");
        byte[] payloadBytes = new byte[10];
        for (int i = 0; i < 10; i++) payloadBytes[i] = (byte)(i + 1);
        byte[] thirdChunk = Encoding.UTF8.GetBytes(json2 + "\r\n");

        await pipe.Writer.WriteAsync(firstChunk);
        await pipe.Writer.WriteAsync(payloadBytes[..4]);
        await pipe.Writer.WriteAsync(payloadBytes[4..]);
        await pipe.Writer.WriteAsync(thirdChunk);
        await pipe.Writer.CompleteAsync();

        List<EzwInboundFrame> frames = await CollectAsync(channel.Reader);
        await task;

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].Payload).IsEquivalentTo(payloadBytes);
        await Assert.That(frames[1].Message.Action).IsEqualTo("status");
    }

    [Test]
    public async Task Malformed_json_emits_malformed_frame_without_crashing_loop()
    {
        (Pipe? pipe, Channel<EzwInboundFrame>? channel, Task? task) = StartLoop();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("not-json\r\n" + """{"action":"paired"}""" + "\r\n"));
        await pipe.Writer.CompleteAsync();

        List<EzwInboundFrame> frames = await CollectAsync(channel.Reader);
        await task;

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].Message.Action).IsEqualTo("_malformed");
        await Assert.That(frames[1].Message.Action).IsEqualTo("paired");
    }

    [Test]
    public async Task Numeric_overflow_in_a_field_does_not_crash_the_loop()
    {
        // Regression: real EW frames ship liverev > Int32.MaxValue. Earlier versions
        // promoted these fields' types; a stray int conversion would fault the loop.
        (Pipe? pipe, Channel<EzwInboundFrame>? channel, Task? task) = StartLoop();
        string json = """{"action":"status","liverev":99999999999999,"schedulerev":88888888888888,"requestrev":"5"}""";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(json + "\r\n"));
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("""{"action":"paired"}""" + "\r\n"));
        await pipe.Writer.CompleteAsync();

        List<EzwInboundFrame> frames = await CollectAsync(channel.Reader);
        await task;

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].Message.Action).IsEqualTo("status");
        await Assert.That(frames[0].Message.LiveRev).IsEqualTo(99999999999999L);
        await Assert.That(frames[1].Message.Action).IsEqualTo("paired");
    }

    [Test]
    public async Task Frame_exceeding_max_size_throws()
    {
        (Pipe? pipe, Channel<EzwInboundFrame>? channel, Task? task) = StartLoop();
        byte[] huge = new byte[(int)(EzwLineFramer.MaxFrameSize + 100)];
        Array.Fill(huge, (byte)'a');

        await pipe.Writer.WriteAsync(huge);
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));
        await pipe.Writer.CompleteAsync();

        await Assert.That(async () => await task).ThrowsExactly<InvalidDataException>();
        _ = channel; // suppress unused
    }

    private static (Pipe Pipe, Channel<EzwInboundFrame> Channel, Task Task) StartLoop()
    {
        Pipe pipe = new();
        Channel<EzwInboundFrame> channel = System.Threading.Channels.Channel.CreateUnbounded<EzwInboundFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        EzwReadLoop loop = new(pipe.Reader, channel.Writer);
        Task task = Task.Run(() => loop.RunAsync(CancellationToken.None));
        return (pipe, channel, task);
    }

    private static async Task<List<EzwInboundFrame>> CollectAsync(ChannelReader<EzwInboundFrame> reader)
    {
        List<EzwInboundFrame> list = new();
        await foreach (EzwInboundFrame frame in reader.ReadAllAsync())
        {
            list.Add(frame);
        }
        return list;
    }
}
