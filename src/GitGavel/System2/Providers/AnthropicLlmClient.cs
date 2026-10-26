using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Providers;

public class AnthropicLlmClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public TimeSpan Timeout => _httpClient.Timeout;

    public AnthropicLlmClient(string? apiKey = null, HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _apiKey = apiKey
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? "";
        _httpClient = httpClient ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(300) };
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Anthropic API key is missing. Set ANTHROPIC_API_KEY environment variable.");
        }

        var sw = Stopwatch.StartNew();

        var model = string.IsNullOrWhiteSpace(request.Model) || request.Model.StartsWith("gemini")
            ? "claude-3-5-sonnet-20241022"
            : request.Model;

        var userPrompt = request.Prompt;
        if (request.ExpectJson && !userPrompt.Contains("JSON", StringComparison.OrdinalIgnoreCase))
        {
            userPrompt += "\n\nRespond with valid JSON only.";
        }

        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["temperature"] = request.Temperature,
            ["messages"] = new[]
            {
                new { role = "user", content = userPrompt }
            }
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            payload["system"] = request.SystemPrompt;
        }

        var json = JsonSerializer.Serialize(payload);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("x-api-key", _apiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");

        var httpResponse = await _httpClient.SendAsync(httpRequest, ct);
        sw.Stop();

        var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);
        if (!httpResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Anthropic API call failed with status {(int)httpResponse.StatusCode} ({httpResponse.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var text = "";
        if (root.TryGetProperty("content", out var contentElem) && contentElem.GetArrayLength() > 0)
        {
            foreach (var block in contentElem.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    block.TryGetProperty("text", out var txt))
                {
                    text += txt.GetString();
                }
            }
        }

        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it)) promptTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot)) completionTokens = ot.GetInt32();
        }

        return new LlmResponse
        {
            Content = text,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            DurationMs = sw.ElapsedMilliseconds,
            ModelUsed = model
        };
    }
}
