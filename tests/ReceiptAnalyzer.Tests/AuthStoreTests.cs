using ReceiptAnalyzer.Api.Auth;

namespace ReceiptAnalyzer.Tests;

public class AuthStoreTests : IDisposable
{
    private readonly string _dir;

    public AuthStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ra-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static StoredCredential Cred(byte id, uint signCount = 0) =>
        new(new byte[] { id, 2, 3 }, new byte[] { 9, 9 }, new byte[] { 7 }, signCount, "phone", Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Fact]
    public void Add_then_load_round_trips_through_disk()
    {
        var store = new AuthStore(_dir);
        Assert.False(store.Any());

        store.Add(Cred(1));

        var reloaded = new AuthStore(_dir);
        Assert.True(reloaded.Any());
        Assert.Single(reloaded.All());
    }

    [Fact]
    public void Find_matches_by_credential_id_bytes()
    {
        var store = new AuthStore(_dir);
        store.Add(Cred(1));
        store.Add(Cred(2));

        Assert.NotNull(store.Find(new byte[] { 1, 2, 3 }));
        Assert.Null(store.Find(new byte[] { 4, 2, 3 }));
    }

    [Fact]
    public void Add_replaces_an_existing_credential_with_the_same_id()
    {
        var store = new AuthStore(_dir);
        store.Add(Cred(1, signCount: 5));
        store.Add(Cred(1, signCount: 9));

        Assert.Single(store.All());
        Assert.Equal(9u, store.Find(new byte[] { 1, 2, 3 })!.SignCount);
    }

    [Fact]
    public void UpdateSignCount_persists_the_new_counter()
    {
        var store = new AuthStore(_dir);
        store.Add(Cred(1, signCount: 1));

        store.UpdateSignCount(new byte[] { 1, 2, 3 }, 42);

        Assert.Equal(42u, new AuthStore(_dir).Find(new byte[] { 1, 2, 3 })!.SignCount);
    }
}
