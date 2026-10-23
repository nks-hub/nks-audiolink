using System.Reflection;

namespace NksAudioLink.Core;

/// <summary>Shared product identity from the version set in Directory.Build.props.</summary>
public static class ProductVersion
{
    public static string Informational { get; } =
        typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ProductVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static string Display { get; } = Informational.Split('+')[0];
}
