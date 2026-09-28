using System.Net;
using System.Text;
using System.Text.Json;

namespace Abbyy.DocLang.Demo.Tests;

public sealed class SettingsAndAiTests
{
    [Fact]
    public void StandardPresetHasTheSpecifiedPresentationFeatures()
    {
        var features = ExperiencePresets.Get(ExperiencePreset.Standard);
        Assert.True(features.ShowJson && features.ShowPlainText && features.ShowBenchmarkTokens);
        Assert.True(features.ShowFileSizes && features.ShowProcessingTime && features.ShowTokenReduction);
        Assert.True(features.ShowRepresentationAnalysis && features.ShowDocLangViewer && features.EnableAiTest);
        Assert.True(features.ShowActualAiTokenUsage && features.ShowResponseTime && features.ShowAiResponses);
        Assert.False(features.EnableCompareAll);
    }

    [Theory]
    [InlineData(ExperiencePreset.DocLangFocus)]
    [InlineData(ExperiencePreset.AiComparison)]
    public void PresetsUseTheirSpecifiedFeatureSets(ExperiencePreset preset)
    {
        var features = ExperiencePresets.Get(preset);
        if (preset == ExperiencePreset.DocLangFocus)
        {
            Assert.True(features.ShowJson && features.ShowBenchmarkTokens && features.ShowFileSizes);
            Assert.True(features.ShowTokenReduction && features.ShowRepresentationAnalysis && features.ShowDocLangViewer);
            Assert.False(features.ShowPlainText || features.ShowProcessingTime || features.EnableAiTest);
            Assert.False(features.EnableCompareAll || features.ShowActualAiTokenUsage || features.ShowResponseTime);
        }
        else
        {
            Assert.True(features.ShowJson && features.ShowPlainText && features.EnableAiTest);
            Assert.True(features.ShowActualAiTokenUsage && features.ShowResponseTime);
            Assert.True(features.ShowAiResponses && features.EnableCompareAll);
            Assert.True(features.ShowDocLangViewer && features.ShowRepresentationAnalysis);
            Assert.False(features.ShowProcessingTime);
        }
    }

    [Fact]
    public void TechnicalPresetEnablesEveryAvailableFeature()
    {
        var features = ExperiencePresets.Get(ExperiencePreset.Technical);
        Assert.All(typeof(DemoFeatureSettings).GetProperties(), property => Assert.True((bool)property.GetValue(features)!));
    }

    [Fact]
    public void ManualFeatureChangesBecomeCustomWithoutAffectingCredentials()
    {
        var service = new DemoFeatureService();
        var before = service.Status.Features;
        var changed = service.Update(before with { ShowJson = false });
        Assert.Equal(ExperiencePreset.Custom, changed.Preset);
        Assert.False(changed.Features.ShowJson);
        Assert.DoesNotContain("ApiKey", typeof(DemoFeatureSettings).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void DocLangCannotBeHiddenAndFeaturePreferencesNeverContainAnApiKey()
    {
        Assert.DoesNotContain(typeof(DemoFeatureSettings).GetProperties(), property =>
            string.Equals(property.Name, "ShowDocLang", StringComparison.OrdinalIgnoreCase));
        var session = new SessionSettings();
        var secret = "test-" + Guid.NewGuid().ToString("N");
        session.Update(new() { ApiKey = secret });
        var features = new DemoFeatureService();
        features.Update(features.Status.Features with { ShowJson = false });
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(features));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(features.Status));
    }

