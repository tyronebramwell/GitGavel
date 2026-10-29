using GitGavel.System2.Models;

namespace GitGavel.System2.Abstractions;

public interface IFindingParser
{
    List<FindingCandidate> ParseFindings(string rawResponse, string agentName, string defaultCategory, Action<string>? onWarning = null);
    List<IssueResolutionCandidate> ParseIssueResolutions(string rawResponse, Action<string>? onWarning = null);
}
