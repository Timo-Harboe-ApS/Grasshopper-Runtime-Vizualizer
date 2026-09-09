using System.Diagnostics;
using System.Threading;
using Grasshopper.Kernel;
using GrasshopperRuntimeVisualizer.Streaming;

namespace GrasshopperRuntimeVisualizer.Recording;

public sealed class RuntimeRecorder
{
    private readonly GH_Document _document;
    private readonly SessionWriter _writer;
    private readonly StackRepository? _stacks;
    private readonly HashSet<Guid> _subscribedObjects = new();
    private readonly Dictionary<Guid, GH_SolutionPhase> _knownPhases = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private int _solutionId;
    private bool _stopped;

    private RuntimeRecorder(GH_Document document, SessionWriter writer, StackRepository? stacks)
    {
        _document = document;
        _writer = writer;
        _stacks = stacks;
    }

    public string SessionName => _writer.SessionName;
    public string SessionFolder => _writer.SessionFolder;
    public int EventCount => _writer.EventCount;

    public static RuntimeRecorder Start(
        GH_Document document,
        string sessionsRoot,
        bool captureStacks,
        HttpVisualizerServer server)
    {
        var session = SessionWriter.Create(sessionsRoot, server);
        GraphSnapshot.Write(document, Path.Combine(session.SessionFolder, "graph.json"));
        session.WriteMetadata("recording", captureStacks);

        var stacks = captureStacks ? new StackRepository(Path.Combine(session.SessionFolder, "stacks.json")) : null;
        var recorder = new RuntimeRecorder(document, session, stacks);
        recorder.SubscribeDocument();
        recorder.Record("recording_start");
        return recorder;
    }

    public void Stop(string status)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }

        Record(status == "stopped" ? "recording_stop" : "recording_interrupted");
        UnsubscribeDocument();
        _stacks?.Flush();
        _writer.WriteMetadata(status, _stacks is not null);
        _writer.Dispose();
    }

    private void SubscribeDocument()
    {
        _document.SolutionStart += OnSolutionStart;
        _document.SolutionEnd += OnSolutionEnd;
        _document.ObjectsAdded += OnObjectsAdded;
        _document.ObjectsDeleted += OnObjectsDeleted;

        foreach (var obj in _document.Objects)
        {
            SubscribeObject(obj);
        }
    }

    private void UnsubscribeDocument()
    {
        _document.SolutionStart -= OnSolutionStart;
        _document.SolutionEnd -= OnSolutionEnd;
        _document.ObjectsAdded -= OnObjectsAdded;
        _document.ObjectsDeleted -= OnObjectsDeleted;

        foreach (var obj in _document.Objects)
        {
            UnsubscribeObject(obj);
        }
    }

    private void SubscribeObject(IGH_DocumentObject obj)
    {
        SubscribeSingleObject(obj);
    }

    private void SubscribeSingleObject(IGH_DocumentObject obj)
    {
        lock (_gate)
        {
            if (!_subscribedObjects.Add(obj.InstanceGuid))
            {
                return;
            }

            if (obj is IGH_ActiveObject active)
            {
                _knownPhases[obj.InstanceGuid] = active.Phase;
            }
        }

        obj.SolutionExpired += OnSolutionExpired;
        obj.ObjectChanged += OnObjectChanged;
    }

    private void UnsubscribeObject(IGH_DocumentObject obj)
    {
        UnsubscribeSingleObject(obj);
    }

    private void UnsubscribeSingleObject(IGH_DocumentObject obj)
    {
        lock (_gate)
        {
            _subscribedObjects.Remove(obj.InstanceGuid);
            _knownPhases.Remove(obj.InstanceGuid);
        }

        obj.SolutionExpired -= OnSolutionExpired;
        obj.ObjectChanged -= OnObjectChanged;
    }

    private void OnSolutionStart(object sender, GH_SolutionEventArgs e)
    {
        _solutionId++;
        Record("solution_start", ("solution", _solutionId));
    }

    private void OnSolutionEnd(object sender, GH_SolutionEventArgs e)
    {
        Record("solution_end", ("solution", _solutionId), ("durationMs", e.Duration.TotalMilliseconds));
        UpdateKnownPhases();
    }

    private void OnObjectsAdded(object sender, GH_DocObjectEventArgs e)
    {
        foreach (var obj in e.Objects)
        {
            SubscribeObject(obj);
            Record("object_added", ("object", obj.InstanceGuid), ("name", obj.Name), ("nickname", obj.NickName));
        }
    }

    private void OnObjectsDeleted(object sender, GH_DocObjectEventArgs e)
    {
        foreach (var obj in e.Objects)
        {
            Record("object_removed", ("object", obj.InstanceGuid), ("name", obj.Name), ("nickname", obj.NickName));
            UnsubscribeObject(obj);
        }
    }

    private void OnSolutionExpired(IGH_DocumentObject sender, GH_SolutionExpiredEventArgs e)
    {
        var time = _clock.Elapsed.TotalSeconds;
        var stackId = _stacks?.CaptureStackId();
        var phaseBefore = PhaseBefore(sender);
        var phaseAfter = sender is IGH_ActiveObject active ? active.Phase : (GH_SolutionPhase?)null;

        Record(
            "expire",
            time,
            ("object", sender.InstanceGuid),
            ("solution", _solutionId),
            ("thread", Thread.CurrentThread.ManagedThreadId),
            ("recompute", e.Recompute),
            ("stackId", stackId),
            ("phaseBefore", phaseBefore?.ToString()),
            ("phaseAfter", phaseAfter?.ToString()));

        if (phaseBefore is not null &&
            phaseBefore.Value != GH_SolutionPhase.Blank &&
            phaseAfter == GH_SolutionPhase.Blank)
        {
            Record(
                "canvas_expire",
                time,
                ("object", sender.InstanceGuid),
                ("solution", _solutionId),
                ("thread", Thread.CurrentThread.ManagedThreadId),
                ("recompute", e.Recompute),
                ("phaseBefore", phaseBefore.Value.ToString()),
                ("phaseAfter", phaseAfter.Value.ToString()));
        }

        if (phaseAfter is not null)
        {
            lock (_gate)
            {
                _knownPhases[sender.InstanceGuid] = phaseAfter.Value;
            }
        }
    }

    private void OnObjectChanged(IGH_DocumentObject sender, GH_ObjectChangedEventArgs e)
    {
        Record(
            "object_changed",
            ("object", sender.InstanceGuid),
            ("change", e.Type.ToString()),
            ("nickname", sender.NickName));
    }

    private void Record(string type, params (string Key, object? Value)[] values)
    {
        Record(type, _clock.Elapsed.TotalSeconds, values);
    }

    private void Record(string type, double time, params (string Key, object? Value)[] values)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
        }

        _writer.Enqueue(type, time, values);
    }

    private GH_SolutionPhase? PhaseBefore(IGH_DocumentObject obj)
    {
        if (obj is not IGH_ActiveObject active)
        {
            return null;
        }

        lock (_gate)
        {
            if (_knownPhases.TryGetValue(obj.InstanceGuid, out var phase))
            {
                return phase;
            }

            _knownPhases[obj.InstanceGuid] = active.Phase;
            return active.Phase;
        }
    }

    private void UpdateKnownPhases()
    {
        lock (_gate)
        {
            foreach (var obj in _document.Objects)
            {
                if (obj is IGH_ActiveObject active)
                {
                    _knownPhases[obj.InstanceGuid] = active.Phase;
                }
            }
        }
    }
}
