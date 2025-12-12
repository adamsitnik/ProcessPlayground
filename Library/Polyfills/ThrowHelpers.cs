#if NET48
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System;

internal static class ArgumentNullExceptionExtensions
{
    public static void ThrowIfNull(this Type _, object? argument, string? paramName = null)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }
}

internal static class ArgumentExceptionExtensions
{
    public static void ThrowIfNullOrEmpty(this Type _, string? argument, string? paramName = null)
    {
        if (string.IsNullOrEmpty(argument))
        {
            throw new ArgumentException("Value cannot be null or empty.", paramName);
        }
    }
}
#endif
