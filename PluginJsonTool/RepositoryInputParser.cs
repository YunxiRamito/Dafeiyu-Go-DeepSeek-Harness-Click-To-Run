using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DshPluginJsonTool
{
    internal static class RepositoryInputParser
    {
        private static readonly Regex PlainRepositoryPattern =
            new Regex(
                @"^\s*(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?\s*$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex GitHubUrlPattern =
            new Regex(
                @"^\s*(?:https?://)?(?:www\.)?github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?(?:[/?#].*)?\s*$",
                RegexOptions.Compiled
                    | RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant);

        private static readonly Regex GitSshPattern =
            new Regex(
                @"^\s*git@github\.com:(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?\s*$",
                RegexOptions.Compiled
                    | RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant);

        internal static List<RepositoryEntry> Parse(
            string text,
            out List<string> errors)
        {
            List<RepositoryEntry> entries = new List<RepositoryEntry>();
            HashSet<string> seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            errors = new List<string>();

            string[] lines = (text ?? String.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');

            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string repositoryPart = line;
                string note = String.Empty;
                int noteSeparator = line.IndexOf('|');
                if (noteSeparator >= 0)
                {
                    repositoryPart = line.Substring(0, noteSeparator).Trim();
                    note = line.Substring(noteSeparator + 1).Trim();
                }

                Match match = GitHubUrlPattern.Match(repositoryPart);
                if (!match.Success)
                {
                    match = GitSshPattern.Match(repositoryPart);
                }

                if (!match.Success)
                {
                    match = PlainRepositoryPattern.Match(repositoryPart);
                }

                if (!match.Success)
                {
                    errors.Add("第 " + (index + 1) + " 行格式无效：" + line);
                    continue;
                }

                string owner = match.Groups["owner"].Value.Trim();
                string repository = match.Groups["repo"].Value
                    .Trim()
                    .TrimEnd('.');
                if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    repository = repository.Substring(0, repository.Length - 4);
                }

                string fullName = owner + "/" + repository;
                if (!seen.Add(fullName))
                {
                    continue;
                }

                entries.Add(new RepositoryEntry
                {
                    Owner = owner,
                    Repository = repository,
                    Note = note
                });
            }

            return entries;
        }
    }
}
