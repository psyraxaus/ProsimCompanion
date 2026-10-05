using System.Reflection;

namespace ProsimCompanion.Prosim.Sdk;

/// <summary>Which public constructors the loaded <c>ProSimConnect</c> offers.</summary>
[Flags]
internal enum SdkConstructorShape
{
    /// <summary>Neither known constructor — this SDK build cannot be used.</summary>
    None = 0,

    /// <summary><c>ProSimConnect()</c>: the older builds (ProSim 1.74-beta.8, dll of 2025-11).</summary>
    Parameterless = 1,

    /// <summary><c>ProSimConnect(string apiKey = "")</c>: the newer builds (dll of 2026-07).</summary>
    ApiKey = 2,
}

/// <summary>
/// Builds the SDK's <c>ProSimConnect</c> through whichever constructor the user's dll has.
///
/// Issue #158 (ticket t-20261005-1918, 2026-10-05): the two SDK builds in the field have
/// <em>disjoint</em> constructors — the older one only <c>()</c>, the newer one only
/// <c>(string apiKey = "")</c>. Compiled against the newer dll, <c>new ProSimConnect()</c> and
/// <c>new ProSimConnect(apiKey)</c> both became the same <c>.ctor(String)</c> member reference,
/// so on the older dll the JIT failed the whole <see cref="SdkConnection"/> constructor with
/// MissingMethodException before any connect attempt. Both builds report file version 1.1.1.0,
/// so the version cannot pick the shape — only reflection can. Everything else the app binds
/// (<c>Connect(string, bool)</c>, the events, <c>DataRef</c>) is identical on both.
///
/// Works on a <see cref="Type"/> rather than on <c>ProSimConnect</c> so the selection is
/// testable with stand-in types and without the SDK.
/// </summary>
internal static class SdkConstructorSelector
{
    /// <summary>The constructors <paramref name="connectType"/> offers.</summary>
    public static SdkConstructorShape Detect(Type connectType)
    {
        ArgumentNullException.ThrowIfNull(connectType);

        var shape = SdkConstructorShape.None;
        if (connectType.GetConstructor(Type.EmptyTypes) is not null)
        {
            shape |= SdkConstructorShape.Parameterless;
        }

        if (connectType.GetConstructor([typeof(string)]) is not null)
        {
            shape |= SdkConstructorShape.ApiKey;
        }

        return shape;
    }

    /// <summary>
    /// What the log needs to tell one SDK build from another: where the dll came from, its
    /// product version (it carries the ProSim commit hash — the file version does not differ
    /// between builds) and its constructor shape.
    /// </summary>
    public static SdkInfo Inspect(Type connectType)
    {
        ArgumentNullException.ThrowIfNull(connectType);

        var assembly = connectType.Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return new SdkInfo(
            assembly.Location,
            string.IsNullOrWhiteSpace(version) ? assembly.GetName().Version?.ToString() ?? "unknown" : version,
            Detect(connectType));
    }

    /// <summary>Short name of a shape for the log ("parameterless", "API-key", …).</summary>
    public static string Describe(SdkConstructorShape shape) => shape switch
    {
        SdkConstructorShape.Parameterless => "parameterless",
        SdkConstructorShape.ApiKey => "API-key",
        SdkConstructorShape.Parameterless | SdkConstructorShape.ApiKey => "parameterless and API-key",
        _ => "unknown",
    };

    /// <summary>
    /// Creates an instance of <paramref name="connectType"/>. With an API key the API-key
    /// constructor is preferred; without one the parameterless constructor is. When only the
    /// other constructor exists it is used: a key is then dropped (the caller warns — see
    /// <see cref="Detect"/>), and a missing key becomes the constructor's own default value,
    /// which is what the compiler passed before this was reflection.
    /// </summary>
    /// <exception cref="SdkIncompatibleException">The type has neither constructor.</exception>
    public static object Create(Type connectType, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(connectType);

        var parameterless = connectType.GetConstructor(Type.EmptyTypes);
        var withApiKey = connectType.GetConstructor([typeof(string)]);
        var hasKey = !string.IsNullOrWhiteSpace(apiKey);

        if (withApiKey is not null && (hasKey || parameterless is null))
        {
            var parameter = withApiKey.GetParameters()[0];
            object? argument = hasKey
                ? apiKey
                : parameter.HasDefaultValue ? parameter.DefaultValue : null;

            // DoNotWrapExceptions: the SDK's own exceptions (AuthenticationException on a bad
            // key) must reach the caller as themselves, as they did with a direct `new`.
            return withApiKey.Invoke(BindingFlags.DoNotWrapExceptions, null, [argument], null);
        }

        if (parameterless is not null)
        {
            return parameterless.Invoke(BindingFlags.DoNotWrapExceptions, null, null, null);
        }

        throw new SdkIncompatibleException(
            $"{connectType.FullName} has neither a parameterless constructor nor one that takes an API key.");
    }
}

/// <summary>Identity of the loaded ProSimSDK.dll (see <see cref="SdkConstructorSelector.Inspect"/>).</summary>
internal sealed record SdkInfo(string Location, string ProductVersion, SdkConstructorShape Shape);

/// <summary>The loaded ProSimSDK.dll has a shape this app does not know how to drive.</summary>
internal sealed class SdkIncompatibleException : Exception
{
    public SdkIncompatibleException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// True when <paramref name="exception"/> says the loaded dll and this app do not fit:
    /// this exception, or the runtime's own binding failures (a member or type the app was
    /// compiled against is not in the user's dll — MissingMethodException is a
    /// <see cref="MissingMemberException"/>).
    /// </summary>
    public static bool IsMismatch(Exception exception)
        => exception is SdkIncompatibleException
            or MissingMemberException
            or TypeLoadException
            or BadImageFormatException;
}
