"use strict";
const $ = (selector) => document.querySelector(selector);
const all = (selector) => [...document.querySelectorAll(selector)];
const themeStorageKey = "fineReaderDocLangTheme";
const pageIdentity = Object.freeze({
  applicationVersion: document.querySelector('meta[name="application-version"]')?.content || "",
  contractVersion: document.querySelector('meta[name="application-contract"]')?.content || "",
  buildId: document.querySelector('meta[name="application-build"]')?.content || ""
});
let selectedFile = null;
let result = null;
let settings = { configured: false, model: "", revision: 0, demoFeatures: null };
let uploadLimit = 50 * 1024 * 1024;
let processing = false;
let asking = false;
let comparing = false;
let testingConnection = false;
let singleAiOperation = null;
let comparisonVisible = false;
let pendingSettingsOperations = 0;
let settingsQueue = Promise.resolve();
let applicationCompatible = false;
let toastTimer;
const isFiniteNumber = (value) => typeof value === "number" && Number.isFinite(value);
const number = (value) => isFiniteNumber(value) ? value.toLocaleString("en-US") : "Unavailable";
function fileSize(bytes) {
  if (!isFiniteNumber(bytes) || bytes < 0) return "Unavailable";
  return bytes < 1024 ? number(bytes) + " B"
    : bytes < 1024 * 1024 ? (bytes / 1024).toFixed(1) + " KB" : (bytes / 1024 / 1024).toFixed(2) + " MB";
}

function setTheme(theme) {
  const dark = theme === "dark";
  document.documentElement.dataset.theme = dark ? "dark" : "light";
  $("#theme-toggle").textContent = dark ? "Light" : "Dark";
  $("#theme-toggle").setAttribute("aria-label", dark ? "Switch to light theme" : "Switch to dark theme");
  try { localStorage.setItem(themeStorageKey, dark ? "dark" : "light"); } catch { /* storage may be unavailable */ }
}
try { setTheme(localStorage.getItem(themeStorageKey) === "dark" ? "dark" : "light"); } catch { setTheme("light"); }
$("#theme-toggle").addEventListener("click", () => setTheme(document.documentElement.dataset.theme === "dark" ? "light" : "dark"));

