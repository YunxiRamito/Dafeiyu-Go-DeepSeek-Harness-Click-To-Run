using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DeepSeekHarnessLauncher
{
    /// <summary>A version and content bound, process-only compatibility guard. Never edits installed plugins.</summary>
    internal static class DshStartupCompatibility
    {
        internal const string PreloadSource = """
            import * as nodeModule from 'node:module';
            import { createHash } from 'node:crypto';
            import { readFileSync } from 'node:fs';
            import { fileURLToPath } from 'node:url';
            import { dirname, join } from 'node:path';

            // Only dsh-ops 0.3.0's published session-pwsh.js is eligible.
            // Cordis dispatches internal/config with a null receiver. Keep the
            // original plugin's filtering, sandbox policy and configuration.
            function applyGuard(url, result) {
                  if (!url.startsWith('file:') || !url.endsWith('/dsh-ops/lib/session-pwsh.js')
                      || result.format !== 'module' || result.source == null) return result;
                  const source = typeof result.source === 'string' ? result.source : Buffer.from(result.source).toString('utf8');
                  if (createHash('sha256').update(source).digest('hex') !== '53e251480ae27ce16bb84099f07ce670633c7bab49c155e5a0024a7991b4f8de') return result;
                  let identity;
                  try { identity = JSON.parse(readFileSync(join(dirname(fileURLToPath(url)), '..', 'package.json'), 'utf8')); }
                  catch { return result; }
                  if (identity.name !== 'dsh-ops' || identity.version !== '0.3.0') return result;
                  const original = 'if (!matches(this.entry) || this.parent?.fiber?.entry === this.entry) return config';
                  const replacement = 'if (!matches(this?.entry) || this?.parent?.fiber?.entry === this?.entry) return config';
                  if (!source.includes(original)) return result;
                  console.error('[Dafeiyu-Go] Applied session-only dsh-ops 0.3.0 null-context compatibility guard. Installed files are unchanged.');
                  return { ...result, source: source.replace(original, replacement) };
            }
            if (typeof nodeModule.registerHooks === 'function') {
              nodeModule.registerHooks({ load(url, context, nextLoad) { return applyGuard(url, nextLoad(url, context)); } });
            } else if (typeof nodeModule.register === 'function') {
              // Node 22.13 ships with the launcher. Its stable asynchronous loader
              // API predates registerHooks; use the same narrowly bound transform.
              const imports = "import { createHash } from 'node:crypto';\nimport { readFileSync } from 'node:fs';\nimport { fileURLToPath } from 'node:url';\nimport { dirname, join } from 'node:path';\n";
              const loader = imports + applyGuard.toString()
                + '\nexport async function load(url, context, nextLoad) { return applyGuard(url, await nextLoad(url, context)); }\n';
              nodeModule.register('data:text/javascript,' + encodeURIComponent(loader), import.meta.url);
            } else {
              console.error('[Dafeiyu-Go] Node.js module registration is unavailable; update Node.js to enable the dsh-ops compatibility guard.');
            }
            """;

        internal static void Configure(ProcessStartInfo startInfo, string dshRoot, Action<string> log = null)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));
            if (startInfo.ArgumentList.Count != 0)
                throw new InvalidOperationException("DSH compatibility preload requires the owning command arguments.");
            try
            {
                byte[] content = Encoding.UTF8.GetBytes(PreloadSource);
                string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                string directory = Path.Combine(Path.GetFullPath(dshRoot), ".launcher-compat");
                Directory.CreateDirectory(directory);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("DSH compatibility directory is a link.");
                string path = Path.Combine(directory, "startup-" + hash + ".mjs");
                if (File.Exists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                        || new FileInfo(path).Length != content.Length
                        || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), SHA256.HashData(content)))
                        throw new IOException("DSH compatibility preload content does not match the launcher.");
                }
                else
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    file.Write(content);
                    file.Flush(true);
                }
                string uri = new Uri(path).AbsoluteUri;
                startInfo.Arguments = "--import \"" + uri + "\" " + startInfo.Arguments;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                || error is ArgumentException || error is NotSupportedException)
            {
                // A read-only installation must still launch normally; a plugin
                // failure will produce its own specific, actionable diagnostic.
                log?.Invoke("启动兼容层未加载，保留原始 DSH 启动方式：" + error.GetType().Name);
            }
        }
    }
}
