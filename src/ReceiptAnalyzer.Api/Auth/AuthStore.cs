using System.Text.Json;

namespace ReceiptAnalyzer.Api.Auth;

/// <summary>One enrolled passkey (WebAuthn credential) for the single app owner.</summary>
public sealed record StoredCredential(
    byte[] CredentialId,
    byte[] PublicKey,
    byte[] UserHandle,
    uint SignCount,
    string Nickname,
    Guid AaGuid,
    DateTimeOffset CreatedAt);

public sealed class AuthData
{
    public List<StoredCredential> Credentials { get; set; } = new();
}

/// <summary>
/// Durable, JSON-on-disk store of enrolled passkeys (<c>.state/auth/credentials.json</c>), matching the
/// <see cref="ReceiptAnalyzer.Ledger.PriceCacheStore"/> / <c>JobStore</c> pattern. Single logical owner;
/// multiple credentials = multiple devices.
/// </summary>
public sealed class AuthStore
{
    private readonly string _path;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public AuthStore(string outputDir)
        => _path = Path.Combine(outputDir, ".state", "auth", "credentials.json");

    public AuthData Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return new AuthData();
            return JsonSerializer.Deserialize<AuthData>(File.ReadAllText(_path), JsonOptions) ?? new AuthData();
        }
    }

    public void Save(AuthData data)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
    }

    public bool Any() => Load().Credentials.Count > 0;

    public IReadOnlyList<StoredCredential> All() => Load().Credentials;

    public void Add(StoredCredential credential)
    {
        var data = Load();
        data.Credentials.RemoveAll(c => c.CredentialId.SequenceEqual(credential.CredentialId));
        data.Credentials.Add(credential);
        Save(data);
    }

    public StoredCredential? Find(byte[] credentialId)
        => Load().Credentials.FirstOrDefault(c => c.CredentialId.SequenceEqual(credentialId));

    public void UpdateSignCount(byte[] credentialId, uint signCount)
    {
        var data = Load();
        var idx = data.Credentials.FindIndex(c => c.CredentialId.SequenceEqual(credentialId));
        if (idx < 0) return;
        data.Credentials[idx] = data.Credentials[idx] with { SignCount = signCount };
        Save(data);
    }
}
