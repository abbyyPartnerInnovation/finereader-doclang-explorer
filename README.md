# FineReader Engine DocLang Demo

An independent local ABBYY Marketplace reference application that recognizes one document once with FineReader Engine, creates native JSON, DocLang, and Plain Text exports, and lets a user compare those complete exports as optional AI inputs. The separate `AbbyyDocLangJais` application is outside this solution.

The application reports measurements for the processed document without pricing, billing estimates, or claims that one representation is universally smaller or produces a better answer.

## What the demo shows

- One FineReader recognition pass followed by `FEF_JSON`, `FEF_DocLang`, and `FEF_TextUnicodeDefaults` exports from the same recognized document.
- Original native file sizes and local `o200k_base` benchmark-token counts.
- Document-specific JSON and DocLang structure analysis.
- Optional Single Ask using JSON, DocLang, or Plain Text; DocLang is selected by default.
- Optional Compare All in the fixed order JSON, DocLang, Plain Text.
- Provider-reported actual input/output usage when OpenAI supplies it.
- Light and dark themes with a browser-local theme preference.

## Requirements

1. Windows x64.
2. A .NET SDK targeting .NET 8 and the ASP.NET Core 8 runtime.
3. Microsoft Edge for the browser regression suite.
4. FineReader Engine 12.8.2 Windows x64 or later, matching `FREngine.DotNet.Interop.dll`, native DocLang support, and a valid ABBYY licence.
5. Network access for package restore. OpenAI network access is used only after an explicit connection test, Ask, or Compare All action.

Supported input: PDF, PNG, JPG/JPEG, TIF/TIFF, and BMP.

## Configure FineReader once

From `FineReader-DocLang-Demo`:

```powershell
.\tools\Setup-FineReader.ps1
Copy-Item .\src\Abbyy.DocLang.Demo.Web\appsettings.Local.example.json .\src\Abbyy.DocLang.Demo.Web\appsettings.Local.json
dotnet restore .\Abbyy.DocLang.Demo.sln
```

Edit only the new `appsettings.Local.json` with the local FineReader runtime, customer project, licence, language, and processing settings. The file is ignored by Git and excluded from publish output. `Setup-FineReader.ps1` copies only the matching FineReader .NET wrapper into this solution's `vendor` directory.

The application starts without FineReader credentials and reports the missing setup instead of simulating OCR.

## Build and run the current complete application

Use the single supported development/start command:

```powershell
.\tools\Run-Demo.ps1
```

The script:

1. validates the package-assets file and restores packages first when it is missing, malformed, or records a restore error;
2. checks port 5188;
3. stops only a previous `Abbyy.DocLang.Demo.Web` process launched from this project;
4. refuses to stop an unrelated process;
5. builds the complete Release application; and
6. starts it at `http://localhost:5188`.

It deliberately does not use `--no-build`. A failed restore does not stop an already running valid demo. The UI files are embedded in the compiled web assembly, so editing a source `wwwroot` file cannot silently update the browser while an older backend remains loaded.

Open `http://localhost:5188` after the build completes.

### Verify the running build

The footer shows a short non-sensitive application version/build identifier. The full identity is available locally:

```powershell
Invoke-RestMethod http://localhost:5188/api/application
```

The returned values are:

- `applicationVersion` — assembly version;
- `contractVersion` — frontend/API compatibility version;
- `buildId` — deterministic assembly module identity for the exact build.

The server injects the same contract and build identity into the delivered page. Every subsequent API call carries both values. A missing or mismatched value receives `409 application_version_mismatch`, and the page displays an explicit rebuild/restart banner instead of rendering missing metrics or reverting controls.

The identity contains no username, path, key, licence, or machine-specific secret.

## Create and verify a distributable

```powershell
.\tools\Publish-Demo.ps1
```

This command runs the full Release test suite, publishes to a new timestamped directory under `artifacts/releases`, launches that published executable on an isolated local port, verifies frontend/backend identity and current API fields, then creates a ZIP archive beside the directory.

`appsettings.Local.json` is never included. To run an extracted release, copy `appsettings.Local.example.json` to `appsettings.Local.json` beside the executable and configure that installation.

