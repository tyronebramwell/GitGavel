using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GitGavel.Configuration;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Models;

namespace GitGavel.System2.Providers;

public class OpenAiLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _apiKey;
    private bool _disposed;

    public string ProviderName => "openai";
    public TimeSpan Timeout => _httpClient.Timeout;

    public OpenAiLlmClient(string? apiKey, HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        _apiKey = apiKey ?? "";
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(300);
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var model = LlmModelDefaults.ResolveOpenAiModel(request.Model);

        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var payload = new
        {
            model,
            temperature = request.Temperature,
            messages = new[]
            {
                new { role = "system", content = "You are an autonomous enterprise software auditing agent. Respond ONLY with a valid JSON array." },
                new { role = "user", content = request.Prompt }
            }
        };

        reqMsg.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(reqMsg, ct);
        var rawBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OpenAI API error ({(int)response.StatusCode}): {rawBody}");
        }

        sw.Stop();

        string content = "[]";
        int promptTokens = 0;
        int completionTokens = 0;

        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var contentElem))
                {
                    content = contentElem.GetString() ?? "[]";
                }
            }

            if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptVal))
                    promptTokens = ptVal;
                if (usage.TryGetProperty("completion_tokens", out var ctElem) && ctElem.TryGetInt32(out var ctVal))
                    completionTokens = ctVal;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"OpenAI API returned malformed JSON response: {ex.Message}", ex);
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
