namespace Abbyy.DocLang.Demo;

public sealed record SettingsStatus(bool Configured, string Model);

// Deliberately not records: generated ToString() must never expose a credential.
public sealed class SettingsUpdate
{
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public bool ClearKey { get; set; }
    public ExperiencePreset? ExperiencePreset { get; set; }
    public DemoFeatureSettings? DemoFeatures { get; set; }
    public string? DemoFeature { get; set; }
    public bool? DemoFeatureEnabled { get; set; }
    public long? ExpectedRevision { get; set; }
    public override string ToString() => "SettingsUpdate (credential redacted)";
}

public sealed class AiCredentials(string apiKey, string model)
{
    internal string ApiKey { get; } = apiKey;
    public string Model { get; } = model;
    public override string ToString() => "AiCredentials (credential redacted)";
}

public sealed class SessionSettings
{
    public const string DefaultModel = "gpt-5.6-terra";
    private readonly object sync = new();
    private string? key;
    private string model = DefaultModel;
    public SettingsStatus Status { get { lock (sync) return new(!string.IsNullOrEmpty(key) && model.Length > 0, model); } }
    public SettingsStatus Update(SettingsUpdate update)
    {
        lock (sync)
        {
            var nextModel = (update.Model ?? model).Trim();
            if (nextModel.Length > 100 || nextModel.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')))
                throw new DemoException("Enter a valid OpenAI model name (up to 100 characters).");
            var nextKey = update.ApiKey?.Trim();
            if (nextKey is { Length: > 0 } && (nextKey.Length > 512 || nextKey.Any(c => c <= ' ' || c > '~')))
                throw new DemoException("The API key contains unsupported characters or is too long.");
            if (!update.ClearKey && (!string.IsNullOrEmpty(nextKey) || !string.IsNullOrEmpty(key)) && string.IsNullOrEmpty(nextModel))
                throw new DemoException("Enter a model name to use with the API key.");
            model = nextModel;
            if (update.ClearKey) key = null;
            else if (!string.IsNullOrEmpty(nextKey)) key = nextKey;
            return new(!string.IsNullOrEmpty(key) && model.Length > 0, model);
        }
    }
    public AiCredentials GetCredentials()
    {
        lock (sync)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(model))
                throw new DemoException("OpenAI API key is not configured. Open Settings - AI Configuration.");
            return new(key, model);
        }
    }
}

public sealed record ApplicationSettingsStatus(
    bool Configured,
    string Model,
    long Revision,
    DemoFeatureStatus DemoFeatures);

public sealed class ApplicationSettingsService(SessionSettings session, DemoFeatureService demoFeatures)
{
    private readonly object sync = new();
    private long revision;

    public ApplicationSettingsStatus Status
    {
        get
        {
            lock (sync)
            {
                var current = session.Status;
                return new(current.Configured, current.Model, revision, demoFeatures.Status);
            }
        }
    }

    public ApplicationSettingsStatus Update(SettingsUpdate update)
    {
        lock (sync)
        {
            if (update.ExpectedRevision is { } expected && expected != revision)
                throw new DemoException(
                    "Settings changed in another browser tab. The latest settings will be loaded before retrying your change.",
                    409, "settings_revision_conflict");

            var hasAiUpdate = update.ApiKey is not null || update.Model is not null || update.ClearKey;
            var hasFeaturePair = update.DemoFeature is not null || update.DemoFeatureEnabled.HasValue;
            if ((update.DemoFeature is null) != !update.DemoFeatureEnabled.HasValue)
                throw new DemoException("A feature name and value must be supplied together.");

            var experienceOperations = (update.ExperiencePreset.HasValue ? 1 : 0)
                + (update.DemoFeatures is not null ? 1 : 0)
                + (hasFeaturePair ? 1 : 0);
            if (experienceOperations > 1 || (hasAiUpdate && experienceOperations > 0))
                throw new DemoException("Update AI configuration or Experience settings in one operation, not both.");
            if (!hasAiUpdate && experienceOperations == 0)
                throw new DemoException("No settings change was supplied.");

            if (update.ExperiencePreset is { } preset)
                demoFeatures.ApplyPreset(preset);
            else if (update.DemoFeatures is { } features)
                demoFeatures.Update(features);
            else if (hasFeaturePair)
                demoFeatures.UpdateFeature(update.DemoFeature!, update.DemoFeatureEnabled!.Value);
            else
                session.Update(update);

            revision++;
            var current = session.Status;
            return new(current.Configured, current.Model, revision, demoFeatures.Status);
        }
    }
}