class DemoApiError extends Error {
  constructor(message, status = 0, code = null) { super(message); this.status = status; this.code = code; }
}
function showApplicationMismatch(message) {
  applicationCompatible = false;
  document.documentElement.dataset.applicationCompatible = "false";
  $("#application-version-message").textContent = message || "Reload the page. If this message remains, stop the demo, rebuild it, and start it again.";
  $("#application-version-error").hidden = false;
  $("#settings-open").disabled = true;
  all("main button, main input, main select").forEach(control => { control.disabled = true; });
}
function contractFailure(message) {
  showApplicationMismatch(message);
  return new DemoApiError(message, 409, "application_version_mismatch");
}
async function verifyApplicationIdentity() {
  if (!pageIdentity.contractVersion || !pageIdentity.buildId || pageIdentity.buildId.startsWith("__APP_"))
    throw contractFailure("The page was not loaded from a complete application build. Stop the demo, rebuild it, and start it again.");
  let response;
  try { response = await fetch("/api/application", { cache: "no-store" }); }
  catch { throw contractFailure("The application backend could not be reached. Check that the current build is running."); }
  const identity = await response.json().catch(() => null);
  if (!response.ok || !identity
      || identity.contractVersion !== pageIdentity.contractVersion
      || identity.buildId !== pageIdentity.buildId)
    throw contractFailure("The frontend and backend are different application builds. Stop the demo, rebuild it, and start it again.");
  applicationCompatible = true;
  document.documentElement.dataset.applicationCompatible = "true";
  $("#application-version-error").hidden = true;
  $("#build-identity").textContent = "Version " + identity.applicationVersion + " | Build " + identity.buildId.slice(0, 8);
  $("#build-identity").title = "Contract " + identity.contractVersion + " | Build " + identity.buildId;
}
async function api(path, options = {}) {
  if (!applicationCompatible && path !== "/api/application")
    throw contractFailure("The application build has not been verified. Reload the page or rebuild and restart the demo.");
  let response;
  try { response = await fetch(path, { ...options, headers: {
    "X-Demo-Request": "1",
    "X-Demo-Contract": pageIdentity.contractVersion,
    "X-Demo-Build": pageIdentity.buildId,
    ...options.headers
  } }); }
  catch { throw new DemoApiError("The demo server could not be reached. Check that it is running."); }
  const data = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) {
    const error = new DemoApiError(data?.error || "The request could not be completed.", response.status, data?.code);
    if (error.code === "application_version_mismatch") showApplicationMismatch(error.message);
    throw error;
  }
  return data;
}
function showStatus(selector, message = "", error = false) {
  const target = $(selector);
  target.textContent = message;
  target.classList.toggle("error", error);
}
function toast(message) {
  $("#toast").textContent = message;
  $("#toast").hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $("#toast").hidden = true; }, 2500);
}
function features() { return settings.demoFeatures?.features ?? {}; }
function aiEnabled() { return features().enableAiTest ?? true; }
function comparisonEnabled() { return features().enableCompareAll ?? false; }
function syncAi() {
  const ready = aiEnabled() && settings.configured && !!result && !asking && !comparing && !processing;
  $("#ask").disabled = !ready;
  $("#compare-all").disabled = !ready || !comparisonEnabled();
  $("#ai-setup").textContent = settings.configured
    ? "Model: " + settings.model + (result ? " - Ready for your question." : " - Process a document to ask questions about it.")
    : "Configure an API key and model in Settings to enable this optional test.";
}
function applyDemoFeatures(status) {
  if (!status?.features) return;
  settings.demoFeatures = status;
  const active = status.features;
  all("[data-feature]").forEach(element => { element.hidden = !active[element.dataset.feature]; });
  all(".export-metrics").forEach(metrics => {
    metrics.hidden = [...metrics.querySelectorAll("[data-feature]")].every(element => element.hidden);
  });
  renderSingleMetrics();
  $("#answer").hidden = !active.showAiResponses || !singleAiOperation;
  $("#comparison-results").hidden = !active.enableCompareAll || !comparisonVisible;
  syncAi();
}
function renderExperienceSettings() {
  const status = settings.demoFeatures;
  const available = !!status?.features;
  const busy = pendingSettingsOperations > 0;
  $("#experience-preset").disabled = !available || busy;
  if (available) $("#experience-preset").value = status.preset;
  all("[data-demo-feature]").forEach(input => {
    input.disabled = !available || busy;
    if (available) input.checked = !!status.features[input.dataset.demoFeature];
  });
}
function updateAiSettingsUi() {
  $("#model").value = settings.model;
  $("#key-help").textContent = settings.configured
    ? "API key configured. Leave this blank to keep it, or enter a replacement."
    : "Kept in server memory for this running session only.";
  const busy = pendingSettingsOperations > 0;
  $("#clear-key").disabled = !settings.configured || busy;
  $("#save-settings").disabled = busy;
  $("#test-connection").disabled = !settings.configured || testingConnection || busy;
  syncAi();
}
function selectSettingsTab(panelId, focus = false) {
  all("[data-settings-tab]").forEach(tab => {
    const selected = tab.dataset.settingsTab === panelId;
    tab.classList.toggle("active", selected);
    tab.setAttribute("aria-selected", String(selected));
    tab.tabIndex = selected ? 0 : -1;
    if (selected && focus) tab.focus();
  });
  all(".settings-panel").forEach(panel => { panel.hidden = panel.id !== panelId; });
}
function applySettingsSnapshot(snapshot) {
  settings = snapshot;
  applyDemoFeatures(settings.demoFeatures);
  updateAiSettingsUi();
  renderExperienceSettings();
}
async function refreshSettingsNow() {
  settings = await api("/api/settings");
  applySettingsSnapshot(settings);
  return settings;
}
function queueSettingsOperation(operation) {
  pendingSettingsOperations++;
  updateAiSettingsUi(); renderExperienceSettings();
  const run = settingsQueue.then(operation, operation);
  settingsQueue = run.catch(() => {});
  return run.finally(() => {
    pendingSettingsOperations--;
    updateAiSettingsUi(); renderExperienceSettings();
  });
}
async function updateSettingsNow(update) {
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      const snapshot = await api("/api/settings", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ...update, expectedRevision: settings.revision }) });
      applySettingsSnapshot(snapshot);
      return snapshot;
    } catch (error) {
      if (error.code !== "settings_revision_conflict" || attempt > 0) throw error;
      await refreshSettingsNow();
    }
  }
  throw new DemoApiError("The settings change could not be applied.");
}

