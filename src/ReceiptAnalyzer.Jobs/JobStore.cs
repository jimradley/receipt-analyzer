using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReceiptAnalyzer.Jobs;

/// <summary>
/// Durable, file-backed store for <see cref="AnalysisJob"/> records and their source images,
/// under <c>{outputDir}/.state/jobs/</c>. Job ids are the SHA-256 of the image bytes, giving
/// free idempotency: the same receipt always maps to the same job.
/// </summary>
public sealed class JobStore
{
    private readonly string _jobsDir;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public JobStore(string outputDir)
    {
        _jobsDir = Path.Combine(outputDir, ".state", "jobs");
        Directory.CreateDirectory(_jobsDir);
    }

    public static string ComputeId(byte[] imageBytes) =>
        Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();

    private string JobPath(string id) => Path.Combine(_jobsDir, id + ".json");
    private string ImagePath(string id) => Path.Combine(_jobsDir, id + ".img");

    /// <summary>Returns the existing job for this image, or creates and persists a fresh Queued one.</summary>
    public (AnalysisJob Job, bool Created) GetOrCreate(byte[] imageBytes, string mediaType)
    {
        var id = ComputeId(imageBytes);
        var existing = Get(id);
        if (existing is not null) return (existing, false);

        var job = new AnalysisJob { Id = id, MediaType = mediaType };
        File.WriteAllBytes(ImagePath(id), imageBytes);
        Save(job);
        return (job, true);
    }

    public AnalysisJob? Get(string id)
    {
        lock (_gate)
        {
        var path = JobPath(id);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<AnalysisJob>(File.ReadAllText(path), JsonOptions);
        }
    }

    public byte[]? GetImage(string id)
    {
        var path = ImagePath(id);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Drops the stored source image — called once a job is terminal and can no longer resume.</summary>
    public void DeleteImage(string id)
    {
        var path = ImagePath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Deletes terminal job records (and any leftover images) older than the retention window. Returns the count removed.</summary>
    public int PruneTerminal(TimeSpan retention)
    {
        if (!Directory.Exists(_jobsDir)) return 0;
        var cutoff = DateTimeOffset.UtcNow - retention;
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(_jobsDir, "*.json"))
        {
            AnalysisJob? job;
            try { job = JsonSerializer.Deserialize<AnalysisJob>(File.ReadAllText(path), JsonOptions); }
            catch { continue; }
            if (job is null || !job.IsTerminal || job.UpdatedAt > cutoff) continue;

            File.Delete(path);
            DeleteImage(job.Id);
            removed++;
        }
        return removed;
    }

    public void Save(AnalysisJob job)
    {
        lock (_gate)
        {
        job.UpdatedAt = DateTimeOffset.UtcNow;
        var path = JobPath(job.Id);
        var tmp = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(job, JsonOptions));
        File.Move(tmp, path, true);
        }
    }

    /// <summary>Non-terminal jobs (Queued/Running) — re-enqueued on startup so they resume.</summary>
    public IReadOnlyList<AnalysisJob> Resumable()
    {
        if (!Directory.Exists(_jobsDir)) return [];
        return Directory.EnumerateFiles(_jobsDir, "*.json")
            .Select(p => JsonSerializer.Deserialize<AnalysisJob>(File.ReadAllText(p), JsonOptions))
            .Where(j => j is not null && !j.IsTerminal)
            .Select(j => j!)
            .ToList();
    }
}
