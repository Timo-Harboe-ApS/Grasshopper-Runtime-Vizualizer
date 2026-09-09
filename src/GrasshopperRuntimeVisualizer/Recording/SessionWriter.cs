using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using GrasshopperRuntimeVisualizer.Streaming;

namespace GrasshopperRuntimeVisualizer.Recording;

public sealed class SessionWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly HttpVisualizerServer _server;
    private readonly Channel<Dictionary<string, object?>> _channel;
    private readonly Task _writerTask;
    private readonly StreamWriter _events;
    private long _sequence;
    private bool _disposed;

    private SessionWriter(string sessionFolder, string sessionName, HttpVisualizerServer server)
    {
        SessionFolder = sessionFolder;
        SessionName = sessionName;
        _server = server;
        _events = new StreamWriter(File.Open(Path.Combine(sessionFolder, "events.jsonl"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
        _channel = Channel.CreateUnbounded<Dictionary<string, object?>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _writerTask = Task.Run(WriteLoop);
    }

    public string SessionFolder { get; }
    public string SessionName { get; }
    public int EventCount => (int)Interlocked.Read(ref _sequence);

    public static SessionWriter Create(string sessionsRoot, HttpVisualizerServer server)
    {
        Directory.CreateDirectory(sessionsRoot);
        var baseName = $"session_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        var name = baseName;
        var index = 1;

        while (Directory.Exists(Path.Combine(sessionsRoot, name)))
        {
            name = $"{baseName}_{index++}";
        }

        var folder = Path.Combine(sessionsRoot, name);
        Directory.CreateDirectory(folder);
        return new SessionWriter(folder, name, server);
    }

    public void Enqueue(string type, double timeSeconds, params (string Key, object? Value)[] values)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var evt = new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["t"] = Math.Round(timeSeconds, 6),
            ["type"] = type,
            ["session"] = SessionName
        };

        foreach (var (key, value) in values)
        {
            if (value is not null)
            {
                evt[key] = value;
            }
        }

        _channel.Writer.TryWrite(evt);
    }

    public void WriteMetadata(string status, bool captureStacks)
    {
        var metadata = new
        {
            session = SessionName,
            status,
            pluginVersion = typeof(SessionWriter).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion,
            recorderModel = "native-top-level-senders; raw-expire-callbacks-plus-canvas-expire-phase-transitions",
            captureStacks,
            eventCount = EventCount,
            writtenAt = DateTimeOffset.Now,
            format = 1
        };

        File.WriteAllText(Path.Combine(SessionFolder, "metadata.json"), JsonSerializer.Serialize(metadata, JsonOptions));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();

        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best effort shutdown. The metadata status still marks interrupted sessions.
        }

        _events.Flush();
        _events.Dispose();
    }

    private async Task WriteLoop()
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync())
        {
            await _events.WriteLineAsync(JsonSerializer.Serialize(evt, JsonOptions));
            await _events.FlushAsync();
            _server.Broadcast(evt);
        }

        await _events.FlushAsync();
    }
}
