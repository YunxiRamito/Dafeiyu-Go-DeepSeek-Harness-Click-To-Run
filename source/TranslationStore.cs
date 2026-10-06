using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    /// <summary>一条缓存的译文。原文、模型、时间都记下来，方便判断"过期没"。</summary>
    internal sealed class TranslationCacheEntry
    {
        public string Key { get; set; } = String.Empty;

        /// <summary>对象稳定标识（技能/插件的来源标识）。</summary>
        public string ObjectId { get; set; } = String.Empty;

        public string DocumentPath { get; set; } = String.Empty;

        /// <summary>翻译时原文的 SHA-256。原文一变就对不上，判定为过期。</summary>
        public string SourceSha256 { get; set; } = String.Empty;

        public string Language { get; set; } = TranslationIdentity.DefaultLanguage;

        public int EngineVersion { get; set; } = TranslationIdentity.EngineVersion;

        public string Model { get; set; } = String.Empty;

        public string TranslatedAt { get; set; } = String.Empty;

        /// <summary>按序存放的文本块译文（代码块不进这里）。</summary>
        public List<string> Texts { get; set; } = new List<string>();

        /// <summary>重组后的完整中文，直接给详情页用。</summary>
        public string Document { get; set; } = String.Empty;
    }

    /// <summary>
    /// 译文缓存。
    ///
    /// 放在 <c>%LOCALAPPDATA%\DeepSeekHarness\translations</c>：
    /// - 不在任何技能根目录下（DSH 不会把它当新技能扫出来，也不会多出一份 SKILL.md）；
    /// - 不在插件目录或 profile 里（插件运行时不会加载它）；
    /// - 不在安装目录（安装/更新覆盖不到）。
    ///
    /// 先写 .tmp 再原子替换，且只有校验通过的整篇译文才会被保存 ——
    /// 取消或失败不会留下"半成品缓存"。
    /// </summary>
    internal static class TranslationStore
    {
        internal const string FolderName = "translations";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        internal static string ResolveDirectory()
        {
            return Path.Combine(LauncherSettingsStore.DirectoryPath, FolderName);
        }

        /// <summary>
        /// 技能的对象稳定标识。用安装目录名（连安装根的唯一性够用），
        /// 因为翻译和卸载清理两边都能拿到它，不会一边能对上一边对不上。
        /// </summary>
        internal static string SkillObjectId(string folderOrKey)
        {
            return "skill:" + (folderOrKey ?? String.Empty).Trim().ToLowerInvariant();
        }

        /// <summary>插件的对象稳定标识（用 profile 里的安装键）。</summary>
        internal static string PluginObjectId(string key)
        {
            return "plugin:" + (key ?? String.Empty).Trim().ToLowerInvariant();
        }

        /// <summary>缓存文件名用键的哈希，避免中文/斜杠/长度问题。</summary>
        internal static string FileNameFor(string key)
        {
            return "t-" + Sha256Hex(key ?? String.Empty) + ".json";
        }

        internal static string ComputeSha256(string text)
        {
            return Sha256Hex(text ?? String.Empty);
        }

        private static string Sha256Hex(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                for (int index = 0; index < hash.Length; index++)
                {
                    builder.Append(hash[index].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        /// <summary>读缓存。任何异常都当"没有缓存"，绝不因此弄坏翻译流程。</summary>
        internal static TranslationCacheEntry Load(string key)
        {
            if (String.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            try
            {
                string path = Path.Combine(ResolveDirectory(), FileNameFor(key));
                if (!File.Exists(path))
                {
                    return null;
                }

                TranslationCacheEntry entry = JsonSerializer.Deserialize<TranslationCacheEntry>(
                    File.ReadAllText(path, Encoding.UTF8),
                    JsonOptions);
                if (entry == null || !String.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    return null;
                }

                if (entry.Texts == null)
                {
                    entry.Texts = new List<string>();
                }

                return entry;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写缓存。先 .tmp 再替换；失败返回原因，调用方照常显示本次结果。</summary>
        internal static bool Save(TranslationCacheEntry entry, out string error)
        {
            error = null;
            if (entry == null || String.IsNullOrWhiteSpace(entry.Key))
            {
                error = "缓存条目为空。";
                return false;
            }

            try
            {
                string directory = ResolveDirectory();
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, FileNameFor(entry.Key));
                string temporary = path + ".tmp";
                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(entry, JsonOptions),
                    new UTF8Encoding(false));
                File.Move(temporary, path, true);
                return true;
            }
            catch (Exception exception)
            {
                error = "译文缓存写入失败：" + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// 清掉某个对象（技能/插件）名下的全部缓存。
        /// 只删 ObjectId 匹配的条目 —— 别的来源的缓存一个字都不动。
        /// </summary>
        internal static int RemoveByObject(string objectId)
        {
            if (String.IsNullOrWhiteSpace(objectId))
            {
                return 0;
            }

            int removed = 0;
            try
            {
                string directory = ResolveDirectory();
                if (!Directory.Exists(directory))
                {
                    return 0;
                }

                string[] files = Directory.GetFiles(directory, "t-*.json");
                for (int index = 0; index < files.Length; index++)
                {
                    TranslationCacheEntry entry = null;
                    try
                    {
                        entry = JsonSerializer.Deserialize<TranslationCacheEntry>(
                            File.ReadAllText(files[index], Encoding.UTF8),
                            JsonOptions);
                    }
                    catch
                    {
                        entry = null;
                    }

                    if (entry == null
                        || !String.Equals(entry.ObjectId, objectId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        File.Delete(files[index]);
                        removed++;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return removed;
        }

        /// <summary>缓存里是否有可复用的整篇译文。</summary>
        internal static bool TryReuse(
            string key,
            string currentSourceSha256,
            out TranslationCacheEntry entry)
        {
            entry = Load(key);
            if (entry == null)
            {
                return false;
            }

            if (!TranslationIdentity.IsReusable(
                entry.SourceSha256,
                currentSourceSha256,
                entry.EngineVersion))
            {
                return false;
            }

            return !String.IsNullOrWhiteSpace(entry.Document);
        }
    }
}
