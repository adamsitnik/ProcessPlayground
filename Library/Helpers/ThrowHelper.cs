#if NETFRAMEWORK
namespace System.TBA;

internal static class ThrowHelper
{
    public static void ThrowIfNull(object? argument, string? paramName = null)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    public static void ThrowIfNullOrEmpty(string? argument, string? paramName = null)
    {
        if (string.IsNullOrEmpty(argument))
        {
            throw new ArgumentException("Value cannot be null or empty.", paramName);
        }
    }
}
#endif
