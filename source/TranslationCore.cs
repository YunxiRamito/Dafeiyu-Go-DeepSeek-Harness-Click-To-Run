using System;
using System.Collections.Generic;
using System.Text;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 译文缓存的稳定标识。
    ///
    /// 任务要求缓存键包含：对象稳定标识 + 文档路径 + 原文 SHA-256 +
    /// 目标语言 + 翻译实现版本。少任何一项都会出问题 ——
    /// 少了 SHA 就分不清"原文改了"和"没改"，少了实现版本就永远用旧提示词的产物。
    /// </summary>
    internal static class TranslationIdentity
    {
        internal const string DefaultLanguage = "zh-CN";

        /// <summary>翻译实现版本。改提示词/分段策略/校验规则时 +1。</summary>
        internal const int EngineVersion = 1;

        internal static string BuildKey(
            string objectId,
            string documentPath,
            string sourceSha256,
            string targetLanguage = null,
            int? engineVersion = null)
        {
            string language = String.IsNullOrWhiteSpace(targetLanguage)
                ? DefaultLanguage
                : targetLanguage.Trim();
            int version = engineVersion.HasValue && engineVersion.Value > 0
                ? engineVersion.Value
                : EngineVersion;
            return Normalize(objectId)
                + "|" + Normalize(documentPath)
                + "|" + Normalize(sourceSha256)
                + "|" + language.ToLowerInvariant()
                + "|v" + version;
        }

        /// <summary>译文是否还能复用：原文没变就直接用，不重新调模型。</summary>
        internal static bool IsReusable(
            string cachedSourceSha256,
            string currentSourceSha256,
            int cachedEngineVersion)
        {
            if (String.IsNullOrWhiteSpace(cachedSourceSha256)
                || String.IsNullOrWhiteSpace(currentSourceSha256))
            {
                // 有一边算不出来就宁可重译，不能拿可能过期的译文糊弄。
                return false;
            }

            if (cachedEngineVersion != EngineVersion)
            {
                return false;
            }

            return String.Equals(
                Normalize(cachedSourceSha256),
                Normalize(currentSourceSha256),
                StringComparison.Ordinal);
        }

        private static string Normalize(string value)
        {
            return (value ?? String.Empty).Trim().Replace('\\', '/').ToLowerInvariant();
        }
    }

    /// <summary>一个待翻译/已翻译的 Markdown 片段。</summary>
    internal sealed class MarkdownChunk
    {
        /// <summary>"text" 会送去翻译；"code" 原样保留，翻译时不许动。</summary>
        public string Kind { get; set; } = "text";

        public string Content { get; set; } = String.Empty;

        public bool IsCode
        {
            get { return String.Equals(Kind, "code", StringComparison.Ordinal); }
        }
    }

    /// <summary>
    /// 按 Markdown 结构分块。
    ///
    /// 核心约束：代码围栏**整体**是一块，绝不从中间切开 ——
    /// 切开之后模型会把半段代码当自然语言翻译，技术内容就坏了。
    /// 文本块按标题和长度边界切，保证请求长度可控、按序重组能还原。
    /// </summary>
    internal static class MarkdownChunker
    {
        internal static List<MarkdownChunk> Split(string markdown, int maxChars)
        {
            List<MarkdownChunk> chunks = new List<MarkdownChunk>();
            if (String.IsNullOrEmpty(markdown))
            {
                return chunks;
            }

            if (maxChars < 200)
            {
                maxChars = 200;
            }

            string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            StringBuilder text = new StringBuilder();
            string fence = null;
            StringBuilder code = new StringBuilder();

            Action flushText = delegate()
            {
                if (text.Length > 0)
                {
                    chunks.Add(new MarkdownChunk
                    {
                        Kind = "text",
                        Content = text.ToString()
                    });
                    text.Length = 0;
                }
            };

            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                string trimmed = line.TrimStart();

                if (fence != null)
                {
                    code.Append(line);
                    if (index < lines.Length - 1)
                    {
                        code.Append('\n');
                    }

                    if (trimmed.StartsWith(fence, StringComparison.Ordinal)
                        && trimmed.TrimEnd().Length >= fence.Length)
                    {
                        // 内容原样保留（包括结尾换行）：重组要能逐字节还原，
                        // 少一个换行就会把后面的段落粘到围栏行上。
                        chunks.Add(new MarkdownChunk
                        {
                            Kind = "code",
                            Content = code.ToString()
                        });
                        code.Length = 0;
                        fence = null;
                    }

                    continue;
                }

                string opener = FenceOpener(trimmed);
                if (opener != null)
                {
                    flushText();
                    fence = opener;
                    code.Append(line);
                    if (index < lines.Length - 1)
                    {
                        code.Append('\n');
                    }

                    continue;
                }

                bool heading = IsHeading(trimmed);
                if (heading && text.Length > 0)
                {
                    flushText();
                }

                if (text.Length > 0 && text.Length + line.Length + 1 > maxChars)
                {
                    flushText();
                }

                text.Append(line);
                if (index < lines.Length - 1)
                {
                    text.Append('\n');
                }
            }

            // 围栏没闭合：这一段仍然当代码块保留，别把没关的 ``` 丢进翻译。
            if (fence != null && code.Length > 0)
            {
                chunks.Add(new MarkdownChunk
                {
                    Kind = "code",
                    Content = code.ToString()
                });
            }

            flushText();
            return chunks;
        }

        /// <summary>重组：翻译只替换 text 块，code 块原样放回，顺序不变。</summary>
        internal static string Reassemble(
            IList<MarkdownChunk> chunks,
            IList<string> translatedTexts)
        {
            StringBuilder text = new StringBuilder();
            int translated = 0;
            for (int index = 0; index < chunks.Count; index++)
            {
                MarkdownChunk chunk = chunks[index];
                string content = chunk.Content;
                if (!chunk.IsCode)
                {
                    if (translatedTexts != null && translated < translatedTexts.Count)
                    {
                        content = translatedTexts[translated];
                    }

                    translated++;
                }

                text.Append(content);
            }

            return text.ToString();
        }

        private static string FenceOpener(string trimmed)
        {
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                return "```";
            }

            if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                return "~~~";
            }

            return null;
        }

        private static bool IsHeading(string trimmed)
        {
            int hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#')
            {
                hashes++;
            }

            return hashes >= 1
                && hashes <= 6
                && hashes < trimmed.Length
                && trimmed[hashes] == ' ';
        }
    }

    /// <summary>
    /// 译文结构校验。返回 null = 通过；否则是中文原因。
    ///
    /// 只做"能机械判定"的部分：代码围栏数量与内容、标题数量、原文里的
    /// URL 和行内代码是否还在。自然语言是否通顺不该由代码判。
    /// </summary>
    internal static class TranslationValidator
    {
        internal static string Validate(string source, string translated)
        {
            if (String.IsNullOrWhiteSpace(translated))
            {
                return "译文为空。";
            }

            if (String.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            List<string> sourceCode = CodeBlocks(source);
            List<string> translatedCode = CodeBlocks(translated);
            if (sourceCode.Count != translatedCode.Count)
            {
                return "代码围栏数量不一致（原文 " + sourceCode.Count
                    + " 段，译文 " + translatedCode.Count + " 段）。";
            }

            for (int index = 0; index < sourceCode.Count; index++)
            {
                if (!String.Equals(
                    sourceCode[index].Trim(),
                    translatedCode[index].Trim(),
                    StringComparison.Ordinal))
                {
                    return "第 " + (index + 1) + " 段代码块被改动或错位。";
                }
            }

            int sourceHeadings = CountHeadings(source);
            int translatedHeadings = CountHeadings(translated);
            if (sourceHeadings != translatedHeadings)
            {
                return "标题数量不一致（原文 " + sourceHeadings
                    + " 个，译文 " + translatedHeadings + " 个）。";
            }

            List<string> tokens = TechnicalTokens(source);
            List<string> missing = new List<string>();
            for (int index = 0; index < tokens.Count; index++)
            {
                if (translated.IndexOf(tokens[index], StringComparison.Ordinal) < 0)
                {
                    missing.Add(tokens[index]);
                }

                if (missing.Count >= 5)
                {
                    break;
                }
            }

            if (missing.Count > 0)
            {
                return "译者丢掉了技术内容：" + String.Join("、", missing);
            }

            return null;
        }

        /// <summary>把 ``` / ~~~ 围栏的正文按顺序取出来（不含围栏行）。</summary>
        internal static List<string> CodeBlocks(string markdown)
        {
            List<string> blocks = new List<string>();
            if (String.IsNullOrEmpty(markdown))
            {
                return blocks;
            }

            string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
            string fence = null;
            StringBuilder buffer = new StringBuilder();
            for (int index = 0; index < lines.Length; index++)
            {
                string trimmed = lines[index].TrimStart();
                if (fence == null)
                {
                    if (trimmed.StartsWith("```", StringComparison.Ordinal))
                    {
                        fence = "```";
                        buffer.Length = 0;
                    }
                    else if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
                    {
                        fence = "~~~";
                        buffer.Length = 0;
                    }

                    continue;
                }

                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    blocks.Add(buffer.ToString().TrimEnd('\n'));
                    buffer.Length = 0;
                    fence = null;
                    continue;
                }

                buffer.Append(lines[index]).Append('\n');
            }

            if (fence != null)
            {
                blocks.Add(buffer.ToString().TrimEnd('\n'));
            }

            return blocks;
        }

        internal static int CountHeadings(string markdown)
        {
            if (String.IsNullOrEmpty(markdown))
            {
                return 0;
            }

            int count = 0;
            bool inFence = false;
            string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string trimmed = lines[index].TrimStart();
                if (trimmed.StartsWith("```", StringComparison.Ordinal)
                    || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }

                if (inFence)
                {
                    continue;
                }

                int hashes = 0;
                while (hashes < trimmed.Length && trimmed[hashes] == '#')
                {
                    hashes++;
                }

                if (hashes >= 1 && hashes <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>原文里必须原样保留的技术 token：URL 与行内代码。</summary>
        internal static List<string> TechnicalTokens(string markdown)
        {
            List<string> tokens = new List<string>();
            if (String.IsNullOrEmpty(markdown))
            {
                return tokens;
            }

            string text = markdown.Replace("\r\n", "\n");

            // 行内代码：成对的 `...`（不跨行、非空、不是围栏行）。
            string[] lines = text.Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    continue;
                }

                int cursor = 0;
                while (cursor < line.Length)
                {
                    int open = line.IndexOf('`', cursor);
                    if (open < 0)
                    {
                        break;
                    }

                    int close = line.IndexOf('`', open + 1);
                    if (close < 0)
                    {
                        break;
                    }

                    string inner = line.Substring(open + 1, close - open - 1).Trim();
                    if (inner.Length > 0 && tokens.IndexOf(inner) < 0)
                    {
                        tokens.Add(inner);
                    }

                    cursor = close + 1;
                }
            }

            // URL
            int position = 0;
            while (position < text.Length)
            {
                int start = text.IndexOf("http", position, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    break;
                }

                int end = start;
                while (end < text.Length && !Char.IsWhiteSpace(text[end])
                    && text[end] != ')' && text[end] != '>' && text[end] != '"')
                {
                    end++;
                }

                string url = text.Substring(start, end - start).TrimEnd('.', ',', ';', ':');
                if (url.Length > 7 && tokens.IndexOf(url) < 0)
                {
                    tokens.Add(url);
                }

                position = Math.Max(end, start + 1);
            }

            return tokens;
        }
    }
}
