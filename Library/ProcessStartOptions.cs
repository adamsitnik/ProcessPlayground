using System;
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
    public IDictionary<string, string?> Environment => _envVars ??= InitializeEnvironment();
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

    public ProcessStartOptions(string fileName, IDictionary<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(environment);

        _fileName = fileName;
        _envVars = new Dictionary<string, string?>(environment);
    }

    private Dictionary<string, string?> InitializeEnvironment()
    {
        Dictionary<string, string?> envDict = new();
        
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            envDict[(string)entry.Key] = (string?)entry.Value;
        }
        
        return envDict;
    }
}