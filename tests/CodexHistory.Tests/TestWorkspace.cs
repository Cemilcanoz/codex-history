using System.Security.Cryptography;
using CodexHistory.Infrastructure;

namespace CodexHistory.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodexHistory.Tests", Guid.NewGuid().ToString("N"));
    public string Source => Path.Combine(Root, "source");
    public string Database => Path.Combine(Root, "catalog", "history.db");

    public TestWorkspace() => Directory.CreateDirectory(Source);

    public SqliteHistoryCatalog Catalog() => new(Database);

    public string WriteLog(string relativePath, params string[] lines)
    {
        var path = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        return path;
    }

    public string WriteText(string relativePath, string text)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