The release can be checked independently:

```powershell
.\tools\Verify-Release.ps1 -ApplicationDirectory .\artifacts\releases\FineReader-DocLang-Demo-YYYYMMDD-HHMMSS
```

## Architecture and native-export preservation

- `FineReaderProcessor`, `StaWorker`, and `SinglePassExporter` initialize the native wrapper on one STA thread, recognize once, and export JSON, DocLang, and Unicode text sequentially.
- `DocumentService` serializes processing and atomically retains the latest successful result in memory. Failed replacement does not install a partial result.
- Each `NativeExport` retains its original bytes and a BOM-aware decoded string. Downloads return the original native bytes; Copy, Expand, benchmarking, analysis, and AI use the complete decoded export, never the shortened preview.
- `BenchmarkTokenizer` records `o200k_base` counts during processing. AI operations reuse those stored counts and never reprocess, regenerate, or retokenize the document.
- `SessionSettings` holds the API key/model in server memory. `DemoFeatureService` holds presentation features separately. `ApplicationSettingsService` applies one settings domain per revision and rejects stale writes.
- Static UI assets are embedded in the same compiled assembly as the API. The root page is stamped with that assembly's build identity at runtime.

Processing time covers native document open/load, recognition, and all three native exports. It excludes upload, engine startup, analysis, token benchmarking, and AI.

## AI requests

AI is optional and occurs only after an explicit user action and after a document has been processed.

### Single Ask

The browser sends `POST /api/ask` with `documentId`, `question`, and exactly one `representation`: `json`, `doclang`, or `plaintext`. The response repeats the document ID and representation. The browser refuses to display an answer whose IDs do not match the request.

Every successful result compactly shows:

- representation used;
- Document input size;
- benchmark tokens;
- actual input tokens or `Not reported`;
- actual output tokens or `Not reported`;
- AI response time; and
- answer.

These are the Standard/default presentation. An Experience switch can deliberately hide its corresponding metric or answer without changing the measurements returned by the API.

### Compare All

`POST /api/ai/compare` captures the document, trimmed question, model, credentials, fixed instructions, and output limit once, then awaits requests in this order:

1. JSON
2. DocLang
3. Plain Text

Only the complete document representation changes. FineReader is not called and benchmark tokens are not recalculated. Provider failures represented by safe `DemoException` messages remain as failed table columns while later representations continue.

The comparison table shows status, Document input size, benchmark tokens, actual input/output tokens, response time, and View answer controls. It declares no winner and applies no score. While the single server response is pending, the browser honestly says only `Comparing JSON, DocLang and Plain Text...`.

### Measurement definitions

- **Native export file size** is the byte length of the original native export file.
- **Document input size** is `Encoding.UTF8.GetByteCount` over the complete decoded native representation supplied as the document value to the AI workflow. It intentionally excludes the grounding instructions, representation prefix, user question, HTTP headers, and JSON envelope. It is therefore a representation-comparison metric, not total provider-request size.
- **Benchmark tokens** are the stored local `o200k_base` count for that complete representation.
- **Actual input/output tokens** come only from the provider's `usage` object. They are never estimated or replaced by benchmark counts.
- **AI response time** is measured locally from immediately before HTTP dispatch through reading and parsing the complete provider response body.

Document input size, benchmark tokens, and response time are required for a successful operation. If they are absent or invalid, the browser reports an application contract problem rather than `Unavailable` or `Not reported`. Provider usage remains optional.

