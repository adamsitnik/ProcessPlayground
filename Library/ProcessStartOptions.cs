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
    public IDictionary<string, string?> Environment => _envVars ??= new();
    public DirectoryInfo? WorkingDirectory { get; set; }
    public bool CreateNoWindow { get; set; }

    // New: User very often implement it on their own.
    public bool KillOnParentDeath { get; set; }

    public ProcessStartOptions(string fileName)
    {
#if NETFRAMEWORK
        ThrowHelper.ThrowIfNullOrEmpty(fileName, nameof(fileName));
#else
        ArgumentException.ThrowIfNullOrEmpty(fileName);
#endif

        _fileName = fileName;
    }
}