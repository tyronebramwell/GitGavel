using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using GitGavel.Configuration;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Providers;

public class AnthropicLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private bool _disposed;

    public string ProviderName => "anthropic";
    public TimeSpan Timeout => _httpClient.Timeout;

    public AnthropicLlmClient(string? apiKey, HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _apiKey = apiKey ?? "";
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(300);
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var model = LlmModelDefaults.ResolveAnthropicModel(request.Model);

        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        reqMsg.Headers.Add("x-api-key", _apiKey);
        reqMsg.Headers.Add("anthropic-version", "2023-06-01");

        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["temperature"] = request.Temperature,
            ["messages"] = new[]
            {
                new { role = "user", content = request.Prompt }
            }
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            payload["system"] = request.SystemPrompt;
        }

        reqMsg.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(reqMsg, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Anthropic API error ({(int)response.StatusCode}): {rawBody}");
        }

        sw.Stop();

        string content = "[]";
        int promptTokens = 0;
        int completionTokens = 0;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.TryGetProperty("content", out var contentArr) &&
                contentArr.ValueKind == JsonValueKind.Array &&
                contentArr.GetArrayLength() > 0 &&
                contentArr[0].TryGetProperty("text", out var textElem))
            {
                content = textElem.GetString() ?? "[]";
            }

            if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("input_tokens", out var pt) && pt.TryGetInt32(out var ptVal))
                    promptTokens = ptVal;
                if (usage.TryGetProperty("output_tokens", out var ctElem) && ctElem.TryGetInt32(out var ctVal))
                    completionTokens = ctVal;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Anthropic API returned malformed JSON response: {ex.Message}", ex);
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
