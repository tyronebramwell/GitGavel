namespace GitGavel.Discovery;

public record ScannedFile(
    string RelativePath,
    string AbsolutePath,
    int LineCount,
    long SizeBytes,
    string Content,
    bool IsSpec);

public record SarifFinding(
    string RuleId,
    string Message,
    string Level,
    string FilePath,
    int StartLine,
    int EndLine);

public class RepoContext
{
    public required string RootDirectory { get; init; }
    public required IReadOnlyList<ScannedFile> SpecFiles { get; init; }
    public required IReadOnlyList<ScannedFile> SourceFiles { get; init; }
    public required IReadOnlyList<SarifFinding> SarifFindings { get; init; }
    public IReadOnlyList<FileAstSummary> AstSummaries { get; init; } = [];
    public long TotalLinesOfCode => SourceFiles.Sum(s => (long)s.LineCount);
    public int TotalFilesScanned => SpecFiles.Count + SourceFiles.Count;
}
