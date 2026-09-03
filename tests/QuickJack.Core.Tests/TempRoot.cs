using QuickJack.Core.Storage;

namespace QuickJack.Core.Tests;

/// <summary>An isolated on-disk state root, deleted when the test finishes.</summary>
public sealed class TempRoot : IDisposable
{
    public string Path { get; }
    public QuickJackPaths Paths { get; }

    public TempRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "quickjack-tests", Guid.NewGuid().ToString("N"));
        Paths = QuickJackPaths.Under(Path);
        Directory.CreateDirectory(Paths.UserDirectory);
        Directory.CreateDirectory(Paths.MachineDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