    [Fact]
    public void CredentialsAreNeverInPublicStatusSerializationOrToString()
    {
        var secret = "test-" + Guid.NewGuid().ToString("N");
        var settings = new SessionSettings();
        var update = new SettingsUpdate { ApiKey = secret, Model = "test-model" };
        var status = settings.Update(update);
        Assert.True(status.Configured);
        var credentials = settings.GetCredentials();
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(status));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(settings));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(credentials));
        Assert.DoesNotContain(secret, credentials.ToString());
        Assert.DoesNotContain(secret, update.ToString());
        Assert.False(new SessionSettings().Status.Configured);
    }

    [Fact]
    public void BlankKeyKeepsExistingCredentialAndClearRemovesIt()
    {
        var settings = new SessionSettings();
        settings.Update(new() { ApiKey = "test-secret", Model = "model-one" });
        Assert.True(settings.Update(new() { ApiKey = "", Model = "model-two" }).Configured);
        Assert.Equal("model-two", settings.Status.Model);
        Assert.False(settings.Update(new() { ClearKey = true }).Configured);
        Assert.Throws<DemoException>(() => settings.GetCredentials());
    }

    [Fact]
    public void ValidationIsAtomicAndErrorsNeverEchoSecrets()
    {
        var settings = new SessionSettings();
        settings.Update(new() { ApiKey = "test-secret", Model = "model-one" });
        Assert.Throws<DemoException>(() => settings.Update(new() { Model = "" }));
        var error = Assert.Throws<DemoException>(() =>
            settings.Update(new() { ApiKey = "test-secret\ninjected", Model = "model-two" }));
        Assert.DoesNotContain("test-secret", error.Message);
        Assert.Equal("model-one", settings.Status.Model);
        Assert.True(settings.Status.Configured);
    }

    [Fact]
    public async Task OpenAiSendsCompleteSelectedRepresentationOnlyOnExplicitAskWithStorageDisabled()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiProvider(http);
        var settings = new SessionSettings();
        Assert.Equal(SessionSettings.DefaultModel, settings.Status.Model);
        Assert.False(settings.Status.Configured);
        settings.Update(new() { ApiKey = "test-secret" });
        Assert.Null(handler.Body);
        await provider.AskAsync(new(AiRepresentation.DocLang, Fixtures.DocLang, "What is here?"), settings.GetCredentials(), CancellationToken.None);
        using (var defaultBody = JsonDocument.Parse(handler.Body!))
            Assert.Equal(SessionSettings.DefaultModel, defaultBody.RootElement.GetProperty("model").GetString());
        handler.Body = null;
        settings.Update(new() { ApiKey = "test-secret", Model = "test-model" });
        Assert.Null(handler.Body);
        var answer = await provider.AskAsync(new(AiRepresentation.DocLang, Fixtures.DocLang, "What is here?"), settings.GetCredentials(), CancellationToken.None);
        Assert.Equal("Test answer.", answer.Text);
        Assert.Equal("https://api.openai.com/v1/responses", handler.Url);
        Assert.Equal("Bearer test-secret", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Document representation:\n" + Fixtures.DocLang, body.RootElement.GetProperty("input")[0].GetProperty("content").GetString());
        Assert.Equal("Question:\nWhat is here?", body.RootElement.GetProperty("input")[1].GetProperty("content").GetString());
        Assert.Equal(AiRepresentation.DocLang, answer.Representation);
        Assert.Equal(101, answer.Usage!.InputTokens);
        Assert.Equal(11, answer.Usage.CachedInputTokens);
        Assert.Equal(7, answer.Usage.OutputTokens);
        Assert.Equal(108, answer.Usage.TotalTokens);
        Assert.DoesNotContain("test-secret", handler.Body!);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task ConnectionTestUsesTheConfiguredModelWithoutSendingDocumentContent()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var provider = new OpenAiProvider(http);
        var connection = await provider.TestConnectionAsync(new AiCredentials("test-secret", SessionSettings.DefaultModel), CancellationToken.None);
        Assert.Equal(SessionSettings.DefaultModel, connection.Model);
        Assert.Equal("Bearer test-secret", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(SessionSettings.DefaultModel, body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("Connection check.", body.RootElement.GetProperty("input").GetString());
        Assert.False(body.RootElement.TryGetProperty("instructions", out _));
        Assert.DoesNotContain(Fixtures.DocLang, handler.Body!);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ProviderErrorsDoNotEchoResponseBodies(int status)
    {
        using var http = new HttpClient(new RecordingHandler { Status = (HttpStatusCode)status, Response = "test-secret from provider" });
        var provider = new OpenAiProvider(http);
        var error = await Assert.ThrowsAsync<DemoException>(() =>
            provider.AskAsync(new(AiRepresentation.DocLang, Fixtures.DocLang, "Question"), new AiCredentials("test-secret", "model"), CancellationToken.None));
        Assert.DoesNotContain("test-secret", error.Message);
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task ConnectionFailureIsSafeAndActionable()
    {
        using var http = new HttpClient(new RecordingHandler { Status = HttpStatusCode.Unauthorized, Response = "test-secret from provider" });
        var provider = new OpenAiProvider(http);
        var error = await Assert.ThrowsAsync<DemoException>(() =>
            provider.TestConnectionAsync(new AiCredentials("test-secret", "model"), CancellationToken.None));
        Assert.Equal("Authentication failed. Check the configured OpenAI API key.", error.Message);
        Assert.DoesNotContain("test-secret", error.Message);
    }

    [Fact]
    public async Task MalformedSuccessfulProviderResponseIsSanitized()
    {
        using var http = new HttpClient(new RecordingHandler { Response = "{not valid json" });
        var provider = new OpenAiProvider(http);
        var error = await Assert.ThrowsAsync<DemoException>(() => provider.AskAsync(
            new(AiRepresentation.DocLang, Fixtures.DocLang, "Question"), new AiCredentials("test-secret", "model"), CancellationToken.None));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal("OpenAI returned an unreadable response. Try again shortly.", error.Message);
    }

    [Fact]
    public async Task ConnectionTimeoutUsesASafeActionableMessage()
    {
        using var http = new HttpClient(new TimeoutHandler());
        var provider = new OpenAiProvider(http);
        var error = await Assert.ThrowsAsync<DemoException>(() =>
            provider.TestConnectionAsync(new AiCredentials("test-secret", "model"), CancellationToken.None));
        Assert.Equal("The OpenAI request timed out.", error.Message);
        Assert.Equal(504, error.StatusCode);
    }

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body;
        public string? Url;
        public string? Authorization;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Response = """{"model":"test-model","status":"completed","usage":{"input_tokens":101,"input_tokens_details":{"cached_tokens":11},"output_tokens":7,"total_tokens":108},"output":[{"type":"reasoning"},{"type":"message","content":[{"type":"output_text","text":"Test answer."}]}]}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException());
    }
}