function selectFile(file) {
  if (!file || processing) return;
  if (!/\.(pdf|png|jpe?g|tiff?|bmp)$/i.test(file.name)) {
    showStatus("#status", "Choose a PDF, PNG, JPEG, TIFF, or BMP document.", true); return;
  }
  if (!file.size || file.size > uploadLimit) {
    showStatus("#status", "Choose a nonempty document up to " + fileSize(uploadLimit) + ".", true); return;
  }
  selectedFile = file;
  $("#file-label").replaceChildren(document.createTextNode(file.name + " - " + fileSize(file.size) + " "));
  const change = document.createElement("button");
  change.className = "text-button"; change.textContent = "change";
  change.addEventListener("click", () => $("#file-input").click());
  $("#file-label").append(change);
  $("#process").disabled = false;
  showStatus("#status");
}
$("#browse").addEventListener("click", () => $("#file-input").click());
$("#file-input").addEventListener("change", (event) => selectFile(event.target.files[0]));
const dropZone = $("#drop-zone");
for (const eventName of ["dragenter", "dragover"]) dropZone.addEventListener(eventName, (event) => {
  event.preventDefault(); if (!processing) dropZone.classList.add("dragging");
});
for (const eventName of ["dragleave", "drop"]) dropZone.addEventListener(eventName, (event) => {
  event.preventDefault(); dropZone.classList.remove("dragging");
  if (eventName === "drop") {
    if (event.dataTransfer.files.length !== 1) showStatus("#status", "Drop one document at a time.", true);
    else selectFile(event.dataTransfer.files[0]);
  }
});
window.addEventListener("dragover", (event) => event.preventDefault());
window.addEventListener("drop", (event) => event.preventDefault());

$("#process").addEventListener("click", async () => {
  if (!selectedFile || processing) return;
  processing = true; $("#process").disabled = true; $("#process").textContent = "Processing...";
  $("#file-input").disabled = true; $("#upload-ready").setAttribute("aria-busy", "true"); syncAi();
  showStatus("#status", "Recognizing the document and creating all three native exports...");
  try {
    result = await api("/api/process", { method: "POST", headers: {
      "Content-Type": "application/octet-stream", "X-File-Name": encodeURIComponent(selectedFile.name)
    }, body: selectedFile });
    renderResult(); showStatus("#status");
  } catch (error) { showStatus("#status", error.message, true); }
  finally {
    processing = false; $("#process").disabled = !selectedFile; $("#process").textContent = "Process ->";
    $("#file-input").disabled = false; $("#upload-ready").removeAttribute("aria-busy"); syncAi();
  }
});

