using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace System.TBA;

public sealed class ProcessStartOptions
{
    private readonly string _fileName;
    private List<string>? _arguments;
    private Dictionary<string, string?>? _envVars;

    // More or less same as ProcessStartInfo
    public string FileName => _fileName;
    public IList<string> Arguments => _arguments ??= new();
    public IDictionary<string, string?> Environment => _envVars ??= CreateEnvironmentCopy();
    public DirectoryInfo? WorkingDirectory { get; set; }
    public bool CreateNoWindow { get; set; }

    // New: User very often implement it on their own.
    public bool KillOnParentDeath { get; set; }

    // Internal property to check if environment was explicitly set
    internal bool HasEnvironmentBeenAccessed => _envVars != null;

    public ProcessStartOptions(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        _fileName = fileName;
    }

    private static Dictionary<string, string?> CreateEnvironmentCopy()
    {
        Dictionary<string, string?> envDict = new();
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            envDict[(string)entry.Key] = (string?)entry.Value;
        }
        return envDict;
    }

    /// <summary>
    /// Resolves the given file name to an absolute path and creates a new ProcessStartOptions instance.
    /// </summary>
    /// <param name="fileName">The file name to resolve.</param>
    /// <returns>A new ProcessStartOptions instance with the resolved path.</returns>
    /// <exception cref="ArgumentException">Thrown when fileName is null or empty.</exception>
    /// <exception cref="FileNotFoundException">Thrown when fileName cannot be resolved to an existing file.</exception>
    public static ProcessStartOptions ResolvePath(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        string? resolvedPath = ResolvePathInternal(fileName);
        
        if (resolvedPath == null)
        {
            throw new FileNotFoundException($"Could not find file '{fileName}'.", fileName);
        }

        return new ProcessStartOptions(resolvedPath);
    }

    private static string? ResolvePathInternal(string filename)
    {
        // If the filename is a complete path, use it, regardless of whether it exists.
        if (Path.IsPathRooted(filename))
        {
            // In this case, it doesn't matter whether the file exists or not;
            // it's what the caller asked for, so it's what they'll get
            return filename;
        }

        // Then check the executable's directory
        string? executablePath = GetExecutablePath();
        if (executablePath != null)
        {
            try
            {
                string? dir = Path.GetDirectoryName(executablePath);
                if (dir != null)
                {
                    string path = Path.Combine(dir, filename);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch (ArgumentException) { } // ignore any errors in data that may come from the exe path
        }

        // Then check the current directory
        string currentDirPath = Path.Combine(Directory.GetCurrentDirectory(), filename);
        if (File.Exists(currentDirPath))
        {
            return currentDirPath;
        }

#if WINDOWS
        // Windows-specific search locations (from CreateProcessW documentation)
        
        // Check the 32-bit Windows system directory
        string? systemDirectory = WindowsHelpers.GetSystemDirectory();
        if (systemDirectory != null)
        {
            string path = Path.Combine(systemDirectory, filename);
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Check the 16-bit Windows system directory (System subdirectory of Windows directory)
        string? windowsDirectory = WindowsHelpers.GetWindowsDirectory();
        if (windowsDirectory != null)
        {
            string path = Path.Combine(windowsDirectory, "System", filename);
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Check the Windows directory
        if (windowsDirectory != null)
        {
            string path = Path.Combine(windowsDirectory, filename);
            if (File.Exists(path))
            {
                return path;
            }
        }
#endif

        // Then check each directory listed in the PATH environment variables
        return FindProgramInPath(filename);
    }

    private static string? GetExecutablePath()
    {
#if NETFRAMEWORK
        return System.Reflection.Assembly.GetEntryAssembly()?.Location;
#else
        return System.Environment.ProcessPath;
#endif
    }

    private static string? FindProgramInPath(string program)
    {
        string? pathEnvVar = System.Environment.GetEnvironmentVariable("PATH");
        if (pathEnvVar != null)
        {
#if WINDOWS
            char pathSeparator = ';';
#else
            char pathSeparator = ':';
#endif
            var pathParser = new StringParser(pathEnvVar, pathSeparator, skipEmpty: true);
            while (pathParser.MoveNext())
            {
                string subPath = pathParser.ExtractCurrent();
                string path = Path.Combine(subPath, program);
                if (IsExecutableFile(path))
                {
                    return path;
                }
            }
        }
        return null;
    }

    private static bool IsExecutableFile(string path)
    {
#if NETFRAMEWORK || WINDOWS
        return File.Exists(path);
#else
        // Modern .NET on Unix/Linux
        return UnixHelpers.IsExecutable(path);
#endif
    }
}