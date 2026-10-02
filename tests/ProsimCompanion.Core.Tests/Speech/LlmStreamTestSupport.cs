using System.Net;
using System.Text;
using System.Text.Json;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>A response body that arrives in exactly the byte chunks the test scripts — one
/// chunk per read — and then ends, hangs until cancelled, or breaks.</summary>
internal sealed class ScriptedStream : Stream
{
    public enum Ending
    {
        Close,
        Hang,
        Break,
    }

    private readonly Queue<byte[]> _chunks;
    private readonly Ending _ending;
    private readonly TimeSpan _delay;

    public ScriptedStream(IEnumerable<byte[]> chunks, Ending ending = Ending.Close, TimeSpan delay = default)
    {
        _chunks = new Queue<byte[]>(chunks);
        _ending = ending;
        _delay = delay;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, cancellationToken);
        }

        if (_chunks.Count > 0)
        {
            var chunk = _chunks.Dequeue();
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        switch (_ending)
        {
            case Ending.Hang:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            case Ending.Break:
                throw new IOException("connection reset");
            default:
                return 0;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A chat endpoint that answers with a scripted streamed body.</summary>
internal sealed class StreamHandler(Func<Stream> body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(status) { Content = new StreamContent(body()) };
    }
}

internal static class LlmWire
{
    /// <summary>An OpenAI-shaped SSE body: a role-only chunk, one data line per delta, a
    /// keep-alive comment, then <c>[DONE]</c>.</summary>
    public static string Sse(params string[] deltas)
    {
        var sb = new StringBuilder();
        sb.Append("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n");
        foreach (var delta in deltas)
        {
            sb.Append("data: ").Append(JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = delta } } },
            })).Append("\n\n");
        }

        sb.Append(": keep-alive\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    /// <summary>An Ollama-shaped NDJSON body: one object per delta, then <c>done: true</c>.</summary>
    public static string Ndjson(params string[] deltas)
    {
        var sb = new StringBuilder();
        foreach (var delta in deltas)
        {
            sb.Append(JsonSerializer.Serialize(new { message = new { role = "assistant", content = delta }, done = false }))
                .Append('\n');
        }

        sb.Append("{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n");
        return sb.ToString();
    }

    /// <summary>The whole body as one chunk.</summary>
    public static Stream Whole(string body) => new ScriptedStream([Encoding.UTF8.GetBytes(body)]);

    /// <summary>The body cut into chunks of <paramref name="size"/> BYTES — through JSON
    /// objects and through multi-byte characters alike.</summary>
    public static Stream Sliced(string body, int size, ScriptedStream.Ending ending = ScriptedStream.Ending.Close)
        => new ScriptedStream(Encoding.UTF8.GetBytes(body).Chunk(size), ending);
}

/// <summary>
/// An arbiter that behaves like the real one for a streamed item: it speaks the segments in
/// order (marking first audio, spoken, starved), and can be told to take its time per segment
/// or to cut the item short. A non-stream request is recorded and resolves Spoken.
/// </summary>
internal sealed class DrainingArbiter : ISpeechArbiter
{
    private readonly object _gate = new();

    public List<SpeechRequest> Requests { get; } = [];

    /// <summary>Every segment spoken, with its cache flag.</summary>
    public List<SpeechSegment> Segments { get; } = [];

    /// <summary>How long one segment takes to speak.</summary>
    public TimeSpan SegmentDuration { get; set; } = TimeSpan.Zero;

    /// <summary>Cut the item (as a Critical would) once this many segments were spoken.</summary>
    public int? AbortAfterSegments { get; set; }

    public event Action<SpeechArbiterEvent>? Observed
    {
        add { }
        remove { }
    }

    public Task<SpeechOutcome> EnqueueAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Requests.Add(request);
        }

        return request.Stream is { } stream
            ? Task.Run(() => DrainAsync(stream, cancellationToken), CancellationToken.None)
            : Task.FromResult(SpeechOutcome.Spoken);
    }

    public Task<SpeechOutcome> SpeakAsync(
        string text, SpeechPriority priority = SpeechPriority.Normal, CancellationToken cancellationToken = default)
        => EnqueueAsync(new SpeechRequest(text, priority), cancellationToken);

    private async Task<SpeechOutcome> DrainAsync(StreamedUtterance stream, CancellationToken cancellationToken)
    {
        var spoken = 0;
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stream.Close(aborted: true);
                return SpeechOutcome.Dropped;
            }

            if (!stream.Reader.TryRead(out var segment))
            {
                stream.MarkStarved(DateTimeOffset.UtcNow);
                using var poll = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
                try
                {
                    if (!await stream.Reader.WaitToReadAsync(poll.Token))
                    {
                        stream.Close(aborted: false);
                        return SpeechOutcome.Spoken;
                    }
                }
                catch (OperationCanceledException)
                {
                    // poll tick
                }

                continue;
            }

            stream.MarkFed();
            if (spoken == 0)
            {
                stream.MarkFirstAudio(DateTimeOffset.UtcNow);
            }

            if (SegmentDuration > TimeSpan.Zero)
            {
                await Task.Delay(SegmentDuration, CancellationToken.None);
            }

            spoken++;
            stream.MarkSpoken(segment.Text);
            lock (_gate)
            {
                Segments.Add(segment);
            }

            if (AbortAfterSegments == spoken)
            {
                stream.Close(aborted: true);
                return SpeechOutcome.Superseded;
            }
        }
    }
}
