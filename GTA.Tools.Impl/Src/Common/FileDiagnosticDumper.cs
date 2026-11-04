
namespace GTA.Tools.Impl.Common;

/// <summary>Writes every dumped artifact as "{directory}\{name}". Best-effort -- never throws.</summary>
public sealed class FileDiagnosticDumper : IDiagnosticDumper
{
    private readonly string _directory;

    public FileDiagnosticDumper(string directory) => _directory = directory;

    public void Dump(string name, string content)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, name), content);
        }
        catch { }
    }
}
