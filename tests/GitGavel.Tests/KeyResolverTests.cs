using FluentAssertions;
using GitGavel.Configuration;

namespace GitGavel.Tests;

public class KeyResolverTests
{
    [Fact]
    public void Resolve_WithDirectKey_ReturnsDirectKey()
    {
        var resolved = KeyResolver.Resolve("my-direct-api-key", "IGNORED_ENV_VAR");
        resolved.Should().Be("my-direct-api-key");
    }

    [Fact]
    public void Resolve_WithValidEnvVarSet_ReturnsEnvVarValue()
    {
        var testEnv = "GITGAVEL_UNIT_TEST_VAR_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(testEnv, "secret_value_123");

        try
        {
            var resolved = KeyResolver.Resolve(null, testEnv);
            resolved.Should().Be("secret_value_123");
        }
        finally
        {
            Environment.SetEnvironmentVariable(testEnv, null);
        }
    }

    [Fact]
    public void Resolve_WithFallbackEnvVars_ReturnsFirstMatchingFallback()
    {
        var fallbackKey = "GITGAVEL_FB_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(fallbackKey, "fallback_val");

        try
        {
            var resolved = KeyResolver.Resolve(null, "NON_EXISTENT_VAR", fallbackKey);
            resolved.Should().Be("fallback_val");
        }
        finally
        {
            Environment.SetEnvironmentVariable(fallbackKey, null);
        }
    }

    [Fact]
    public void Resolve_WhenNoKeyOrEnvVarFound_ReturnsNull()
    {
        var resolved = KeyResolver.Resolve(null, "DEFINITELY_NOT_SET_123456789");
        resolved.Should().BeNull();
    }
}
