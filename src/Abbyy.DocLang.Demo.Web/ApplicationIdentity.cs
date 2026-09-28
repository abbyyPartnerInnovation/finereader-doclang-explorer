using System.Reflection;

namespace Abbyy.DocLang.Demo;

public sealed record ApplicationIdentityStatus(string ApplicationVersion, string ContractVersion, string BuildId);

public static class ApplicationIdentity
{
    public const string ContractVersion = "2026.09.1";
    public const string ContractHeader = "X-Demo-Contract";
    public const string BuildHeader = "X-Demo-Build";

    private static readonly Assembly Assembly = typeof(ApplicationIdentity).Assembly;

    public static string ApplicationVersion { get; } = Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public static string BuildId { get; } = Assembly.ManifestModule.ModuleVersionId.ToString("N");
    public static ApplicationIdentityStatus Status { get; } = new(ApplicationVersion, ContractVersion, BuildId);

    public static bool Matches(string? contractVersion, string? buildId) =>
        string.Equals(contractVersion, ContractVersion, StringComparison.Ordinal)
        && string.Equals(buildId, BuildId, StringComparison.Ordinal);

    public static string StampIndex(string template) => template
        .Replace("__APP_VERSION__", ApplicationVersion, StringComparison.Ordinal)
        .Replace("__APP_CONTRACT_VERSION__", ContractVersion, StringComparison.Ordinal)
        .Replace("__APP_BUILD_ID__", BuildId, StringComparison.Ordinal);
}