The provider parser also retains cached-input and total-token values when reported, but they are not primary UI metrics. The implementation follows the [OpenAI Responses API reference](https://developers.openai.com/api/reference/cli/resources/responses/methods/create) and sends `store=false` for document requests and Test Connection.

## Settings and experiences

Settings remains available before processing, during processing, after Replace, and after AI errors. Saving settings never invokes FineReader or OpenAI. Test Connection is a separate explicit action and sends only the configured model plus fixed text `Connection check.` with `store=false` and a one-token output limit.

The default model is defined by `SessionSettings.DefaultModel` and is currently `gpt-5.6-terra`. Model availability depends on the configured OpenAI account and should be confirmed with Test Connection.

Experience controls are presentation-only:

- Core: Show JSON, Show Plain Text, benchmark tokens, file sizes, processing time, token reduction.
- DocLang: representation analysis and DocLang Viewer link.
- AI: Ask, Compare All, actual token usage, response time, and responses.

| Preset | Behavior |
| --- | --- |
| Standard (default) | Core comparison, analysis/viewer, Ask, provider usage, response time, and responses on; Compare All off |
| DocLang Focus | Document representation details with AI off |
| AI Comparison | All comparison controls/metrics and responses on; processing time off |
| Technical | Every implemented feature on |
| Custom | Read-only preset label produced by manual switch changes; it cannot be intentionally selected |

Switches use native checkboxes and work by switch click, label click, and keyboard. Settings writes carry the last observed server revision. Same-tab writes are queued in user order; a cross-tab revision conflict refreshes current settings and retries the user's specific change once.

## Shared state and multiple tabs

The application is a local, single-presenter demo. The server shares one current document, API key/model, and Experience configuration across tabs.

- Settings are refreshed whenever Settings opens and before an AI action.
- Stale settings writes are rejected by revision and retried against current state.
- AI responses include the requested document ID and representation and are validated before display.
- The browser checks that the server document is still current before displaying a completed AI response.
- Replacing a document in one tab can make another tab's displayed document stale; that tab receives an explicit stale-document message on its next AI action and should reload.

## Theme and accessibility

The theme toggle changes CSS design tokens between light and dark and preserves only `fineReaderDocLangTheme` in local storage. It does not touch documents, settings, OCR, or AI.

Settings tabs support Left/Right Arrow, Home, and End. Switches and representation radio buttons remain native keyboard controls, selected states have semantic checked/selected state, and focus remains visibly outlined.

## Security and local operation

- The listener and middleware accept localhost requests only and validate allowed hosts.
- API calls require the matching application contract/build identity. Mutations additionally require the demo request header and same-origin validation.
- CSP blocks framing and external page resources. Responses use `no-store`, `nosniff`, and `no-referrer` headers.
- API keys are not written to configuration, disk, logs, cookies, local/session storage, GET responses, exceptions, or generated release artifacts.
- Test Connection never sends document content. Document AI requests happen only after an explicit action and use `store=false`.
- Document bodies, provider bodies, native errors, credentials, stack traces, and local paths are not logged or returned.
- Temporary OCR files use generated OS-temporary directories and are removed on normal completion, failure, and cancellation.

This is not a multi-user service. It has no accounts, database, durable document repository, telemetry, or tenant isolation.

## Tests

Run the complete suite:

```powershell
dotnet test .\Abbyy.DocLang.Demo.sln -c Debug
```

Browser tests run by default with installed Microsoft Edge. They exercise the real embedded page and serialized HTTP endpoints through a local Kestrel proxy while replacing only FineReader and OpenAI with fakes. No live FineReader recognition or OpenAI call occurs.

Coverage includes:

- build/contract identity and mismatch rejection;
- current settings schema with obsolete pricing/input flags rejected;
- every Experience switch via label, keyboard, and direct click;
- preset and session retention behavior;
- stale settings revisions and atomic domain updates;
- JSON, DocLang, Plain Text, and Compare All representation selection;
- required local metrics and optional provider usage;
- partial comparison failure;
- OpenAI response parsing and `store=false`;
- one recognition pass, complete exports, cleanup, limits, and original-byte downloads;
- theme persistence, accessibility basics, and responsive layout; and
- a mismatched frontend/backend browser failure state.

`Publish-Demo.ps1` reruns these tests in Release and verifies the published executable itself before creating the ZIP.

## Known limitations

- A valid local FineReader licence is required for live OCR.
- OpenAI requires a reachable endpoint, valid key, and model access.
- Complete exports can exceed provider context limits.
- Provider usage fields are optional and remain `Not reported` when absent.
- Compare All is sequential, has no cancel button, and is a demonstration rather than a statistically controlled model evaluation.
- Forced termination can leave FineReader temporary job directories; normal lifecycle paths clean them.
- Server restart clears documents, credentials, Experience state, and its settings revision. Theme preference remains in the browser.
