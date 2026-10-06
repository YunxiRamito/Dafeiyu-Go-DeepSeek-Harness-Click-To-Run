using DeepSeekHarnessLauncher;

// 翻译核心：缓存标识 / 复用判定 / Markdown 分块 / 结构校验。全部离线。
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}

string Sha(string seed) => seed.PadRight(64, '0').Substring(0, 64);

// ---------------------------------------------------------------- 缓存标识
string key = TranslationIdentity.BuildKey("skill:owner/repo|skills/a", "SKILL.md", Sha("a"));
Check(
    key == TranslationIdentity.BuildKey("skill:owner/repo|skills/a", "SKILL.md", Sha("a")),
    "identical inputs produce identical cache keys");
Check(
    key != TranslationIdentity.BuildKey("skill:owner/repo|skills/b", "SKILL.md", Sha("a")),
    "different object id produces a different key");
Check(
    key != TranslationIdentity.BuildKey("skill:owner/repo|skills/a", "README.md", Sha("a")),
    "different document path produces a different key");
Check(
    key != TranslationIdentity.BuildKey("skill:owner/repo|skills/a", "SKILL.md", Sha("b")),
    "different source hash produces a different key");
Check(
    key != TranslationIdentity.BuildKey("skill:owner/repo|skills/a", "SKILL.md", Sha("a"), "en-US"),
    "different target language produces a different key");
Check(
    key != TranslationIdentity.BuildKey("skill:owner/repo|skills/a", "SKILL.md", Sha("a"), null, 2),
    "different engine version produces a different key");
Check(key.EndsWith("|zh-cn|v1"), "default language and engine version are part of the key");
Check(
    TranslationIdentity.BuildKey("Skill:Owner/Repo", "docs\\SKILL.md", Sha("A"))
        == TranslationIdentity.BuildKey("skill:owner/repo", "docs/SKILL.md", Sha("a")),
    "key is stable across path separators and casing");

// ---------------------------------------------------------------- 复用判定
Check(
    TranslationIdentity.IsReusable(Sha("a"), Sha("a"), TranslationIdentity.EngineVersion),
    "unchanged source and current engine is reusable");
Check(
    !TranslationIdentity.IsReusable(Sha("a"), Sha("b"), TranslationIdentity.EngineVersion),
    "changed source invalidates the translation");
Check(
    !TranslationIdentity.IsReusable(Sha("a"), Sha("a"), TranslationIdentity.EngineVersion - 1),
    "old engine version invalidates the translation");
Check(!TranslationIdentity.IsReusable(null, Sha("a"), 1), "missing cached hash is not reusable");
Check(!TranslationIdentity.IsReusable(Sha("a"), null, 1), "missing current hash is not reusable");

// ---------------------------------------------------------------- Markdown 分块
string document =
    "# 标题一\n\n这是第一段。\n\n" +
    "```bash\n" +
    "dsh plugin --profile web add pkg\n" +
    "这条注释也是代码，不能被翻译。\n" +
    "```\n\n" +
    "## 标题二\n\n" +
    "```js\nconsole.log(1);\nconsole.log(2);\n```\n\n" +
    "结尾段落，带着 `--flag` 和 https://example.com/docs 两个技术 token。\n";

var chunks = MarkdownChunker.Split(document, 400);
Check(chunks.Count > 1, "long markdown is split into more than one chunk");
int codeChunks = chunks.Count(c => c.IsCode);
Check(codeChunks == 2, "each fenced block becomes exactly one code chunk");
foreach (var chunk in chunks.Where(c => c.IsCode))
{
    Check(chunk.Content.StartsWith("```") && chunk.Content.TrimEnd().EndsWith("```"),
        "code chunk carries both fences");
}
Check(chunks.First(c => c.IsCode).Content.Contains("不能被翻译"),
    "the whole fence body stays inside one chunk");

var textChunks = chunks.Where(c => !c.IsCode).ToList();
Check(textChunks.All(c => c.Content.Length <= 400), "text chunks respect the size limit");
Check(
    string.Join("", chunks.Select(c => c.Content)) == document.Replace("\r\n", "\n"),
    "chunks reassemble to the original document byte for byte");

