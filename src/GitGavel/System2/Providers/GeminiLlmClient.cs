using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using GitGavel.Configuration;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Providers;

public class GeminiLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private bool _disposed;

    public string ProviderName => "gemini";
    public TimeSpan Timeout => _httpClient.Timeout;

    public GeminiLlmClient(string? apiKey, HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _apiKey = apiKey ?? "";
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(300);
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var model = LlmModelDefaults.ResolveGeminiModel(request.Model);

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        var payload = new Dictionary<string, object>
        {
            ["contents"] = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = request.Prompt } }
                }
            },
            ["generationConfig"] = new
            {
                temperature = request.Temperature,
                responseMimeType = request.ExpectJson ? "application/json" : "text/plain"
            }
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            payload["systemInstruction"] = new
            {
                parts = new[] { new { text = request.SystemPrompt } }
            };
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        httpRequest.Headers.Add("x-goog-api-key", _apiKey);

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gemini API error ({(int)response.StatusCode}): {rawBody}");
        }

        sw.Stop();

        string content = "[]";
        int promptTokens = 0;
        int completionTokens = 0;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0)
            {
                var first = candidates[0];
                if (first.TryGetProperty("content", out var contentElem) &&
                    contentElem.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array &&
                    parts.GetArrayLength() > 0 &&
                    parts[0].TryGetProperty("text", out var textElem))
                {
                    content = textElem.GetString() ?? "[]";
                }
            }

            if (doc.RootElement.TryGetProperty("usageMetadata", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("promptTokenCount", out var pt) && pt.TryGetInt32(out var ptVal))
                    promptTokens = ptVal;
                if (usage.TryGetProperty("candidatesTokenCount", out var ctElem) && ctElem.TryGetInt32(out var ctVal))
                    completionTokens = ctVal;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Gemini API returned malformed JSON response: {ex.Message}", ex);
        }

        return new LlmResponse
        {
            Content = content,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            DurationMs = sw.ElapsedMilliseconds,
            ModelUsed = model
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
