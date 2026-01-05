namespace Tests;

internal static class ConditionalTests
{
    public const string UnixOnly =
#if WINDOWS
        "Unix-specific test";
#else
        "";
#endif

    public const string WindowsOnly =
#if !WINDOWS
        "Windows-specific test";
#else
        "";
#endif
}
