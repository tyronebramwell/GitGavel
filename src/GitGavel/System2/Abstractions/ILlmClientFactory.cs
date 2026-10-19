using GitGavel.Configuration;

namespace GitGavel.System2.Abstractions;

public interface ILlmClientFactory
{
    ILlmClient CreateClient(SubagentSettings settings, GitGavelConfig config);
}