function renderFacts(selector, analysis) {
  const container = $(selector); container.replaceChildren();
  if (analysis.unavailable) { const message = document.createElement("p"); message.className = "muted"; message.textContent = analysis.unavailable; container.append(message); return; }
  for (const fact of analysis.facts) {
    const card = document.createElement("div"); card.className = "fact";
    const label = document.createElement("span"); label.textContent = fact.label;
    const value = document.createElement("strong"); value.textContent = fact.value;
    card.append(label, value); container.append(card);
  }
}
function clearAiResults() {
  singleAiOperation = null; comparisonVisible = false;
  $("#answer").hidden = true; $("#answer").textContent = "";
  $("#single-ai-metrics").replaceChildren(); $("#single-ai-metrics").hidden = true;
  $("#comparison-results").hidden = true; $("#comparison-cards").replaceChildren();
  showStatus("#ai-status");
}
function renderResult() {
  $("#upload-ready").hidden = true; $("#upload-complete").hidden = false;
  $("#document-label").textContent = result.fileName + " - " + number(result.pages) + (result.pages === 1 ? " page" : " pages") + " - " + result.processingSeconds.toFixed(2) + " s";
  $("#pages").textContent = number(result.pages); $("#processing").textContent = result.processingSeconds.toFixed(2) + " s";
  $("#json-tokens").textContent = number(result.exports.find(x => x.kind === "json").tokens);
  $("#doclang-tokens").textContent = number(result.exports.find(x => x.kind === "doclang").tokens);
  $("#reduction").textContent = result.reductionPercent === null ? "N/A" : result.reductionPercent.toFixed(1) + "%";
  $(".reduction").classList.toggle("negative", result.reductionPercent < 0);
  $("#reduction-note").textContent = result.reductionPercent < 0 ? "DocLang uses more tokens" : "in benchmark tokens";
  for (const representation of result.exports) {
    const panel = $('.representation[data-kind="' + representation.kind + '"]');
    panel.querySelector("[data-tokens]").textContent = number(representation.tokens) + " tokens";
    panel.querySelector("[data-size]").textContent = fileSize(representation.fileBytes);
    panel.querySelector("[data-preview]").textContent = representation.preview || "(Empty native export)";
    panel.querySelector("[data-preview]").classList.remove("empty");
    const note = panel.querySelector("[data-note]"); const defaultNote = note.dataset.defaultText ??= note.textContent;
    note.textContent = representation.previewTruncated ? (note.hasAttribute("data-supporting") ? defaultNote + " " : "") + "Preview shortened - Copy, Download, and Expand use the complete export" : defaultNote;
    panel.querySelectorAll("button[data-action]").forEach(button => { button.disabled = false; });
    const download = panel.querySelector('[data-action="download"]'); download.href = exportPath(representation.kind, "download"); download.setAttribute("download", representation.fileName); download.removeAttribute("aria-disabled");
  }
  const jsonLabels = new Set(["Pages", "Text regions", "Lines", "Words", "Character records", "Position / geometry objects", "Tables", "Table cells"]);
  renderFacts("#json-facts", { ...result.jsonDetails, facts: result.jsonDetails.facts.filter(fact => jsonLabels.has(fact.label)) });
  const docLangDetails = result.docLangDetails;
  renderFacts("#doclang-facts", { ...docLangDetails, facts: [{ label: "Pages", value: number(result.pages) }, ...["Tables", "Structural blocks", "Location elements"].map(label => docLangDetails.facts.find(fact => fact.label === label)).filter(Boolean) ] });
  const specification = docLangDetails.facts.find(fact => fact.label === "Specification version");
  $("#doclang-specification").hidden = !specification || !!docLangDetails.unavailable;
  $("#doclang-specification").textContent = specification ? "DocLang specification: v" + specification.value.replace(/^v/i, "") : "";
  clearAiResults(); syncAi();
}
function exportPath(kind, action) {
  const path = "/api/exports/" + result.id + "/" + kind + "/" + action;
  return action === "download" ? path + "?contract=" + encodeURIComponent(pageIdentity.contractVersion) + "&build=" + encodeURIComponent(pageIdentity.buildId) : path;
}

