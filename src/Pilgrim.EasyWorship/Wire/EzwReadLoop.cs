using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Pilgrim.EasyWorship.Wire;

internal sealed class EzwReadLoop
{
    public const long MaxPayloadSize = 16L * 1024 * 1024;

    private readonly PipeReader _reader;
    private readonly ChannelWriter<EzwInboundFrame> _output;
    private EzwInboundMessage? _pendingMessage;
    private string? _pendingRaw;
    private byte[]? _pendingPayload;
    private int _pendingFilled;

    public EzwReadLoop(PipeReader reader, ChannelWriter<EzwInboundFrame> output)
    {
        _reader = reader;
        _output = output;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult read = await _reader.ReadAsync(ct).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = read.Buffer;

                buffer = await ConsumePendingPayloadAsync(buffer, ct).ConfigureAwait(false);

                while (_pendingPayload is null && EzwLineFramer.TryReadLine(ref buffer, out ReadOnlySequence<byte> line))
                {
                    if (line.Length > EzwLineFramer.MaxFrameSize)
                    {
                        throw new InvalidDataException(
                            $"frame exceeds max size ({line.Length} > {EzwLineFramer.MaxFrameSize})");
                    }

                    (EzwInboundMessage? message, string? raw) = ParseFrame(line);
                    if (message.Size > 0)
                    {
                        if (message.Size > MaxPayloadSize)
                        {
                            throw new InvalidDataException(
                                $"payload exceeds max size ({message.Size} > {MaxPayloadSize})");
                        }
                        _pendingMessage = message;
                        _pendingRaw = raw;
                        _pendingPayload = new byte[(int)message.Size];
                        _pendingFilled = 0;
                        buffer = await ConsumePendingPayloadAsync(buffer, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await _output.WriteAsync(new EzwInboundFrame(message, raw, Array.Empty<byte>()), ct).ConfigureAwait(false);
                    }
                }

                _reader.AdvanceTo(buffer.Start, read.Buffer.End);
                if (read.IsCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            _output.TryComplete();
        }
    }

    private async ValueTask<ReadOnlySequence<byte>> ConsumePendingPayloadAsync(
        ReadOnlySequence<byte> buffer, CancellationToken ct)
    {
        if (_pendingPayload is null || buffer.Length == 0)
        {
            return buffer;
        }

        int need = _pendingPayload.Length - _pendingFilled;
        int take = (int)Math.Min(need, buffer.Length);
        buffer.Slice(0, take).CopyTo(_pendingPayload.AsSpan(_pendingFilled, take));
        _pendingFilled += take;
        buffer = buffer.Slice(take);

        if (_pendingFilled == _pendingPayload.Length)
        {
            await _output.WriteAsync(
                new EzwInboundFrame(_pendingMessage!, _pendingRaw!, _pendingPayload),
                ct).ConfigureAwait(false);
            _pendingMessage = null;
            _pendingRaw = null;
            _pendingPayload = null;
            _pendingFilled = 0;
        }

        return buffer;
    }

    private static (EzwInboundMessage Message, string Raw) ParseFrame(ReadOnlySequence<byte> line)
    {
        byte[] bytes = line.IsSingleSegment ? line.FirstSpan.ToArray() : line.ToArray();
        string raw = DecodeText(bytes);
        try
        {
            EzwInboundMessage? message = JsonSerializer.Deserialize(bytes, EzwJsonContext.Default.EzwInboundMessage);
            return (message ?? new EzwInboundMessage(), raw);
        }
        catch (Exception ex) when (ex is JsonException or OverflowException or FormatException or InvalidOperationException)
        {
            return (new EzwInboundMessage { Action = "_malformed" }, raw);
        }
    }

    // Encoding.UTF8 uses the replacement fallback and never throws, so a strict
    // decoder is required for the Latin1 fallback to actually trigger on the
    // non-UTF-8 bytes EW can emit in some locales.
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}

internal readonly record struct EzwInboundFrame(EzwInboundMessage Message, string Raw, byte[] Payload);
