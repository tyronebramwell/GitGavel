using System.Net;
using System.Text;
using FluentAssertions;
using GitGavel.Configuration;
using GitGavel.System2.Abstractions;
using GitGavel.System2.Providers;

namespace GitGavel.Tests;

[Collection("EnvironmentVariables")]
public class LlmProviderAndDotEnvTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public bool WasDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task GeminiLlmClient_GenerateAsync_ExtractsContentAndTokenUsage()
    {
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            var json = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [{ "text": "[{\"title\":\"Bug\"}]" }]
                  }
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 145,
                "candidatesTokenCount": 62
              }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var client = new GeminiLlmClient("gemini-key", new HttpClient(handler));
        var response = await client.GenerateAsync(new LlmRequest
        {
            Prompt = "Audit this code",
            Model = "gemini-2.5-pro",
            Temperature = 0.1,
            ExpectJson = true
        });

        response.Content.Should().Be("[{\"title\":\"Bug\"}]");
        response.PromptTokens.Should().Be(145);
        response.CompletionTokens.Should().Be(62);
        response.TotalTokens.Should().Be(207);
        response.ModelUsed.Should().Be("gemini-2.5-pro");
        capturedBody.Should().Contain("application/json");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GeminiLlmClient_GenerateAsync_WhenHttpError_ThrowsHttpRequestException(HttpStatusCode statusCode)
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"error\":\"failure\"}", Encoding.UTF8, "application/json")
        }));

        using var client = new GeminiLlmClient("gemini-key", new HttpClient(handler));
        var act = () => client.GenerateAsync(new LlmRequest { Prompt = "test" });

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage($"*{(int)statusCode}*");
    }

    [Fact]
    public async Task GeminiLlmClient_GenerateAsync_WhenMalformedJson_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-valid-json", Encoding.UTF8, "application/json")
        }));

        using var client = new GeminiLlmClient("gemini-key", new HttpClient(handler));
        var act = () => client.GenerateAsync(new LlmRequest { Prompt = "test" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*malformed JSON*");
    }

    [Fact]
    public async Task OpenAiLlmClient_GenerateAsync_SendsBearerHeaderAndResolvesModelFallback()
    {
        string? authScheme = null;
        string? authParam = null;
        string? requestBody = null;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            authScheme = req.Headers.Authorization?.Scheme;
            authParam = req.Headers.Authorization?.Parameter;
            requestBody = await req.Content!.ReadAsStringAsync(ct);

            var json = """
            {
              "choices": [
                {
                  "message": {
                    "content": "[{\"title\":\"OpenAI Finding\"}]"
                  }
                }
              ],
              "usage": {
                "prompt_tokens": 200,
                "completion_tokens": 80
              }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var client = new OpenAiLlmClient("sk-test-openai", new HttpClient(handler));
        // Passing a gemini default model should automatically fallback to gpt-4o
        var response = await client.GenerateAsync(new LlmRequest
        {
            Prompt = "Audit OpenAI",
            Model = "gemini-2.5-pro"
        });

        authScheme.Should().Be("Bearer");
        authParam.Should().Be("sk-test-openai");
        requestBody.Should().Contain("\"model\":\"gpt-4o\"");
        response.Content.Should().Be("[{\"title\":\"OpenAI Finding\"}]");
        response.PromptTokens.Should().Be(200);
        response.CompletionTokens.Should().Be(80);
        response.ModelUsed.Should().Be("gpt-4o");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OpenAiLlmClient_GenerateAsync_WhenHttpError_ThrowsHttpRequestException(HttpStatusCode statusCode)
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("rate limit or server error", Encoding.UTF8, "text/plain")
        }));

        using var client = new OpenAiLlmClient("sk-test", new HttpClient(handler));
        var act = () => client.GenerateAsync(new LlmRequest { Prompt = "test", Model = "gpt-4o-mini" });

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage($"*OpenAI API error ({(int)statusCode})*");
    }

    [Fact]
    public async Task OpenAiLlmClient_GenerateAsync_WhenMalformedJson_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<<<malformed>>>", Encoding.UTF8, "application/json")
        }));

        using var client = new OpenAiLlmClient("sk-test", new HttpClient(handler));
        var act = () => client.GenerateAsync(new LlmRequest { Prompt = "test", Model = "gpt-4o" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*OpenAI API returned malformed JSON*");
    }

    [Fact]
    public async Task AnthropicLlmClient_GenerateAsync_SendsHeadersAndExtractsUsage()
    {
        string? apiKeyHeader = null;
        string? versionHeader = null;
        string? requestBody = null;

        var handler = new MockHttpMessageHandler(async (req, ct) =>
        {
            apiKeyHeader = req.Headers.GetValues("x-api-key").FirstOrDefault();
            versionHeader = req.Headers.GetValues("anthropic-version").FirstOrDefault();
            requestBody = await req.Content!.ReadAsStringAsync(ct);

            var json = """
            {
              "content": [
                {
                  "type": "text",
                  "text": "[{\"title\":\"Anthropic Finding\"}]"
                }
              ],
              "usage": {
                "input_tokens": 310,
                "output_tokens": 95
              }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var client = new AnthropicLlmClient("sk-ant-test", new HttpClient(handler));
        var response = await client.GenerateAsync(new LlmRequest
        {
            Prompt = "Audit Anthropic",
            Model = "gemini-2.5-pro"
        });

        apiKeyHeader.Should().Be("sk-ant-test");
        versionHeader.Should().Be("2023-06-01");
        requestBody.Should().Contain("\"model\":\"claude-3-5-sonnet-20241022\"");
        response.Content.Should().Be("[{\"title\":\"Anthropic Finding\"}]");
        response.PromptTokens.Should().Be(310);
        response.CompletionTokens.Should().Be(95);
        response.ModelUsed.Should().Be("claude-3-5-sonnet-20241022");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnthropicLlmClient_GenerateAsync_WhenHttpError_ThrowsHttpRequestException(HttpStatusCode statusCode)
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("anthropic error", Encoding.UTF8, "text/plain")
        }));

        using var client = new AnthropicLlmClient("sk-ant", new HttpClient(handler));
        var act = () => client.GenerateAsync(new LlmRequest { Prompt = "test", Model = "claude-3-5-sonnet-20241022" });

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage($"*Anthropic API error ({(int)statusCode})*");
    }

    [Fact]
    public async Task AnthropicLlmClient_GenerateAsync_WhenMalformedJson_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json")
        }));

        using var client = new AnthropicLlmClient("sk-ant", new HttpClient(handler));
        var act = () => client.GenerateAsync(new LlmRequest { Prompt = "test" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Anthropic API returned malformed JSON*");
    }

    [Fact]
    public async Task LlmClients_WhenCancellationTokenCancelled_ThrowsTaskCanceledException()
    {
        var handler = new MockHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(5000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        using var client = new GeminiLlmClient("key", new HttpClient(handler));

        var act = () => client.GenerateAsync(new LlmRequest { Prompt = "timeout test" }, cts.Token);
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public void LlmClients_Dispose_DoesNotDisposeInjectedHttpClient_ButDisposesOwnedClientSafely()
    {
        var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var sharedHttpClient = new HttpClient(handler);

        var gemini = new GeminiLlmClient("key", sharedHttpClient);
        gemini.Dispose();
        gemini.Dispose(); // Idempotent double-dispose
        handler.WasDisposed.Should().BeFalse("injected HttpClient should not be disposed by client");

        using var ownedOpenAi = new OpenAiLlmClient("key");
        using var ownedAnthropic = new AnthropicLlmClient("key");
    }

    [Fact]
    public void DotEnvLoader_ParsesQuotesExportsCommentsAndPreservesExistingEnvVars()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gitgavel_dotenv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var envFile = Path.Combine(tempDir, ".env");
            File.WriteAllText(envFile, """
            # Comment line
            
            SIMPLE_KEY=simple_value
            DOUBLE_QUOTED="hello world"
            SINGLE_QUOTED='single quoted value'
            export EXPORTED_KEY=exported_val
            CONN_STR=Host=localhost;Port=5432;User=admin
            EXISTING_KEY=new_value_should_be_ignored
            INVALID_LINE_NO_EQUALS
            =EMPTY_KEY_IGNORED
            """);

            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["EXISTING_KEY"] = "already_set"
            };

            var mockEnv = new InMemoryEnvironmentProvider(store);
            DotEnvLoader.Load(tempDir, allowExternalPaths: true, environment: mockEnv);

            store["SIMPLE_KEY"].Should().Be("simple_value");
            store["DOUBLE_QUOTED"].Should().Be("hello world");
            store["SINGLE_QUOTED"].Should().Be("single quoted value");
            store["EXPORTED_KEY"].Should().Be("exported_val");
            store["CONN_STR"].Should().Be("Host=localhost;Port=5432;User=admin");
            store["EXISTING_KEY"].Should().Be("already_set");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DotEnvLoader_WhenDirectoryMissing_DoesNotThrow()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N"));
        var act = () => DotEnvLoader.Load(nonExistent);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task MockLlmClient_SupportsAgentNameAndCustomRuleRegistration()
    {
        var mock = new MockLlmClient();
        mock.RegisterAgentResponse("CustomAgent", "[{\"title\":\"From AgentName\"}]");
        mock.RegisterRule(req => req.Prompt.Contains("SPECIAL_TOKEN"), "[{\"title\":\"From Rule\"}]");

        var ruleResp = await mock.GenerateAsync(new LlmRequest { Prompt = "Check SPECIAL_TOKEN here" });
        ruleResp.Content.Should().Contain("From Rule");

        var agentResp = await mock.GenerateAsync(new LlmRequest { Prompt = "Arbitrary prompt", AgentName = "CustomAgent" });
        agentResp.Content.Should().Contain("From AgentName");
    }

    private sealed class InMemoryEnvironmentProvider(Dictionary<string, string?> store) : IEnvironmentProvider
    {
        public string? GetEnvironmentVariable(string variable) =>
            store.TryGetValue(variable, out var val) ? val : null;

        public void SetEnvironmentVariable(string variable, string? value) =>
            store[variable] = value;
    }
}