$("#replace").addEventListener("click", async () => {
  $("#replace").disabled = true;
  try { await api("/api/result", { method: "DELETE" }); location.reload(); }
  catch (error) { showStatus("#status", error.message, true); $("#replace").disabled = false; }
});
for (const panel of all(".representation")) panel.addEventListener("click", async (event) => {
  const button = event.target.closest("button[data-action]");
  if (!button || !result || button.disabled) return;
  const kind = panel.dataset.kind; button.disabled = true;
  try {
    const content = (await api(exportPath(kind, "content"))).content;
    if (button.dataset.action === "copy") { await navigator.clipboard.writeText(content); toast("Complete " + result.exports.find(x => x.kind === kind).label + " export copied"); }
    else { $("#expand-title").textContent = result.exports.find(x => x.kind === kind).label; $("#expanded-content").textContent = content; $("#expand-dialog").showModal(); $("#expanded-content").scrollTop = 0; }
  } catch (error) { showStatus("#status", error.message || "Copy could not access the clipboard. Use Download instead.", true); }
  finally { button.disabled = false; }
});
all("[data-close]").forEach(button => button.addEventListener("click", () => $("#" + button.dataset.close).close()));
$("#expand-dialog").addEventListener("close", () => { $("#expanded-content").textContent = ""; });
$("#ai-answer-dialog").addEventListener("close", () => { $("#ai-answer-content").textContent = ""; });
$("#settings-dialog").addEventListener("close", () => { $("#api-key").value = ""; showStatus("#connection-status"); });
all("[data-settings-tab]").forEach(tab => {
  tab.addEventListener("click", () => selectSettingsTab(tab.dataset.settingsTab));
  tab.addEventListener("keydown", event => {
    const tabs = all("[data-settings-tab]");
    const current = tabs.indexOf(tab);
    let next = null;
    if (event.key === "ArrowRight") next = (current + 1) % tabs.length;
    else if (event.key === "ArrowLeft") next = (current - 1 + tabs.length) % tabs.length;
    else if (event.key === "Home") next = 0;
    else if (event.key === "End") next = tabs.length - 1;
    if (next !== null) { event.preventDefault(); selectSettingsTab(tabs[next].dataset.settingsTab, true); }
  });
});

async function updateExperience(update) {
  try {
    await queueSettingsOperation(() => updateSettingsNow(update));
    showStatus("#settings-status");
  } catch (error) {
    try { await refreshSettingsNow(); } catch { /* the original error remains actionable */ }
    showStatus("#settings-status", error.message, true);
  }
}
$("#experience-preset").addEventListener("change", async () => { await updateExperience({ experiencePreset: $("#experience-preset").value }); });
all("[data-demo-feature]").forEach(input => input.addEventListener("change", async () => {
  const optimistic = { ...settings.demoFeatures.features, [input.dataset.demoFeature]: input.checked };
  applyDemoFeatures({ preset: "Custom", features: optimistic });
  await updateExperience({ demoFeature: input.dataset.demoFeature, demoFeatureEnabled: input.checked });
}));

$("#settings-open").addEventListener("click", async () => {
  $("#api-key").value = ""; showStatus("#settings-status"); showStatus("#connection-status");
  try { await queueSettingsOperation(refreshSettingsNow); selectSettingsTab("ai-configuration"); $("#settings-dialog").showModal(); }
  catch (error) { showStatus("#status", error.message, true); }
});
$("#settings-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const update = { apiKey: $("#api-key").value, model: $("#model").value };
  try { await queueSettingsOperation(() => updateSettingsNow(update)); $("#api-key").value = ""; showStatus("#settings-status", "Settings saved."); }
  catch (error) { showStatus("#settings-status", error.message, true); }
  finally { $("#api-key").value = ""; }
});
$("#clear-key").addEventListener("click", async () => {
  $("#api-key").value = "";
  try { await queueSettingsOperation(() => updateSettingsNow({ clearKey: true })); showStatus("#settings-status", "API key cleared."); showStatus("#connection-status"); }
  catch (error) { showStatus("#settings-status", error.message, true); }
});
$("#test-connection").addEventListener("click", async () => {
  if (!settings.configured || testingConnection) return;
  testingConnection = true; updateAiSettingsUi(); showStatus("#connection-status", "Testing OpenAI connection...");
  try { await settingsQueue; const connection = await api("/api/ai/test-connection", { method: "POST" }); showStatus("#connection-status", "Connected successfully to OpenAI using " + connection.model + "."); }
  catch (error) { showStatus("#connection-status", error.message, true); }
  finally { testingConnection = false; updateAiSettingsUi(); }
});

