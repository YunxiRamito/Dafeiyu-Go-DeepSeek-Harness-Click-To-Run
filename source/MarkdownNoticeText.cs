using System;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessLauncher
{
    internal static class MarkdownNoticeText
    {
        internal static string ToPlainText(string markdown)
        {
            if (String.IsNullOrEmpty(markdown)) return String.Empty;
            string text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
            text = Regex.Replace(text, @"```[\s\S]*?```", "", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"`([^`\n]+)`", "$1", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"!\[([^]]*)\]\([^)]*\)", "$1", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"\[([^]]+)\]\([^)]*\)", "$1", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"(^|\n)\s*[-*+]\s+", "$1• ", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"(^|\n)\s*\d+[.)]\s+", "$1• ", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"[*_~]", "", RegexOptions.CultureInvariant);
            text = Regex.Replace(text, @"<[^>]*>", "", RegexOptions.CultureInvariant);
            return Regex.Replace(text, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant).Trim();
        }
    }
}
