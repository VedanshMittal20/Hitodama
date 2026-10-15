using System;
using System.IO;

namespace Companion.Tests.TestInfrastructure;

/// <summary>
/// Provides an isolated temporary directory for test executions, ensuring zero state leakage.
/// </summary>
public sealed class TestDirectoryScope : IDisposable
{
    public string DirectoryPath { get; }

    public TestDirectoryScope()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "VoidTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string CreateFilePath(string fileName) => Path.Combine(DirectoryPath, fileName);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup in test environments
        }
    }
}
