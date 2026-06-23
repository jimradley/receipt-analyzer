using ReceiptAnalyzer.Jobs;

namespace ReceiptAnalyzer.Tests;

public class JobStoreTests : IDisposable
{
    private readonly string _dir;

    public JobStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ra-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] Img(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void GetOrCreate_is_idempotent_for_identical_images()
    {
        var store = new JobStore(_dir);
        var bytes = Img("receipt-bytes");

        var (first, created1) = store.GetOrCreate(bytes, "image/jpeg");
        var (second, created2) = store.GetOrCreate(bytes, "image/jpeg");

        Assert.True(created1);
        Assert.False(created2);                 // same image → existing job, no new run
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void Different_images_get_different_ids()
    {
        var store = new JobStore(_dir);
        var (a, _) = store.GetOrCreate(Img("one"), "image/jpeg");
        var (b, _) = store.GetOrCreate(Img("two"), "image/jpeg");
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Save_and_Get_round_trip_with_image()
    {
        var store = new JobStore(_dir);
        var (job, _) = store.GetOrCreate(Img("abc"), "image/png");
        job.Status = JobStatus.Completed;
        job.Markdown = "# done";
        store.Save(job);

        var reloaded = store.Get(job.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(JobStatus.Completed, reloaded!.Status);
        Assert.Equal("# done", reloaded.Markdown);
        Assert.Equal(Img("abc"), store.GetImage(job.Id));
    }

    [Fact]
    public void PruneTerminal_removes_old_terminal_jobs_only()
    {
        var store = new JobStore(_dir);

        var (old, _) = store.GetOrCreate(Img("old-done"), "image/jpeg");
        old.Status = JobStatus.Completed;
        old.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-30);
        store.Save(old);
        // Save() refreshes UpdatedAt to now, so re-persist the backdated record verbatim.
        BackdateSavedRecord(old.Id, DateTimeOffset.UtcNow.AddDays(-30));

        var (recent, _) = store.GetOrCreate(Img("recent-done"), "image/jpeg");
        recent.Status = JobStatus.Completed;
        store.Save(recent);

        var (queued, _) = store.GetOrCreate(Img("queued"), "image/jpeg"); // non-terminal, backdated
        BackdateSavedRecord(queued.Id, DateTimeOffset.UtcNow.AddDays(-30));

        var removed = store.PruneTerminal(TimeSpan.FromDays(14));

        Assert.Equal(1, removed);
        Assert.Null(store.Get(old.Id));        // old + terminal → pruned
        Assert.NotNull(store.Get(recent.Id));  // recent → kept
        Assert.NotNull(store.Get(queued.Id));  // non-terminal → kept regardless of age
    }

    [Fact]
    public void DeleteImage_removes_the_source_image()
    {
        var store = new JobStore(_dir);
        var (job, _) = store.GetOrCreate(Img("img"), "image/jpeg");
        Assert.NotNull(store.GetImage(job.Id));

        store.DeleteImage(job.Id);
        Assert.Null(store.GetImage(job.Id));
    }

    // Rewrites a job's JSON with a backdated UpdatedAt (Save() always stamps "now").
    private void BackdateSavedRecord(string id, DateTimeOffset when)
    {
        var path = Path.Combine(_dir, ".state", "jobs", id + ".json");
        var json = File.ReadAllText(path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["updatedAt"] = when;
        File.WriteAllText(path, node.ToJsonString());
    }

    [Fact]
    public void Resumable_returns_only_non_terminal_jobs()
    {
        var store = new JobStore(_dir);
        var (queued, _) = store.GetOrCreate(Img("q"), "image/jpeg");          // Queued
        var (done, _) = store.GetOrCreate(Img("d"), "image/jpeg");
        done.Status = JobStatus.Completed; store.Save(done);
        var (failed, _) = store.GetOrCreate(Img("f"), "image/jpeg");
        failed.Status = JobStatus.Failed; store.Save(failed);

        var resumable = store.Resumable();

        Assert.Single(resumable);
        Assert.Equal(queued.Id, resumable[0].Id);
    }
}
