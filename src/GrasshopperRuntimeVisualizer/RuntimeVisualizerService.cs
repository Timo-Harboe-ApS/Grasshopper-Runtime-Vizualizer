using System.Diagnostics;
using System.Reflection;
using Grasshopper.Kernel;
using GrasshopperRuntimeVisualizer.Recording;
using GrasshopperRuntimeVisualizer.Streaming;

namespace GrasshopperRuntimeVisualizer;

public sealed class RuntimeVisualizerService
{
    public static RuntimeVisualizerService Instance { get; } = new();

    private readonly object _gate = new();
    private readonly string _sessionsRoot;
    private readonly DebugLog _log;
    private HttpVisualizerServer? _server;
    private RuntimeRecorder? _recorder;
    private bool _debugMode;

    private RuntimeVisualizerService()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _sessionsRoot = Path.Combine(documents, "GrasshopperRuntimeVisualizer", "Sessions");
        Directory.CreateDirectory(_sessionsRoot);
        _log = new DebugLog(Path.Combine(documents, "GrasshopperRuntimeVisualizer", "RuntimeVisualizer.log"));
    }

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _recorder is not null;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (_gate)
            {
                if (_recorder is not null)
                {
                    return $"Recording: {_recorder.SessionName}";
                }

                return LatestSessionFolder() is null ? "Idle" : "Idle: latest session available";
            }
        }
    }

    public int EventCount
    {
        get
        {
            lock (_gate)
            {
                return _recorder?.EventCount ?? 0;
            }
        }
    }

    public string VisualizerUrl => EnsureServer().Url;
    public string DebugUrl => EnsureServer().DebugUrl;
    public string LogPath => _log.Path;

    public string? CurrentOrLatestSessionFolder
    {
        get
        {
            lock (_gate)
            {
                return _recorder?.SessionFolder ?? LatestSessionFolder();
            }
        }
    }

    public void SetDebugMode(bool enabled)
    {
        _debugMode = enabled;
        _log.Enabled = enabled;
        _server?.SetDebugMode(enabled);
        if (enabled)
        {
            _log.Write("Debug mode enabled.");
            _log.Write(BuildDiagnosticsText());
        }
    }

    public void StartRecording(GH_Document document, bool captureStacks)
    {
        lock (_gate)
        {
            if (_recorder is not null)
            {
                return;
            }

            var server = EnsureServer();
            _recorder = RuntimeRecorder.Start(document, _sessionsRoot, captureStacks, server);
            _log.Write($"Started recording session '{_recorder.SessionName}'.");
        }
    }

    public void StopRecording(string status)
    {
        RuntimeRecorder? recorder;

        lock (_gate)
        {
            recorder = _recorder;
            _recorder = null;
        }

        recorder?.Stop(status);
        _log.Write($"Stopped recording with status '{status}'.");
    }

    public void OpenVisualizer()
    {
        var url = VisualizerUrl;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Rhino output still shows the URL through the component output.
            _log.Write($"Failed to open visualizer automatically. Use {url}");
        }
    }

    public string BuildDiagnosticsText()
    {
        var diagnostics = BuildDiagnostics();
        return string.Join(Environment.NewLine, diagnostics.Select(pair => $"{pair.Key}: {pair.Value}"));
    }

    private HttpVisualizerServer EnsureServer()
    {
        if (_server is not null)
        {
            return _server;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var assemblyFolder = Path.GetDirectoryName(assembly.Location)
            ?? AppContext.BaseDirectory;
        var browserRoots = new[]
        {
            Path.Combine(assemblyFolder, "Resources", "browser"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "browser"),
            Path.Combine(Environment.CurrentDirectory, "Resources", "browser")
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        _server = HttpVisualizerServer.Start(
            browserRoots,
            _sessionsRoot,
            ReadEmbeddedIndex,
            BuildDiagnostics,
            _log);
        _server.SetDebugMode(_debugMode);
        _log.Write($"Started local server at {_server.Url}");
        return _server;
    }

    private string? ReadEmbeddedIndex()
    {
        const string name = "GrasshopperRuntimeVisualizer.Resources.browser.index.html";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private Dictionary<string, object?> BuildDiagnostics()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var assemblyFolder = Path.GetDirectoryName(assembly.Location) ?? "";
        var roots = new[]
        {
            Path.Combine(assemblyFolder, "Resources", "browser"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "browser"),
            Path.Combine(Environment.CurrentDirectory, "Resources", "browser")
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        return new Dictionary<string, object?>
        {
            ["status"] = Status,
            ["pluginVersion"] = assembly
                .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .OfType<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion,
            ["visualizerUrl"] = _server?.Url,
            ["debugUrl"] = _server?.DebugUrl,
            ["assemblyLocation"] = assembly.Location,
            ["assemblyFolder"] = assemblyFolder,
            ["appContextBaseDirectory"] = AppContext.BaseDirectory,
            ["currentDirectory"] = Environment.CurrentDirectory,
            ["sessionsRoot"] = _sessionsRoot,
            ["sessionsRootExists"] = Directory.Exists(_sessionsRoot),
            ["latestSession"] = LatestSessionFolder(),
            ["browserRoots"] = roots.Select(root => new
            {
                path = root,
                exists = Directory.Exists(root),
                indexExists = File.Exists(Path.Combine(root, "index.html"))
            }).ToArray(),
            ["embeddedIndexExists"] = Assembly.GetExecutingAssembly().GetManifestResourceStream("GrasshopperRuntimeVisualizer.Resources.browser.index.html") is not null,
            ["logPath"] = _log.Path,
            ["debugMode"] = _debugMode
        };
    }

    private string? LatestSessionFolder()
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(_sessionsRoot)
            .OrderByDescending(Path.GetFileName)
            .FirstOrDefault();
    }
}
