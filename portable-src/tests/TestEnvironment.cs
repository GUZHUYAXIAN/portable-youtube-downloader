using System;
using System.IO;

// Shared only by test executables; never compiled into the application.
static class TestEnvironment
{
    public static string NodePath { get; private set; }

    public static void Initialize(bool report)
    {
        string projectRoot = AppDomain.CurrentDomain.BaseDirectory;
        while (projectRoot != null && !File.Exists(Path.Combine(projectRoot, "portable-src", "test.ps1")))
            projectRoot = Path.GetDirectoryName(projectRoot.TrimEnd(Path.DirectorySeparatorChar));
        if (projectRoot == null)
            throw new InvalidOperationException("Run test executables from inside the project directory.");

        string temporaryDirectory = Path.GetFullPath(Path.Combine(projectRoot, ".local", "tmp"));
        for (string path = temporaryDirectory; path != null; path = Path.GetDirectoryName(path)) {
            if ((Directory.Exists(path) || File.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing a linked test temporary directory: " + path);
        }
        Directory.CreateDirectory(temporaryDirectory);
        foreach (string name in new[] { "TEMP", "TMP", "TMPDIR", "SystemTemp" })
            Environment.SetEnvironmentVariable(name, temporaryDirectory, EnvironmentVariableTarget.Process);
        string actual = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(actual, temporaryDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test temporary path escaped the project: " + actual);
        NodePath = Path.Combine(projectRoot, "portable-src", "vendor", "node-v24.13.0", "node.exe");
        if (report) Console.WriteLine("Test temporary directory: " + actual);
    }
}
