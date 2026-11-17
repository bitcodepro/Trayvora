namespace VoiceAgent.Desktop;

internal enum RequestRoute { Auto, LocalGit, AppMcp, Codex }

internal static class RequestRouting
{
    public static RequestRoute Select(string text, RequestRoute requested)
    {
        // An explicit MCP request takes precedence over the selected local route.
        var words = text.Split([' ', '\t', '\r', '\n', ',', '.', '!', '?', ':', ';', '(', ')', '"'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested == RequestRoute.AppMcp || words.Any(word => word.Equals("MCP", StringComparison.OrdinalIgnoreCase)
            || word.Equals("МСР", StringComparison.OrdinalIgnoreCase)))
        {
            return RequestRoute.AppMcp;
        }

        if (requested == RequestRoute.LocalGit)
        {
            return RequestRoute.LocalGit;
        }

        if (requested == RequestRoute.Codex) return RequestRoute.Codex;

        var gitRequest = words.Any(word =>
        {
            var token = word.ToLowerInvariant();
            return token is "git" or "pr" or "head" or "revqora" or "branch" or "branches"
                or "commit" or "commits" or "pull" or "repository" or "repositories" or "repo" or "repos" or "diff"
                || token.StartsWith("репозитор", StringComparison.Ordinal)
                || token.StartsWith("ветк", StringComparison.Ordinal)
                || token.StartsWith("гілк", StringComparison.Ordinal)
                || token.StartsWith("коммит", StringComparison.Ordinal)
                || token.StartsWith("коміт", StringComparison.Ordinal)
                || token.StartsWith("пулл", StringComparison.Ordinal);
        });
        return gitRequest ? RequestRoute.AppMcp : RequestRoute.Codex;
    }
}