// 重组：只换文本，代码原样
var translated = new List<string>();
foreach (var chunk in chunks)
{
    translated.Add(chunk.IsCode ? null : "[中文]" + chunk.Content);
}
string rebuilt = MarkdownChunker.Reassemble(chunks, translated.Where(t => t != null).ToList());
Check(rebuilt.Contains("dsh plugin --profile web add pkg"), "code survives reassembly untouched");
Check(rebuilt.Contains("[中文]# 标题一"), "text chunks are replaced by their translations");
Check(
    TranslationValidator.CodeBlocks(rebuilt).SequenceEqual(TranslationValidator.CodeBlocks(document)),
    "reassembly keeps every code block identical");

// 超长单行也不能被切断成两块代码
string longLine = "```\n" + new string('x', 900) + "\n```\n";
var longChunks = MarkdownChunker.Split(longLine, 200);
Check(longChunks.Count == 1 && longChunks[0].IsCode, "an oversized code fence stays one chunk");

// 未闭合围栏也当代码，不能丢
string unclosed = "# 标题\n\n```bash\necho hi\n";
var unclosedChunks = MarkdownChunker.Split(unclosed, 200);
Check(unclosedChunks.Any(c => c.IsCode), "unclosed fence is still treated as code");
Check(
    unclosedChunks.Any(c => !c.IsCode && c.Content.Contains("标题")),
    "text before an unclosed fence is kept");

Check(MarkdownChunker.Split(null, 200).Count == 0, "null markdown yields no chunks");
Check(MarkdownChunker.Split("", 200).Count == 0, "empty markdown yields no chunks");
Check(
    MarkdownChunker.Split("# a\n", 1).Count == 1,
    "absurdly small limits are clamped instead of looping");

// ---------------------------------------------------------------- 结构校验
Check(TranslationValidator.Validate(document, document) == null, "identical text passes validation");

string translatedDoc = document
    .Replace("标题一", "标题壹")
    .Replace("标题二", "标题贰")
    .Replace("这是第一段。", "这是第一段的中文。")
    .Replace("结尾段落，带着", "结尾段落，带着");
Check(TranslationValidator.Validate(document, translatedDoc) == null,
    "translated prose passes while code and tokens are untouched");

string brokenCode = document.Replace("console.log(1);", "console.log(1)  // 改坏了");
Check(TranslationValidator.Validate(document, brokenCode) != null, "modified code block is rejected");

string missingFence = document.Replace("```js\nconsole.log(1);\nconsole.log(2);\n```\n\n", "");
Check(TranslationValidator.Validate(document, missingFence) != null, "dropped code fence is rejected");

string missingHeading = document.Replace("## 标题二", "标题二没有井号");
Check(TranslationValidator.Validate(document, missingHeading) != null, "lost heading is rejected");

string droppedFlag = document.Replace("`--flag`", "这个参数");
Check(TranslationValidator.Validate(document, droppedFlag) != null, "dropped inline code token is rejected");

string droppedUrl = document.Replace("https://example.com/docs", "官网文档");
Check(TranslationValidator.Validate(document, droppedUrl) != null, "dropped URL is rejected");

Check(TranslationValidator.Validate(document, "") != null, "empty translation is rejected");
Check(TranslationValidator.Validate(document, "   ") != null, "whitespace-only translation is rejected");
Check(TranslationValidator.Validate(null, "有内容") == null, "null source has nothing to check");

// 围栏内的 # 不算标题
Check(
    TranslationValidator.CountHeadings("```\n# not a heading\n```\n# real\n") == 1,
    "hash inside a fence is not a heading");
Check(
    TranslationValidator.TechnicalTokens("看 `a`、`b` 和 https://x.dev/y 三个。")
        .SequenceEqual(new[] { "a", "b", "https://x.dev/y" }),
    "technical tokens are extracted in order");
Check(TranslationValidator.CodeBlocks("没有代码").Count == 0, "no fences means no code blocks");

Console.WriteLine($"PASS {checks} translation-core checks; offline, no model calls.");
