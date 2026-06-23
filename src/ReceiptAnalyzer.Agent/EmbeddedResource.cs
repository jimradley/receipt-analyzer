using System.Reflection;

namespace ReceiptAnalyzer.Agent;

/// <summary>Reads a text resource embedded in the Agent assembly (e.g. uk-seasonality.json).</summary>
internal static class EmbeddedResource
{
    public static string Load(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
