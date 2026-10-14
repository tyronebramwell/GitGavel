using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Providers;

public class GeminiLlmClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GeminiLlmClient(string? apiKey = null, HttpClient? httpClient = null)
    {
        _apiKey = apiKey
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("AI_API_KEY")
            ?? "";
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Gemini API key is missing. Set GEMINI_API_KEY or AI_API_KEY environment variable.");
        }

        var sw = Stopwatch.StartNew();

        // Build Gemini REST payload
        var payload = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            payload["systemInstruction"] = new
            {
                parts = new[] { new { text = request.SystemPrompt } }
            };
        }

        payload["contents"] = new[]
        {
            new
            {
                parts = new[] { new { text = request.Prompt } }
            }
        };

        var genConfig = new Dictionary<string, object>
        {
            ["temperature"] = request.Temperature
        };

        if (request.ExpectJson)
        {
            genConfig["responseMimeType"] = "application/json";
        }

        payload["generationConfig"] = genConfig;

        var json = JsonSerializer.Serialize(payload);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{request.Model}:generateContent?key={_apiKey}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var httpResponse = await _httpClient.SendAsync(httpRequest, ct);
        sw.Stop();

        var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);

        if (!httpResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Gemini API call failed with status code {(int)httpResponse.StatusCode} ({httpResponse.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var text = "";
        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0)
        {
            var firstCandidate = candidates[0];
            if (firstCandidate.TryGetProperty("content", out var contentObj) &&
                contentObj.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array &&
                parts.GetArrayLength() > 0)
            {
                text = parts[0].GetProperty("text").GetString() ?? "";
            }
        }

        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            if (usage.TryGetProperty("promptTokenCount", out var pt)) promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("candidatesTokenCount", out var ctCount)) completionTokens = ctCount.GetInt32();
        }

        return new LlmResponse
        {
            Content = text,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            DurationMs = sw.ElapsedMilliseconds,
            ModelUsed = request.Model
        };
    }
}
