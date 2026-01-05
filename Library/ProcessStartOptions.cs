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

    // New: Create process in suspended state
    public bool CreateSuspended { get; set; }

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
}