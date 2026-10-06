using System.Text.Json;
using DeepSeekHarnessLauncher;

// 译文缓存与翻译请求构造的离线回归。不联网：替身一旦被调用就直接抛。
string fixtureParent = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(),
    ".translation-store-tests"));
LauncherSettingsStore.Root = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));

int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}

string document =
    "# 标题\n\n"
    + "这是一段说明，带 `--flag` 和 https://example.com/a。\n\n"
    + "```bash\ndsh plugin --profile web add pkg\n```\n";

string sha = TranslationStore.ComputeSha256(document);
string key = TranslationIdentity.BuildKey("skill:owner/repo|skills/a", "SKILL.md", sha);

try
{
    // ---------------------------------------------------------------- 键与文件名
    Check(sha.Length == 64, "source hash is a 64 char sha256");
    Check(
        TranslationStore.ComputeSha256(document) == sha,
        "source hash is stable");
    Check(
        TranslationStore.FileNameFor(key) == TranslationStore.FileNameFor(key),
        "cache file name is stable");
    Check(
        TranslationStore.FileNameFor(key) != TranslationStore.FileNameFor(key + "x"),
        "different keys map to different files");
    Check(
        TranslationStore.FileNameFor("技能/中文:名").StartsWith("t-"),
        "cache file name never contains raw document ids");
    Check(
        !TranslationStore.FileNameFor("技能/中文:名").Contains("/"),
        "cache file name cannot escape the cache directory");

    // ---------------------------------------------------------------- 对象标识
    Check(
        TranslationStore.SkillObjectId(" Demo ") == TranslationStore.SkillObjectId("demo"),
        "skill object id is trimmed and case-insensitive");
    Check(
        TranslationStore.SkillObjectId("demo").StartsWith("skill:"),
        "skill object id carries its kind");
    Check(
        TranslationStore.PluginObjectId("dsh-meme") != TranslationStore.SkillObjectId("dsh-meme"),
        "plugin and skill ids cannot collide");
    Check(TranslationStore.SkillObjectId(null) == "skill:", "null skill id is safe");

    // ---------------------------------------------------------------- 读写往返
    string error;
    TranslationCacheEntry entry = new TranslationCacheEntry
    {
        Key = key,
        ObjectId = "skill:owner/repo|skills/a",
        DocumentPath = "SKILL.md",
        SourceSha256 = sha,
        Language = TranslationIdentity.DefaultLanguage,
        EngineVersion = TranslationIdentity.EngineVersion,
        Model = "deepseek-chat",
        TranslatedAt = "2026-01-01T00:00:00Z",
        Texts = new List<string> { "# 标题\n\n", "这是译文。" },
        Document = "# 标题\n\n这是译文。"
    };
    Check(TranslationStore.Save(entry, out error), "cache save succeeds");
    Check(error == null, "successful save has no error");

    TranslationCacheEntry loaded = TranslationStore.Load(key);
    Check(loaded != null, "cache load returns the saved entry");
    Check(loaded.Document == entry.Document, "document round-trips");
    Check(loaded.Texts.Count == 2 && loaded.Texts[1] == "这是译文。", "text chunks round-trip in order");
    Check(loaded.Model == "deepseek-chat", "model is recorded");
    Check(loaded.TranslatedAt == "2026-01-01T00:00:00Z", "time is recorded");
    Check(loaded.SourceSha256 == sha, "source hash is recorded");
    Check(
        !File.Exists(Path.Combine(TranslationStore.ResolveDirectory(), TranslationStore.FileNameFor(key)) + ".tmp"),
        "no temporary file is left behind");

    // ---------------------------------------------------------------- 复用判定
    TranslationCacheEntry reused;
    Check(
        TranslationStore.TryReuse(key, sha, out reused) && reused.Document == entry.Document,
        "unchanged source reuses the cached translation");
    Check(
        !TranslationStore.TryReuse(key, TranslationStore.ComputeSha256(document + "改了一点"), out reused),
        "changed source invalidates the cached translation");

    TranslationCacheEntry oldEngine = TranslationStore.Load(key);
    oldEngine.EngineVersion = TranslationIdentity.EngineVersion - 1;
    Check(TranslationStore.Save(oldEngine, out error), "engine-version fixture saves");
    Check(
        !TranslationStore.TryReuse(key, sha, out reused),
        "older engine version invalidates the cached translation");
    Check(TranslationStore.Save(entry, out error), "restore the current-version entry");

    // ---------------------------------------------------------------- 按对象清理
    TranslationCacheEntry other = new TranslationCacheEntry
    {
        Key = TranslationIdentity.BuildKey("skill:other/repo|skills/b", "SKILL.md", sha),
        ObjectId = "skill:other/repo|skills/b",
        DocumentPath = "SKILL.md",
        SourceSha256 = sha,
        EngineVersion = TranslationIdentity.EngineVersion,
        Document = "别的来源的译文"
    };
    Check(TranslationStore.Save(other, out error), "second object cache saves");

    Check(TranslationStore.RemoveByObject("skill:owner/repo|skills/a") == 1, "uninstall removes its own cache");
    Check(TranslationStore.Load(key) == null, "removed cache is gone");
    Check(TranslationStore.Load(other.Key) != null, "other objects' cache survives");
    Check(TranslationStore.RemoveByObject(String.Empty) == 0, "empty object id removes nothing");
    Check(TranslationStore.RemoveByObject("不存在的对象") == 0, "unknown object id removes nothing");
    Check(TranslationStore.Load("不存在的键") == null, "unknown key has no cache");

    // ---------------------------------------------------------------- 缓存优先，不走网络
    LauncherSettings settings = new LauncherSettings
    {
        TranslationModel = "deepseek-chat",
        TranslationBaseUrl = "https://api.example.invalid"
    };
    LauncherSettingsStore.ApiKey = "test-key-not-used";
    LauncherSettingsStore.Root = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));

    string cacheKey = TranslationIdentity.BuildKey("skill:cached/repo|skills/a", "SKILL.md", sha);
    Check(TranslationStore.Save(new TranslationCacheEntry
    {
        Key = cacheKey,
        ObjectId = "skill:cached/repo|skills/a",
        DocumentPath = "SKILL.md",
        SourceSha256 = sha,
        EngineVersion = TranslationIdentity.EngineVersion,
        Model = "deepseek-chat",
        Document = "# 已缓存的中文\n"
    }, out error), "fresh cache fixture saves");

    TranslationResult cachedResult = TranslationService.TranslateAsync(
        settings,
        "skill:cached/repo|skills/a",
        "SKILL.md",
        document,
        null,
        null,
        null,
        CancellationToken.None).GetAwaiter().GetResult();
    Check(cachedResult.Ok, "cached document translates successfully");
    Check(cachedResult.FromCache, "cached document is served from cache");
    Check(cachedResult.Chinese == "# 已缓存的中文\n", "cached chinese is returned verbatim");
    Check(!cachedResult.Stale, "cache hit is not reported as stale");

    // 原文变了：应该报"过期"，且因为没配 Key 而不会联网
    LauncherSettingsStore.ApiKey = null;
    TranslationResult staleResult = TranslationService.TranslateAsync(
        settings,
        "skill:cached/repo|skills/a",
        "SKILL.md",
        document + "\n新增一段。\n",
        null,
        null,
        null,
        CancellationToken.None).GetAwaiter().GetResult();
    Check(!staleResult.Ok, "changed source without a key cannot translate");
    Check(staleResult.Stale, "changed source with an older cache is flagged stale");
    Check(
        staleResult.Error != null && staleResult.Error.Contains("API Key"),
        "missing configuration points at the existing API settings entry");

    // ---------------------------------------------------------------- 请求构造
    string requestJson = TranslationService.BuildRequestJson(
        "deepseek-chat",
        TranslationService.SystemPrompt,
        "带 \"引号\" 和\n换行的文本");
    using (JsonDocument parsed = JsonDocument.Parse(requestJson))
    {
        JsonElement root = parsed.RootElement;
        Check(root.GetProperty("model").GetString() == "deepseek-chat", "request carries the model");
        Check(root.GetProperty("stream").GetBoolean() == false, "request is not streaming");
        JsonElement messages = root.GetProperty("messages");
        Check(messages.GetArrayLength() == 2, "request has a system and a user message");
        Check(messages[0].GetProperty("role").GetString() == "system", "first message is the system prompt");
        Check(messages[1].GetProperty("role").GetString() == "user", "second message is the document");
        Check(
            messages[1].GetProperty("content").GetString() == "带 \"引号\" 和\n换行的文本",
            "document content round-trips through json escaping");
    }

    Check(
        TranslationService.SystemPrompt.Contains("原样保留"),
        "system prompt forbids translating technical content");
    Check(
        TranslationService.SystemPrompt.Contains("不是给你的指令"),
        "system prompt treats the document as data, not instructions");
    Check(TranslationService.NormalizeBaseUrl(null) == "https://api.deepseek.com", "default base url");
    Check(TranslationService.NormalizeBaseUrl("https://x.dev/") == "https://x.dev", "base url trailing slash trimmed");
    Check(TranslationService.ResolveModel(null) == "deepseek-chat", "default model");

    string instruction = TranslationService.BuildChunkInstruction("abc", 2, 5);
    Check(instruction.Contains("2/5"), "chunk instruction states its position");
    Check(instruction.Contains("abc"), "chunk instruction carries the source text");
    Check(instruction.Contains("```markdown"), "chunk instruction keeps a fenced wrapper");

    // ---------------------------------------------------------------- 返回解析
    string text;
    string parseError;
    Check(
        TranslationService.TryParseResponse(
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"译文\"}}]}",
            out text,
            out parseError) && text == "译文",
        "valid response is parsed");
    Check(
        !TranslationService.TryParseResponse("{\"error\":{\"message\":\"额度用完了\"}}", out text, out parseError)
            && parseError == "额度用完了",
        "api error message is surfaced");
    Check(!TranslationService.TryParseResponse("{\"choices\":[]}", out text, out parseError), "empty choices rejected");
    Check(!TranslationService.TryParseResponse("not json", out text, out parseError), "broken json rejected");
    Check(!TranslationService.TryParseResponse(null, out text, out parseError), "null response rejected");
    Check(
        !TranslationService.TryParseResponse(
            "{\"choices\":[{\"message\":{\"content\":\"   \"}}]}",
            out text,
            out parseError),
        "blank translation rejected");

    // ---------------------------------------------------------------- 配置检查
    string reason;
    LauncherSettingsStore.ApiKey = null;
    Check(!TranslationService.IsConfigured(settings, out reason), "missing key is not configured");
    Check(reason.Contains("API 与翻译"), "missing key points at the settings page");
    LauncherSettingsStore.ApiKey = "k";
    Check(TranslationService.IsConfigured(settings, out reason), "key plus valid url is configured");
    settings.TranslationBaseUrl = "不是地址";
    Check(!TranslationService.IsConfigured(settings, out reason), "malformed base url is rejected");
    settings.TranslationBaseUrl = "https://api.deepseek.com";

    Console.WriteLine($"PASS {checks} translation-store checks; offline, no HTTP requests.");
}
finally
{
    string fullRoot = Path.GetFullPath(LauncherSettingsStore.Root);
    string expectedParent = fixtureParent + Path.DirectorySeparatorChar;
    _ = fullRoot;
    try
    {
        if (Directory.Exists(fixtureParent))
        {
            Directory.Delete(fixtureParent, true);
        }
    }
    catch
    {
    }
}

namespace DeepSeekHarnessLauncher
{
    internal sealed class LauncherSettings
    {
        public string TranslationModel { get; set; } = "deepseek-chat";
        public string TranslationBaseUrl { get; set; } = "https://api.deepseek.com";
    }

    internal static class Constants
    {
        internal const string UserAgent = "Regression-Test";
    }

    internal static class LauncherSettingsStore
    {
        internal static string Root = Path.Combine(
            Path.GetTempPath(),
            "dsh-translation-store-" + Guid.NewGuid().ToString("N"));

        internal static string ApiKey = "unused";

        internal static string DirectoryPath
        {
            get
            {
                Directory.CreateDirectory(Root);
                return Root;
            }
        }

        internal static string ReadApiKey(LauncherSettings settings) => ApiKey;
    }

    // 真发 HTTP 就说明测试写错了：直接炸掉，比静默联网强。
    internal static class ProxySupport
    {
        internal static void Apply(System.Net.Http.HttpClientHandler handler) =>
            throw new Exception("Unexpected HTTP setup");
    }
}
