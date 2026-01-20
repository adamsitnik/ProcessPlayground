using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace System.TBA;

public sealed class ProcessStartOptions
{
    private static string? _executableDirectory;
#if WINDOWS
    private static string? _systemDirectory;
#endif

    private readonly string _fileName;
    private List<string>? _arguments;
    private Dictionary<string, string?>? _envVars;
    private List<SafeHandle>? _inheritedHandles;

    // More or less same as ProcessStartInfo
    public string FileName => _fileName;
    public IList<string> Arguments => _arguments ??= new();
    public IDictionary<string, string?> Environment => _envVars ??= CreateEnvironmentCopy();

    /// <summary>
    /// Gets a list of handles that will be inherited by the child process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Handles do not need to have inheritance enabled beforehand.
    /// They are also not duplicated, just added as-is to the child process
    /// so the exact same handle values can be used in the child process.
    /// </para>
    /// <para>
    /// On Windows, the implementation will automatically enable inheritance on any handle added to this list
    /// by modifying the handle's flags using SetHandleInformation.
    /// </para>
    /// <para>
    /// On Unix, the implementation will modify the copy of every handle in the child process
    /// by removing FD_CLOEXEC flag. It happens after the fork and before the exec, so it does not affect parent process.
    /// </para>
    /// </remarks>
    public IList<SafeHandle> InheritedHandles => _inheritedHandles ??= new();
    
    public DirectoryInfo? WorkingDirectory { get; set; }
    public bool CreateNoWindow { get; set; }

    // New: User very often implement it on their own.
    public bool KillOnParentDeath { get; set; }

    // New: Start the process in a suspended state (can be resumed later)
    public bool CreateSuspended { get; set; }

    // Internal property to check if environment was explicitly set
    internal bool HasEnvironmentBeenAccessed => _envVars != null;

    // Internal property to check if inherited handles were explicitly set
    internal bool HasInheritedHandlesBeenAccessed => _inheritedHandles != null;

    internal bool IsFileNameResolved { get; }

    public ProcessStartOptions(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        _fileName = fileName;
    }

    private ProcessStartOptions(string resolvedFileName, bool isResolved)
    {
        _fileName = resolvedFileName;
        IsFileNameResolved = isResolved;
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
        string resolvedPath = ResolvePathInternal(fileName);
        return new ProcessStartOptions(resolvedPath, isResolved: true);
    }

    internal static string ResolvePathInternal(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        // If the fileName is a complete path, use it, regardless of whether it exists.
        if (Path.IsPathRooted(fileName))
        {
            // In this case, it doesn't matter whether the file exists or not;
            // it's what the caller asked for, so it's what they'll get
            return fileName;
        }

#if WINDOWS
        // From: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw
        // "If the file name does not contain an extension, .exe is appended.
        // Therefore, if the file name extension is .com, this parameter must include the .com extension.
        // If the file name ends in a period (.) with no extension, or if the file name contains a path, .exe is not appended."

        // HasExtension returns false for trailing dot, so we need to check that separately
        if (fileName[fileName.Length - 1] != '.' && !Path.HasExtension(fileName))
        {
            fileName += ".exe";
        }
#endif

        // Then check the executable's directory
        string? executableDirectory= _executableDirectory ??= Path.GetDirectoryName(GetExecutablePath());
        if (executableDirectory is not null)
        {
            try
            {
                string path = Path.Combine(executableDirectory, fileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch (ArgumentException) { } // ignore any errors in data that may come from the exe path
        }

        // Then check the current directory
        string currentDirPath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        if (File.Exists(currentDirPath))
        {
            return currentDirPath;
        }

#if WINDOWS
        // Windows-specific search locations (from CreateProcessW documentation)
        
        // Check the 32-bit Windows system directory (It can't change over app lifetime)
        string? systemDirectory = _systemDirectory ??= WindowsHelpers.GetSystemDirectory();
        if (systemDirectory is not null)
        {
            string path = Path.Combine(systemDirectory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Check the 16-bit Windows system directory (System subdirectory of Windows directory)
        // Windows directory is user-specific, so we don't cache it.
        string? windowsDirectory = WindowsHelpers.GetWindowsDirectory();
        if (windowsDirectory is not null)
        {
            string path = Path.Combine(windowsDirectory, "System", fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Check the Windows directory
        if (windowsDirectory is not null)
        {
            string path = Path.Combine(windowsDirectory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }
#endif

        // Then check each directory listed in the PATH environment variables
        return FindProgramInPath(fileName);
    }

    private static string? GetExecutablePath()
    {
        return System.Environment.ProcessPath;
    }

    private static string FindProgramInPath(string fileName)
    {
        string? pathEnvVar = System.Environment.GetEnvironmentVariable("PATH");
        if (pathEnvVar is not null)
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
                string path = Path.Combine(subPath, fileName);
                if (IsExecutableFile(path))
                {
                    return path;
                }
            }
        }

        throw new FileNotFoundException("Could not resolve the file.", fileName);
    }

    private static bool IsExecutableFile(string path)
    {
#if WINDOWS
        return File.Exists(path);
#else
        // Modern .NET on Unix/Linux
        return UnixHelpers.IsExecutable(path);
#endif
    }
}