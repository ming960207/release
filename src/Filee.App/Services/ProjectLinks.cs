namespace Filee.App.Services;

/// <summary>Public project links used by About and feedback, separate from the update repository.</summary>
internal static class ProjectLinks
{
    public const string SourceRepositoryUrl = "https://github.com/KnifeLemon/Filee";

    public static string NewIssueUrl => SourceRepositoryUrl + "/issues/new/choose";
}
