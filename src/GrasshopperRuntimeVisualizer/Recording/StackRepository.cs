using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GrasshopperRuntimeVisualizer.Recording;

public sealed class StackRepository
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, StackRecord> _byHash = new();
    private int _nextId;

    public StackRepository(string path)
    {
        _path = path;
    }

    public int CaptureStackId()
    {
        var frames = new StackTrace(2, false)
            .GetFrames()?
            .Select(frame =>
            {
                var method = frame.GetMethod();
                var type = method?.DeclaringType?.FullName ?? "";
                return $"{type}.{method?.Name}";
            })
            .Where(frame => !string.IsNullOrWhiteSpace(frame))
            .Take(48)
            .ToArray() ?? [];

        var canonical = string.Join("\n", frames);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        lock (_gate)
        {
            if (_byHash.TryGetValue(hash, out var existing))
            {
                existing.Count++;
                return existing.Id;
            }

            var record = new StackRecord(++_nextId, hash, frames, 1);
            _byHash[hash] = record;
            return record.Id;
        }
    }

    public void Flush()
    {
        StackRecord[] records;
        lock (_gate)
        {
            records = _byHash.Values.OrderBy(record => record.Id).ToArray();
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record StackRecord(int Id, string Hash, string[] Frames, int Count)
    {
        public int Count { get; set; } = Count;
    }
}
