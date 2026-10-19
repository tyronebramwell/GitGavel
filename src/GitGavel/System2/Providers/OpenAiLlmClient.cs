using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitGavel.System2.Abstractions;

namespace GitGavel.System2.Providers;

public class OpenAiLlmClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public OpenAiLlmClient(string? apiKey = null, HttpClient? httpClient = null)
    {
        _apiKey = apiKey
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? "";
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("OpenAI API key is missing. Set OPENAI_API_KEY environment variable.");
        }

        var sw = Stopwatch.StartNew();

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new { role = "system", content = request.SystemPrompt });
        }
        messages.Add(new { role = "user", content = request.Prompt });

        var payload = new Dictionary<string, object>
        {
            ["model"] = string.IsNullOrWhiteSpace(request.Model) || request.Model.StartsWith("gemini") ? "gpt-4o" : request.Model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature
        };

        if (request.ExpectJson)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        var json = JsonSerializer.Serialize(payload);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var httpResponse = await _httpClient.SendAsync(httpRequest, ct);
        sw.Stop();

        var responseBody = await httpResponse.Content.ReadAsStringAsync(ct);
        if (!httpResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OpenAI API call failed with status {(int)httpResponse.StatusCode} ({httpResponse.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var text = "";
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
            {
                text = content.GetString() ?? "";
            }
        }

        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ctCount)) completionTokens = ctCount.GetInt32();
        }

        return new LlmResponse
        {
            Content = text,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            DurationMs = sw.ElapsedMilliseconds,
            ModelUsed = payload["model"].ToString() ?? request.Model
        };
    }
}
