using Microsoft.Win32.SafeHandles;
using System.Text;
using static Library.LowLevelHelpers;

namespace Library;

/// <summary>
/// An async enumerable that streams output lines from a command line process.
/// </summary>
public class CommandLineOutput : IAsyncEnumerable<OutputLine>
{
    private readonly CommandLineInfo _commandLineInfo;
    private readonly Encoding? _encoding;
    private int? _exitCode, _processId;

    internal CommandLineOutput(CommandLineInfo commandLineInfo, Encoding? encoding)
    {
        _commandLineInfo = commandLineInfo;
        _encoding = encoding;
    }

    // Design: some users need to obtain process ID and exit code when streaming outputs.

    /// <summary>
    /// Gets the process ID of the started process.
    /// </summary>
    /// <remarks>Throws <see cref="InvalidOperationException"/> if the process has not started yet.</remarks>
    public int ProcessId => _processId ?? throw new InvalidOperationException("Process has not started yet.");

    /// <summary>
    /// Gets the exit code of the process.
    /// </summary>
    /// <remarks>Throws <see cref="InvalidOperationException"/> if the process has not exited yet.</remarks>
    public int ExitCode => _exitCode ?? throw new InvalidOperationException("Process has not exited yet.");

    // Design: prevent the deadlocks: the user has to consume output lines, otherwise the process is not even started.
    public async IAsyncEnumerator<OutputLine> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        // Store a copy to avoid not unsubscribing if KillOnCancelKeyPress changes in the meantime
        bool killOnCtrlC = _commandLineInfo.KillOnCancelKeyPress;
        using CancellationTokenSource? cts = killOnCtrlC ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;

        ProcessUtils.CreatePipe(out SafeFileHandle parentOutputHandle, out SafeFileHandle childOutputHandle, parentInputs: false);
        ProcessUtils.CreatePipe(out SafeFileHandle parentErrorHandle, out SafeFileHandle childErrorHandle, parentInputs: false);

        using SafeFileHandle inputHandle = GetStdInputHandle();
        using (parentOutputHandle)
        using (childOutputHandle)
        using (childErrorHandle)
        using (parentErrorHandle)
        {
            using SafeProcessHandle procHandle = ProcessUtils.StartCore(_commandLineInfo, inputHandle, childOutputHandle, childErrorHandle);
            _processId = Interop.Kernel32.GetProcessId(procHandle);

            // CRITICAL: Close the child handles in the parent process
            // so the pipe will signal EOF when the child exits
            childOutputHandle.Close();
            childErrorHandle.Close();

            // NOTE: we could get current console Encoding here, it's ommited for the sake of simplicity of the proof of concept.
            Encoding encoding = _encoding ?? Encoding.UTF8;
            using StreamReader outputReader = new(new FileStream(parentOutputHandle, FileAccess.Read, bufferSize: 0), encoding);
            using StreamReader errorReader = new(new FileStream(parentErrorHandle, FileAccess.Read, bufferSize: 0), encoding);

            if (killOnCtrlC)
            {
                cancellationToken = cts!.Token;
                Console.CancelKeyPress += CtrlC;
            }

            Task<string?> readOutput = outputReader.ReadLineAsync(cancellationToken).AsTask();
            Task<string?> readError = errorReader.ReadLineAsync(cancellationToken).AsTask();

            try
            {
                while (true)
                {
                    Task completedTask = await Task.WhenAny(readOutput, readError);

                    bool isError = completedTask == readError;
                    string? line = await (isError ? readError : readOutput);
                    if (line is null)
                    {
                        await (isError ? readOutput : readError);

                        break;
                    }

                    yield return new(line, isError);

                    if (isError)
                    {
                        readError = errorReader.ReadLineAsync(cancellationToken).AsTask();
                    }
                    else
                    {
                        readOutput = outputReader.ReadLineAsync(cancellationToken).AsTask();
                    }
                }

                _exitCode = procHandle.GetExitCode();
            }
            finally
            {
                if (killOnCtrlC)
                {
                    Console.CancelKeyPress -= CtrlC;
                }
            }
        }

        void CtrlC(object? sender, ConsoleCancelEventArgs e)
        {
            if (!cts!.IsCancellationRequested)
            {
                cts.Cancel();
            }
        }
    }
}