function selectedRepresentation() { return document.querySelector('input[name="ai-representation"]:checked').value; }
function metricText(value, unavailable = "Not reported") { return isFiniteNumber(value) ? number(value) : unavailable; }
function operationValue(operation, property) { return operation?.[property]; }
function aiInputSize(operation) { return fileSize(operationValue(operation, "aiInputBytes")); }
function benchmarkTokens(operation) { return number(operationValue(operation, "benchmarkTokens")); }
function actualTokens(operation, property) { return metricText(operationValue(operation, property)); }
function responseTime(operation) {
  const milliseconds = operationValue(operation, "responseMilliseconds");
  return isFiniteNumber(milliseconds) ? number(milliseconds) + " ms" : "Not reported";
}
function sameDocument(left, right) { return typeof left === "string" && typeof right === "string" && left.toLowerCase() === right.toLowerCase(); }
function validateOptionalMetric(operation, property) {
  const value = operationValue(operation, property);
  if (value !== null && value !== undefined && (!isFiniteNumber(value) || value < 0))
    throw contractFailure("The AI response contains invalid " + property + " metadata. Rebuild and restart the application.");
}
function validateOperation(operation, expectedRepresentation, expectedDocumentId, allowFailure = false) {
  if (!operation || operation.representation !== expectedRepresentation || !sameDocument(operation.documentId, expectedDocumentId))
    throw contractFailure("The AI response does not match the selected document representation. Rebuild and restart the application.");
  if (!isFiniteNumber(operation.aiInputBytes) || operation.aiInputBytes < 0
      || !isFiniteNumber(operation.benchmarkTokens) || operation.benchmarkTokens < 0)
    throw contractFailure("Required local AI measurements are missing. Rebuild and restart the application.");
  const failed = !!operation.error || operation.status === "failed";
  if (failed && !allowFailure)
    throw new DemoApiError(operation.error || "The AI request failed.");
  if (!failed && (!isFiniteNumber(operation.responseMilliseconds) || operation.responseMilliseconds < 0))
    throw contractFailure("The successful AI response is missing its locally measured duration. Rebuild and restart the application.");
  if (!failed && typeof operation.text !== "string")
    throw contractFailure("The successful AI response is missing its answer. Rebuild and restart the application.");
  for (const property of ["actualInputTokens", "actualCachedInputTokens", "actualOutputTokens", "actualTotalTokens"])
    validateOptionalMetric(operation, property);
  return operation;
}
function renderSingleResult(operation) {
  singleAiOperation = operation;
  renderSingleMetrics();
  $("#answer").textContent = operationValue(operation, "text") || "";
  $("#answer").hidden = !(features().showAiResponses ?? true);
  showStatus("#ai-status", operationValue(operation, "incomplete") ? "The answer reached the output limit and may be incomplete." : "");
}
function renderSingleMetrics() {
  const metadata = $("#single-ai-metrics"); metadata.replaceChildren();
  if (!singleAiOperation) { metadata.hidden = true; return; }
  const operation = singleAiOperation;
  const summary = document.createElement("p"); summary.className = "single-ai-summary";
  const parts = ["Representation: " + operation.label, "Document input size: " + aiInputSize(operation)];
  if (features().showBenchmarkTokens ?? true) parts.push("Benchmark tokens: " + benchmarkTokens(operation));
  if (features().showActualAiTokenUsage) {
    parts.push("Actual input tokens: " + actualTokens(operation, "actualInputTokens"));
    parts.push("Actual output tokens: " + actualTokens(operation, "actualOutputTokens"));
  }
  if (features().showResponseTime) parts.push("AI response time: " + responseTime(operation));
  summary.textContent = parts.join(" · ");
  if (parts.length) metadata.append(summary);
  metadata.hidden = !parts.length;
}
function comparisonState(operation) {
  if (operation.error) return "Failed";
  return operation.incomplete ? "Incomplete" : "Complete";
}
function appendComparisonRow(body, label, feature, operations, value) {
  const row = document.createElement("tr");
  if (feature) row.dataset.feature = feature;
  const heading = document.createElement("th"); heading.scope = "row"; heading.textContent = label; row.append(heading);
  operations.forEach((operation, index) => {
    const cell = document.createElement("td");
    const rendered = value(operation, index);
    if (rendered instanceof Node) cell.append(rendered); else cell.textContent = rendered;
    row.append(cell);
  });
  body.append(row);
}
function renderComparison(response) {
  comparisonVisible = true;
  const container = $("#comparison-cards"); container.replaceChildren();
  const operations = response.results || [];
  const table = document.createElement("table"); table.id = "comparison-table"; table.className = "comparison-table";
  const head = document.createElement("thead"); const headerRow = document.createElement("tr");
  const metricHeader = document.createElement("th"); metricHeader.scope = "col"; metricHeader.textContent = "Metric"; headerRow.append(metricHeader);
  operations.forEach(operation => {
    const heading = document.createElement("th"); heading.scope = "col"; heading.textContent = operation.label; headerRow.append(heading);
  });
  head.append(headerRow); table.append(head);
  const body = document.createElement("tbody");
  appendComparisonRow(body, "Status", null, operations, operation => {
    const error = operation.error;
    return error ? "Failed: " + error : comparisonState(operation) + (operation.model ? " | " + operation.model : "");
  });
  appendComparisonRow(body, "Document input size", null, operations, aiInputSize);
  appendComparisonRow(body, "Benchmark tokens", "showBenchmarkTokens", operations, benchmarkTokens);
  appendComparisonRow(body, "Actual input tokens", "showActualAiTokenUsage", operations, operation => operation.error ? "-" : actualTokens(operation, "actualInputTokens"));
  appendComparisonRow(body, "Actual output tokens", "showActualAiTokenUsage", operations, operation => operation.error ? "-" : actualTokens(operation, "actualOutputTokens"));
  appendComparisonRow(body, "AI response time", "showResponseTime", operations, operation => operation.error ? "-" : responseTime(operation));
  appendComparisonRow(body, "Answer", "showAiResponses", operations, (operation, index) => {
    if (!operation.text) return operation.error ? "-" : "Not reported";
    const view = document.createElement("button"); view.type = "button"; view.className = "text-button";
    view.dataset.answerIndex = String(index); view.textContent = "View answer"; return view;
  });
  table.append(body); container.append(table);
  $("#comparison-results").hidden = false; applyDemoFeatures(settings.demoFeatures);
  all("[data-answer-index]").forEach(button => button.addEventListener("click", () => {
    const operation = response.results[Number(button.dataset.answerIndex)];
    $("#ai-answer-title").textContent = operation.label + " answer";
    $("#ai-answer-content").textContent = operation.text || "";
    $("#ai-answer-dialog").showModal();
  }));
}
function renderComparisonProgress() {
  comparisonVisible = true;
  const container = $("#comparison-cards"); container.replaceChildren();
  const progress = document.createElement("p"); progress.className = "comparison-progress";
  progress.textContent = "Comparing JSON, DocLang and Plain Text..."; container.append(progress);
  $("#comparison-results").hidden = false; applyDemoFeatures(settings.demoFeatures);
}
async function synchronizeBeforeAi(documentId) {
  await settingsQueue;
  const [latestSettings, latestResult] = await Promise.all([api("/api/settings"), api("/api/result")]);
  applySettingsSnapshot(latestSettings);
  if (!latestResult || !sameDocument(latestResult.id, documentId))
    throw new DemoApiError("The processed document changed in another browser tab. Reload the page before asking AI.", 409, "stale_document");
  if (!settings.configured) throw new DemoApiError("OpenAI API key is not configured. Open Settings - AI Configuration.");
  if (!aiEnabled()) throw new DemoApiError("Ask with AI was disabled in another browser tab. Review Experience settings.");
}
async function ensureDocumentStillCurrent(documentId) {
  const latestResult = await api("/api/result");
  if (!latestResult || !sameDocument(latestResult.id, documentId))
    throw new DemoApiError("The processed document changed while the AI request was running. The stale answer was not displayed.", 409, "stale_document");
}
$("#ask-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (!result || !settings.configured || !aiEnabled() || asking || comparing) return;
  const documentId = result.id; const representation = selectedRepresentation(); asking = true; singleAiOperation = null; syncAi(); $("#answer").hidden = true; $("#answer").textContent = ""; $("#single-ai-metrics").hidden = true;
  showStatus("#ai-status", "Waiting for OpenAI...");
  try {
    await synchronizeBeforeAi(documentId);
    const answer = await api("/api/ask", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ documentId, question: $("#question").value, representation }) });
    if (result?.id !== documentId) return;
    validateOperation(answer, representation, documentId);
    await ensureDocumentStillCurrent(documentId);
    renderSingleResult(answer);
  } catch (error) { if (result?.id === documentId) showStatus("#ai-status", error.message, true); }
  finally { asking = false; syncAi(); }
});
$("#compare-all").addEventListener("click", async () => {
  if (!result || !settings.configured || !aiEnabled() || !comparisonEnabled() || asking || comparing) return;
  const question = $("#question").value;
  if (!question.trim()) { $("#question").reportValidity(); return; }
  const documentId = result.id; comparing = true; comparisonVisible = false; syncAi(); $("#comparison-results").hidden = true;
  renderComparisonProgress();
  showStatus("#ai-status", "Comparing JSON, DocLang and Plain Text... Settings remain available while the requests run.");
  try {
    await synchronizeBeforeAi(documentId);
    if (!comparisonEnabled()) throw new DemoApiError("Compare All was disabled in another browser tab. Review Experience settings.");
    const response = await api("/api/ai/compare", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ documentId, question }) });
    if (result?.id !== documentId) return;
    const expected = ["json", "doclang", "plaintext"];
    if (!response || !Array.isArray(response.results) || response.results.length !== expected.length)
      throw contractFailure("The comparison response does not match the application contract. Rebuild and restart the application.");
    response.results.forEach((operation, index) => validateOperation(operation, expected[index], documentId, true));
    await ensureDocumentStillCurrent(documentId);
    renderComparison(response);
    const failures = response.results.filter(item => item.error).length;
    showStatus("#ai-status", failures ? "Comparison complete with " + failures + " failed representation(s)." : "JSON complete. DocLang complete. Plain Text complete.");
  } catch (error) {
    comparisonVisible = false; $("#comparison-results").hidden = true;
    if (result?.id === documentId) showStatus("#ai-status", error.message, true);
  }
  finally { comparing = false; syncAi(); }
});

async function initialize() {
  try {
    await verifyApplicationIdentity();
    const [status, savedSettings, previous] = await Promise.all([api("/api/status"), api("/api/settings"), api("/api/result")]);
    uploadLimit = status.maxUploadBytes; $("#file-hint").textContent = "PDF, PNG, JPEG, TIFF, BMP - up to " + Math.round(uploadLimit / 1024 / 1024) + " MB";
    applySettingsSnapshot(savedSettings);
    if (previous) { result = previous; renderResult(); }
    if (!status.fineReaderConfigured) showStatus("#status", "FineReader setup: enter your CustomerProjectId in appsettings.Local.json, then restart.");
    syncAi();
  } catch (error) {
    if (error.code !== "application_version_mismatch") showStatus("#status", error.message, true);
  }
}
initialize();
