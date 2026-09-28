using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Abbyy.DocLang.Demo.Tests;

public sealed class BrowserFactAttribute : FactAttribute
{
}

public sealed class BrowserTests
{
    [BrowserFact]
    public async Task CompactPageUploadExportsSettingsAndResponsiveLayoutWork()
    {
        await using var factory = new DemoFactory();
        using var client = factory.CreateClient();
        // Give Edge a real HTTP endpoint so downloads use the normal browser network path.
        var proxyBuilder = WebApplication.CreateBuilder();
        proxyBuilder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        proxyBuilder.Logging.ClearProviders();
        await using var proxy = proxyBuilder.Build();
        proxy.Run(async context =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                "http://" + context.Request.Host + context.Request.Path + context.Request.QueryString);
            if (context.Request.ContentLength is > 0) request.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            using var response = await client.SendAsync(request);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                context.Response.Headers[header.Key] = header.Value.ToArray();
            context.Response.Headers.Remove("Transfer-Encoding");
            await response.Content.CopyToAsync(context.Response.Body);
        });
        await proxy.StartAsync();
        var browserUrl = proxy.Urls.Single();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = true });
        await using var context = await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1920, Height = 1080 },
            Permissions = ["clipboard-read", "clipboard-write"], AcceptDownloads = true
        });
        var page = await context.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue(error);
        await page.GotoAsync(browserUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.Empty(errors);
        await Assertions.Expect(page.Locator("#build-identity")).ToContainTextAsync("Build " + ApplicationIdentity.BuildId[..8]);
        Assert.Equal("Dark", await page.Locator("#theme-toggle").InnerTextAsync());
        await page.Locator("#theme-toggle").ClickAsync();
        Assert.Equal("dark", await page.Locator("html").GetAttributeAsync("data-theme"));
        Assert.Equal("Light", await page.Locator("#theme-toggle").InnerTextAsync());
        await page.Locator("#theme-toggle").ClickAsync();
        Assert.Equal("light", await page.Locator("html").GetAttributeAsync("data-theme"));
        Assert.False(await page.Locator("#ai-section").EvaluateAsync<bool>("element => element.open"));
        Assert.True(await page.Locator("#process").IsDisabledAsync());
        await page.Locator("#settings-open").ClickAsync();
        await page.Locator("#settings-dialog").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal(SessionSettings.DefaultModel, await page.Locator("#model").InputValueAsync());
        Assert.True(await page.Locator("#test-connection").IsDisabledAsync());
        await page.Locator("[data-settings-tab='ai-configuration']").PressAsync("ArrowRight");
        await Assertions.Expect(page.Locator("[data-settings-tab='experience']")).ToBeFocusedAsync();
        await Assertions.Expect(page.Locator("#experience")).ToBeVisibleAsync();
        Assert.True(await page.Locator("[data-demo-feature='showJson']").IsCheckedAsync());
        Assert.Equal("Standard", await page.Locator("#experience-preset").InputValueAsync());
        Assert.Equal(0, await page.Locator("[data-demo-feature='enableJsonAsAiInput']").CountAsync());
        Assert.Equal(0, await page.Locator("[data-demo-feature='enableDocLangAsAiInput']").CountAsync());
        Assert.Equal(0, await page.Locator("[data-demo-feature='enablePlainTextAsAiInput']").CountAsync());
        await page.Locator("[data-close='settings-dialog']").ClickAsync();
        await page.Locator("#settings-open").ClickAsync();
        await page.Locator("#settings-dialog").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await page.Locator("[data-close='settings-dialog']").ClickAsync();
        await page.Locator("#file-input").SetInputFilesAsync(new FilePayload
        {
            Name = "sample.pdf", MimeType = "application/pdf", Buffer = [1, 2, 3]
        });
        await page.Locator("#process").ClickAsync();
        await page.Locator("#upload-complete").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal("2", await page.Locator("#pages").InnerTextAsync());
        Assert.Contains("sample.pdf - 2 pages - 1.25 s", await page.Locator("#document-label").InnerTextAsync());
        Assert.Equal("JSON", await page.Locator("[data-kind='json'] h3").InnerTextAsync());
        Assert.Equal("Detailed OCR representation", await page.Locator("[data-kind='json'] .format-note").InnerTextAsync());
        Assert.Equal("Preserves granular recognition data and layout details.",
            await page.Locator("[data-kind='json'] [data-note]").InnerTextAsync());
        Assert.Equal("DocLang", await page.Locator("[data-kind='doclang'] h3").InnerTextAsync());
        Assert.Equal("Structure-oriented representation", await page.Locator("[data-kind='doclang'] .format-note").InnerTextAsync());
        Assert.Equal("Preserves document structure for measured comparison with the other exports.",
            await page.Locator("[data-kind='doclang'] [data-note]").InnerTextAsync());
        Assert.Equal(1, factory.Processor.Calls);
        await Screenshot(page, "desktop-results.png");
        var pageHeight = await page.EvaluateAsync<int>("document.documentElement.scrollHeight");
        Assert.True(pageHeight <= 1080, "Desktop results height: " + pageHeight);
        var panels = await page.Locator(".representation").AllAsync();
        var bounds = await Task.WhenAll(panels.Select(panel => panel.BoundingBoxAsync()));
        Assert.All(bounds, bound => Assert.NotNull(bound));
        Assert.True(bounds.Max(x => x!.Width) - bounds.Min(x => x!.Width) < 1);
        Assert.True(bounds.Max(x => x!.Y) - bounds.Min(x => x!.Y) < 1);
        await Screenshot(page, "desktop-results.png");
        await page.Locator("[data-kind='doclang'] [data-action='expand']").ClickAsync();
        await page.Locator("#expand-dialog").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal(Fixtures.DocLang, await page.Locator("#expanded-content").TextContentAsync());
        await page.Locator("[data-close='expand-dialog']").ClickAsync();
        await page.Locator("[data-kind='doclang'] [data-action='copy']").ClickAsync();
        await page.Locator("#toast").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        // Windows clipboard normalizes line endings; the download assertion below checks exact native bytes.
        Assert.Equal(Fixtures.DocLang.ReplaceLineEndings("\n"),
            (await page.EvaluateAsync<string>("navigator.clipboard.readText()")).ReplaceLineEndings("\n"));
        var download = await page.RunAndWaitForDownloadAsync(() => page.Locator("[data-kind='plaintext'] [data-action='download']").ClickAsync());
        Assert.Equal("result.txt", download.SuggestedFilename);
        await using (var stream = await download.CreateReadStreamAsync())
        {
            using var bytes = new MemoryStream();
            await stream!.CopyToAsync(bytes);
            Assert.Equal(Fixtures.Result().Exports.Single(x => x.Kind == ExportKind.PlainText).Bytes, bytes.ToArray());
        }
        Assert.Equal("https://doclang.ai/viewer/", await page.Locator(".viewer-link").GetAttributeAsync("href"));
        await page.Locator("#json-details summary").ClickAsync();
        Assert.Equal("JSON export details",
            await page.Locator("#json-details summary > span").First.InnerTextAsync());
        Assert.Equal("Inspect the OCR hierarchy, geometry, recognition metadata, and other records present in this document's native JSON export.",
            await page.Locator("#json-details .disclosure-content > p").InnerTextAsync());
        Assert.Equal(["Pages", "Text regions", "Lines", "Words", "Character records",
            "Position / geometry objects", "Tables", "Table cells"],
            await page.Locator("#json-facts .fact span").AllTextContentsAsync());
        await page.Locator("#doclang-details summary").ClickAsync();
        Assert.Equal("DocLang export details",
            await page.Locator("#doclang-details summary > span").First.InnerTextAsync());
        Assert.Equal("Inspect the structural elements present in this document's native DocLang export.",
            await page.Locator("#doclang-details .disclosure-content > p").InnerTextAsync());
        Assert.Equal(["Pages", "Tables", "Structural blocks", "Location elements"],
            await page.Locator("#doclang-facts .fact span").AllTextContentsAsync());
        Assert.Equal(["2", "1", "4", "2"], await page.Locator("#doclang-facts .fact strong").AllTextContentsAsync());
        Assert.Equal("DocLang specification: v0.4", await page.Locator("#doclang-specification").InnerTextAsync());
        var jsonDetailsBounds = await page.Locator("#json-details").BoundingBoxAsync();
        var docLangDetailsBounds = await page.Locator("#doclang-details").BoundingBoxAsync();
        Assert.Equal(jsonDetailsBounds!.Y, docLangDetailsBounds!.Y);
        await Screenshot(page, "presentation-details.png");
        await page.Locator("#doclang-details summary").ClickAsync();
        await page.Locator("#json-details summary").ClickAsync();
        await page.Locator("#settings-open").ClickAsync();
        await page.Locator("#settings-dialog").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal(SessionSettings.DefaultModel, await page.Locator("#model").InputValueAsync());
        await page.Locator("#api-key").FillAsync("test-" + Guid.NewGuid().ToString("N"));
        await page.Locator("#save-settings").ClickAsync();
        await Assertions.Expect(page.Locator("#settings-status")).ToHaveTextAsync("Settings saved.");
        Assert.Equal("", await page.Locator("#api-key").InputValueAsync());
        await page.Locator("#test-connection").ClickAsync();
        await Assertions.Expect(page.Locator("#connection-status")).ToHaveTextAsync("Connected successfully to OpenAI using " + SessionSettings.DefaultModel + ".");
        Assert.Equal(SessionSettings.DefaultModel, factory.AiProvider.ConnectionModel);
        factory.AiProvider.ConnectionError = new DemoException("Authentication failed. Check the configured OpenAI API key.");
        await page.Locator("#test-connection").ClickAsync();
        await Assertions.Expect(page.Locator("#connection-status")).ToHaveTextAsync("Authentication failed. Check the configured OpenAI API key.");
        factory.AiProvider.ConnectionError = null;
        await page.Locator("[data-settings-tab='experience']").ClickAsync();
        await page.Locator("#experience-preset").SelectOptionAsync("DocLangFocus");
        await Assertions.Expect(page.Locator("[data-demo-feature='showPlainText']")).Not.ToBeCheckedAsync();
        Assert.True(await page.Locator("[data-kind='plaintext']").IsHiddenAsync());
        Assert.True(await page.Locator("#ai-section").IsHiddenAsync());
        await page.Locator("#experience-preset").SelectOptionAsync("AiComparison");
        await Assertions.Expect(page.Locator("[data-demo-feature='enableAiTest']")).ToBeCheckedAsync();
        await Assertions.Expect(page.Locator("[data-demo-feature='enableCompareAll']")).ToBeCheckedAsync();
        Assert.False(await page.Locator("#ai-section").IsHiddenAsync());
        await page.Locator("#experience-preset").SelectOptionAsync("Technical");
        await Assertions.Expect(page.Locator("[data-demo-feature='showProcessingTime']")).ToBeCheckedAsync();
        Assert.True(await page.Locator("#experience-preset option[value='Custom']").IsDisabledAsync());
        string[] featureNames = [
            "showJson", "showPlainText", "showBenchmarkTokens", "showFileSizes", "showProcessingTime",
            "showTokenReduction", "showRepresentationAnalysis", "showDocLangViewer", "enableAiTest",
            "enableCompareAll", "showActualAiTokenUsage", "showResponseTime", "showAiResponses"
        ];
        foreach (var featureName in featureNames)
        {
            var input = page.Locator($"[data-demo-feature='{featureName}']");
            await input.Locator("xpath=..").ClickAsync();
            await Assertions.Expect(input).Not.ToBeCheckedAsync();
            await Assertions.Expect(input).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator("#experience-preset")).ToHaveValueAsync("Custom");
            if (featureName == "showJson") await Assertions.Expect(page.Locator("[data-kind='json']")).ToBeHiddenAsync();

            await input.FocusAsync();
            await input.PressAsync("Space");
            await Assertions.Expect(input).ToBeCheckedAsync();
            await Assertions.Expect(input).ToBeEnabledAsync();
            if (featureName == "showJson") await Assertions.Expect(page.Locator("[data-kind='json']")).ToBeVisibleAsync();

            await input.ClickAsync();
            await Assertions.Expect(input).Not.ToBeCheckedAsync();
            await Assertions.Expect(input).ToBeEnabledAsync();
            await input.ClickAsync();
            await Assertions.Expect(input).ToBeCheckedAsync();
            await Assertions.Expect(input).ToBeEnabledAsync();
        }
        Assert.Equal(1, factory.Processor.Calls);
        Assert.Empty(factory.AiProvider.Requests);
        await page.Locator("[data-close='settings-dialog']").ClickAsync();
        await page.Locator("#settings-open").ClickAsync();
        await page.Locator("[data-settings-tab='experience']").ClickAsync();
        foreach (var featureName in featureNames)
            await Assertions.Expect(page.Locator($"[data-demo-feature='{featureName}']")).ToBeCheckedAsync();
        await Assertions.Expect(page.Locator("#experience-preset")).ToHaveValueAsync("Custom");
        await page.Locator("#experience-preset").SelectOptionAsync("AiComparison");
        await page.Locator("[data-close='settings-dialog']").ClickAsync();
        await page.Locator("#ai-section summary").ClickAsync();
        Assert.True(await page.Locator("#ask").IsEnabledAsync());
        await page.Locator("#question").FillAsync("What is here?");
        var docLangRadio = page.Locator("input[name='ai-representation'][value='doclang']");
        await Assertions.Expect(docLangRadio).ToBeCheckedAsync();
        await docLangRadio.FocusAsync();
        await docLangRadio.PressAsync("ArrowRight");
        await Assertions.Expect(page.Locator("input[name='ai-representation'][value='plaintext']")).ToBeCheckedAsync();
        await page.Locator("input[name='ai-representation'][value='plaintext']").PressAsync("ArrowLeft");
        await Assertions.Expect(docLangRadio).ToBeCheckedAsync();
        await page.Locator("input[name='ai-representation'][value='json']").CheckAsync();
        await page.Locator("#ask").ClickAsync();
        await Assertions.Expect(page.Locator("#answer")).ToHaveTextAsync("Test answer.");
        await AssertSuccessfulMetrics(page, "#single-ai-metrics");
        await Assertions.Expect(page.Locator("#single-ai-metrics")).ToContainTextAsync("Representation: JSON");
        Assert.Equal(AiRepresentation.Json, factory.AiProvider.Requests.Last().Representation);
        Assert.True(await page.Locator("#comparison-results").IsHiddenAsync());
        var requestsBeforePresentationChanges = factory.AiProvider.Requests.Count;
        await page.Locator("#settings-open").ClickAsync();
        await page.Locator("[data-settings-tab='experience']").ClickAsync();
        await page.Locator("[data-demo-feature='showBenchmarkTokens']").ClickAsync();
        await Assertions.Expect(page.Locator("#single-ai-metrics")).Not.ToContainTextAsync("Benchmark tokens");
        await page.Locator("[data-demo-feature='showBenchmarkTokens']").ClickAsync();
        await page.Locator("[data-demo-feature='showActualAiTokenUsage']").ClickAsync();
        await Assertions.Expect(page.Locator("#single-ai-metrics")).Not.ToContainTextAsync("Actual input tokens");
        await page.Locator("[data-demo-feature='showActualAiTokenUsage']").ClickAsync();
        await page.Locator("[data-demo-feature='showResponseTime']").ClickAsync();
        await Assertions.Expect(page.Locator("#single-ai-metrics")).Not.ToContainTextAsync("AI response time");
        await page.Locator("[data-demo-feature='showResponseTime']").ClickAsync();
        await page.Locator("[data-demo-feature='showAiResponses']").ClickAsync();
        await Assertions.Expect(page.Locator("#answer")).ToBeHiddenAsync();
        await page.Locator("[data-demo-feature='showAiResponses']").ClickAsync();
        await Assertions.Expect(page.Locator("#answer")).ToBeVisibleAsync();
        await page.Locator("[data-close='settings-dialog']").ClickAsync();
        Assert.Equal(requestsBeforePresentationChanges, factory.AiProvider.Requests.Count);
        await page.Locator("input[name='ai-representation'][value='doclang']").CheckAsync();
        await page.Locator("#ask").ClickAsync();
        await Assertions.Expect(page.Locator("#answer")).ToHaveTextAsync("Test answer.");
        await AssertSuccessfulMetrics(page, "#single-ai-metrics");
        Assert.Equal(Fixtures.DocLang, factory.AiProvider.DocLang);
        Assert.DoesNotContain(Fixtures.Json, factory.AiProvider.DocLang!);
        Assert.DoesNotContain(Fixtures.Result().Exports.Single(export => export.Kind == ExportKind.PlainText).Content,
            factory.AiProvider.DocLang!);
        Assert.Equal(SessionSettings.DefaultModel, factory.AiProvider.AskModel);
        await page.Locator("input[name='ai-representation'][value='plaintext']").CheckAsync();
        await page.Locator("#ask").ClickAsync();
        await Assertions.Expect(page.Locator("#answer")).ToHaveTextAsync("Test answer.");
        await AssertSuccessfulMetrics(page, "#single-ai-metrics");
        Assert.Equal(AiRepresentation.PlainText, factory.AiProvider.Requests.Last().Representation);
        await page.Locator("#settings-open").ClickAsync();
        await page.Locator("#model").FillAsync("test-model");
        await page.Locator("#save-settings").ClickAsync();
        await Assertions.Expect(page.Locator("#settings-status")).ToHaveTextAsync("Settings saved.");
        await page.Locator("[data-close='settings-dialog']").ClickAsync();
        await page.Locator("#ask").ClickAsync();
        await Assertions.Expect(page.Locator("#answer")).ToHaveTextAsync("Test answer.");
        Assert.Equal("test-model", factory.AiProvider.AskModel);
        await page.Locator("#settings-open").ClickAsync();
        await page.Locator("[data-settings-tab='experience']").ClickAsync();
        await page.Locator("#experience-preset").SelectOptionAsync("AiComparison");
        await page.Locator("[data-close='settings-dialog']").ClickAsync();
        await page.Locator("#compare-all").ClickAsync();
        await Assertions.Expect(page.Locator("#comparison-table")).ToBeVisibleAsync();
        await AssertSuccessfulMetrics(page, "#comparison-table");
        Assert.Equal([AiRepresentation.Json, AiRepresentation.DocLang, AiRepresentation.PlainText],
            factory.AiProvider.Requests.TakeLast(3).Select(request => request.Representation));
        Assert.Equal(1, factory.Processor.Calls);
        factory.AiProvider.FailedRepresentations.Add(AiRepresentation.Json);
        await page.Locator("#compare-all").ClickAsync();
        await Assertions.Expect(page.Locator("#ai-status")).ToContainTextAsync("1 failed representation");
        await Assertions.Expect(page.Locator("#comparison-table")).ToContainTextAsync("JSON failed for this test");
        Assert.Equal(2, await page.Locator("#comparison-table [data-answer-index]").CountAsync());
        factory.AiProvider.FailedRepresentations.Clear();
        factory.AiProvider.Usage = null;
        await page.Locator("input[name='ai-representation'][value='json']").CheckAsync();
        await page.Locator("#ask").ClickAsync();
        await Assertions.Expect(page.Locator("#single-ai-metrics")).ToContainTextAsync("Representation: JSON");
        await Assertions.Expect(page.Locator("#single-ai-metrics")).ToContainTextAsync("Actual input tokens: Not reported");
        await AssertLocalMetricsWhenProviderUsageIsAbsent(page);
        factory.AiProvider.AskError = new DemoException("OpenAI rate limit reached. Try again shortly.");
        await page.Locator("#ask").ClickAsync();
        await Assertions.Expect(page.Locator("#ai-status")).ToHaveTextAsync("OpenAI rate limit reached. Try again shortly.");
        await page.Locator("#settings-open").ClickAsync();
        await page.Locator("#settings-dialog").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await page.Locator("#clear-key").ClickAsync();
        await Assertions.Expect(page.Locator("#settings-status")).ToHaveTextAsync("API key cleared.");
        using var assertionClient = factory.DemoClient();
        Assert.False((await assertionClient.GetFromJsonAsync<SettingsStatus>("/api/settings"))!.Configured);
        await page.Locator("[data-close='settings-dialog']").ClickAsync();
        Assert.True(await page.EvaluateAsync<bool>("localStorage.getItem('fineReaderDocLangTheme') === 'light' && sessionStorage.length === 0"));
        await page.SetViewportSizeAsync(390, 844);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= 390"));
        await Screenshot(page, "mobile-results.png");
        await page.SetViewportSizeAsync(1920, 1080);
        await page.Locator("#replace").ClickAsync();
        await page.Locator("#upload-ready").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        await page.Locator("#settings-open").ClickAsync();
        await page.Locator("#settings-dialog").WaitForAsync(new() { State = WaitForSelectorState.Visible });
        Assert.Equal("test-model", await page.Locator("#model").InputValueAsync());
        await page.Locator("[data-close='settings-dialog']").ClickAsync();
        Assert.Equal("—", await page.Locator("#pages").InnerTextAsync());
        Assert.Equal(1, factory.Processor.Calls);
        await page.RouteAsync("**/api/application", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = "{\"applicationVersion\":\"1.0.0\",\"contractVersion\":\"different\",\"buildId\":\"different\"}"
        }));
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.Locator("#application-version-error")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#application-version-message")).ToContainTextAsync("different application builds");
        Assert.True(await page.Locator("#settings-open").IsDisabledAsync());
        Assert.Empty(errors);
    }

    private static async Task Screenshot(IPage page, string name)
    {
        var root = Environment.GetEnvironmentVariable("DEMO_ARTIFACTS");
        if (string.IsNullOrEmpty(root)) return;
        Directory.CreateDirectory(root);
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, name), FullPage = true });
    }

    private static async Task AssertSuccessfulMetrics(IPage page, string selector)
    {
        var text = string.Join("\n", await page.Locator(selector).AllInnerTextsAsync());
        Assert.Contains("Document input size", text);
        Assert.Contains("Benchmark tokens", text);
        Assert.Contains("Actual input tokens", text);
        Assert.Contains("Actual output tokens", text);
        Assert.Contains("AI response time", text);
        Assert.DoesNotContain("Actual total", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cached input", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NaN", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Not reported", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unavailable", text, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertLocalMetricsWhenProviderUsageIsAbsent(IPage page)
    {
        var text = await page.Locator("#single-ai-metrics").InnerTextAsync();
        Assert.Contains("Document input size", text);
        Assert.Contains("Benchmark tokens", text);
        Assert.Contains("AI response time", text);
        Assert.Contains("Actual input tokens: Not reported", text);
        Assert.Contains("Actual output tokens: Not reported", text);
        Assert.DoesNotContain("NaN", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Document input size: Unavailable", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Benchmark tokens: Unavailable", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AI response time: Not reported", text, StringComparison.OrdinalIgnoreCase);
    }
}
