using System.Text.Json.Serialization;

namespace Abbyy.DocLang.Demo;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExperiencePreset
{
    Standard,
    DocLangFocus,
    AiComparison,
    Technical,
    Custom
}

// This is deliberately separate from SessionSettings: it describes presentation only
// and never contains an API key or any other credential.
public sealed record DemoFeatureSettings
{
    public bool ShowJson { get; init; }
    public bool ShowPlainText { get; init; }
    public bool ShowBenchmarkTokens { get; init; }
    public bool ShowFileSizes { get; init; }
    public bool ShowProcessingTime { get; init; }
    public bool ShowTokenReduction { get; init; }
    public bool ShowRepresentationAnalysis { get; init; }
    public bool ShowDocLangViewer { get; init; }
    public bool EnableAiTest { get; init; }
    public bool ShowActualAiTokenUsage { get; init; }
    public bool ShowResponseTime { get; init; }
    public bool ShowAiResponses { get; init; }
    public bool EnableCompareAll { get; init; }
}

public sealed record DemoFeatureStatus(ExperiencePreset Preset, DemoFeatureSettings Features);

public static class ExperiencePresets
{
    public static readonly IReadOnlyDictionary<ExperiencePreset, DemoFeatureSettings> Definitions =
        new Dictionary<ExperiencePreset, DemoFeatureSettings>
        {
            [ExperiencePreset.Standard] = new()
            {
                ShowJson = true, ShowPlainText = true, ShowBenchmarkTokens = true, ShowFileSizes = true,
                ShowProcessingTime = true, ShowTokenReduction = true, ShowRepresentationAnalysis = true,
                ShowDocLangViewer = true, EnableAiTest = true, ShowActualAiTokenUsage = true,
                ShowResponseTime = true, ShowAiResponses = true
            },
            [ExperiencePreset.DocLangFocus] = new()
            {
                ShowJson = true, ShowBenchmarkTokens = true, ShowFileSizes = true, ShowTokenReduction = true,
                ShowRepresentationAnalysis = true, ShowDocLangViewer = true
            },
            [ExperiencePreset.AiComparison] = new()
            {
                ShowJson = true, ShowPlainText = true, ShowBenchmarkTokens = true, ShowFileSizes = true,
                ShowTokenReduction = true, EnableAiTest = true, ShowActualAiTokenUsage = true,
                ShowResponseTime = true, ShowAiResponses = true, EnableCompareAll = true,
                ShowDocLangViewer = true, ShowRepresentationAnalysis = true
            },
            [ExperiencePreset.Technical] = new()
            {
                ShowJson = true, ShowPlainText = true, ShowBenchmarkTokens = true, ShowFileSizes = true,
                ShowProcessingTime = true, ShowTokenReduction = true, ShowRepresentationAnalysis = true,
                ShowDocLangViewer = true, EnableAiTest = true, ShowActualAiTokenUsage = true,
                ShowResponseTime = true, ShowAiResponses = true, EnableCompareAll = true
            }
        };

    public static DemoFeatureSettings Get(ExperiencePreset preset) =>
        Definitions.TryGetValue(preset, out var features)
            ? features with { }
            : throw new DemoException("Custom is not a selectable preset.");
}

public sealed class DemoFeatureService
{
    private readonly object sync = new();
    private ExperiencePreset preset = ExperiencePreset.Standard;
    private DemoFeatureSettings features = ExperiencePresets.Get(ExperiencePreset.Standard);

    public DemoFeatureStatus Status
    {
        get { lock (sync) return new(preset, features with { }); }
    }

    public DemoFeatureStatus ApplyPreset(ExperiencePreset nextPreset)
    {
        if (nextPreset == ExperiencePreset.Custom)
            throw new DemoException("Custom represents manual feature selections and cannot be applied.");
        lock (sync)
        {
            preset = nextPreset;
            features = ExperiencePresets.Get(nextPreset);
            return new(preset, features with { });
        }
    }

    public DemoFeatureStatus Update(DemoFeatureSettings nextFeatures)
    {
        ArgumentNullException.ThrowIfNull(nextFeatures);
        lock (sync)
        {
            // DocLang is intentionally absent from the model: it is always visible.
            preset = ExperiencePreset.Custom;
            features = nextFeatures with { };
            return new(preset, features with { });
        }
    }

    public DemoFeatureStatus UpdateFeature(string name, bool enabled)
    {
        lock (sync)
        {
            features = name switch
            {
                "showJson" => features with { ShowJson = enabled },
                "showPlainText" => features with { ShowPlainText = enabled },
                "showBenchmarkTokens" => features with { ShowBenchmarkTokens = enabled },
                "showFileSizes" => features with { ShowFileSizes = enabled },
                "showProcessingTime" => features with { ShowProcessingTime = enabled },
                "showTokenReduction" => features with { ShowTokenReduction = enabled },
                "showRepresentationAnalysis" => features with { ShowRepresentationAnalysis = enabled },
                "showDocLangViewer" => features with { ShowDocLangViewer = enabled },
                "enableAiTest" => features with { EnableAiTest = enabled },
                "enableCompareAll" => features with { EnableCompareAll = enabled },
                "showActualAiTokenUsage" => features with { ShowActualAiTokenUsage = enabled },
                "showResponseTime" => features with { ShowResponseTime = enabled },
                "showAiResponses" => features with { ShowAiResponses = enabled },
                _ => throw new DemoException("The requested Experience feature is not supported.")
            };
            preset = ExperiencePreset.Custom;
            return new(preset, features with { });
        }
    }
}
