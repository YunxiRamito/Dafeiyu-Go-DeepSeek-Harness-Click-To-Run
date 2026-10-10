using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace DeepSeekHarnessLauncher
{
    internal static class PackageDownloadEnvironment
    {
        private const string OfficialRegistry = "https://registry.npmjs.org/";
        private const string MainlandRegistry = "https://registry.npmmirror.com/";

        internal static string ResolvePackageSpecifier(string source, bool backend)
        {
            if (String.IsNullOrWhiteSpace(source)) return source;
            if (!backend) return OriginalPackageSpecifier(source);
            string prefix = String.Empty;
            string value = source.Trim();
            int alias = value.IndexOf("@github:", StringComparison.OrdinalIgnoreCase);
            if (alias < 1) alias = value.IndexOf("@git+https://github.com/", StringComparison.OrdinalIgnoreCase);
            if (alias < 1) alias = value.IndexOf("@https://github.com/", StringComparison.OrdinalIgnoreCase);
            if (alias > 0) { prefix = value.Substring(0, alias + 1); value = value.Substring(alias + 1); }
            string repository = null;
            if (value.StartsWith("github:", StringComparison.OrdinalIgnoreCase)) repository = value.Substring(7);
            else if (value.StartsWith("git+https://github.com/", StringComparison.OrdinalIgnoreCase)) repository = value.Substring(23);
            else if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) repository = value.Substring(19);
            else if (value.IndexOf(':') < 0 && !value.StartsWith("@", StringComparison.Ordinal) && value.Split('#')[0].Split('/').Length == 2)
                repository = value;
            if (repository == null) return source;
            int hash = repository.IndexOf('#');
            string reference = hash < 0 ? String.Empty : repository.Substring(hash);
            string path = (hash < 0 ? repository : repository.Substring(0, hash)).Trim('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);
            if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$")) return source;
            return prefix + "git+" + BackendDownloadSource.BaseUrl + "/api/git/" + path + ".git" + reference;
        }

        internal static string OriginalPackageSpecifier(string source)
        {
            string prefix = "git+" + BackendDownloadSource.BaseUrl + "/api/git/";
            if (source == null) return source;
            int alias = source.IndexOf("@" + prefix, StringComparison.OrdinalIgnoreCase);
            if (alias > 0) return source.Substring(0, alias + 1) + OriginalPackageSpecifier(source.Substring(alias + 1));
            if (!source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return source;
            string path = source.Substring(prefix.Length);
            int hash = path.IndexOf('#');
            string reference = hash < 0 ? String.Empty : path.Substring(hash);
            path = hash < 0 ? path : path.Substring(0, hash);
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);
            return "github:" + path + reference;
        }

        internal static string OriginalBackendUrl(string source)
        {
            if (String.IsNullOrWhiteSpace(source)) return source;
            string value = source.Trim();
            string gitPrefix = "git+" + BackendDownloadSource.BaseUrl + "/api/git/";
            if (value.StartsWith(gitPrefix, StringComparison.OrdinalIgnoreCase))
                return OriginalPackageSpecifier(value);
            string npmPrefix = BackendDownloadSource.BaseUrl + "/api/npm/";
            if (value.StartsWith(npmPrefix, StringComparison.OrdinalIgnoreCase))
                return "https://registry.npmjs.org/" + value.Substring(npmPrefix.Length);
            return value;
        }

        internal static void Apply(ProcessStartInfo process, LauncherSettings settings)
        {
            if (process == null) return;
            ClearBackendState(process);
            if (BackendDownloadSource.IsSelected(settings))
                ApplyBackend(process, BackendDownloadSource.BaseUrl, BackendDownloadSource.CertificateFile());
            else
                process.Environment["npm_config_registry"] =
                    String.Equals(settings?.UpdateSource, "Official", StringComparison.OrdinalIgnoreCase)
                        ? OfficialRegistry
                        : MainlandRegistry;
        }

        internal static void ApplyRegistry(ProcessStartInfo process, string registry)
        {
            if (process == null) return;
            ClearBackendState(process);
            if (String.Equals(registry?.TrimEnd('/'), BackendDownloadSource.BaseUrl + "/api/npm",
                StringComparison.OrdinalIgnoreCase))
            {
                ApplyBackend(process, BackendDownloadSource.BaseUrl, BackendDownloadSource.CertificateFile());
            }
            else if (!String.IsNullOrWhiteSpace(registry))
            {
                process.Environment["npm_config_registry"] = registry.TrimEnd('/') + "/";
            }
        }

        private static void ClearBackendState(ProcessStartInfo process)
        {
            var environment = process.Environment;
            string registry = environment.TryGetValue("npm_config_registry", out string configuredRegistry)
                ? configuredRegistry : String.Empty;
            bool backendConfigured = String.Equals(
                registry.TrimEnd('/'), BackendDownloadSource.BaseUrl + "/api/npm", StringComparison.OrdinalIgnoreCase);

            if (!backendConfigured && environment.TryGetValue("GIT_CONFIG_COUNT", out string countText)
                && Int32.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            {
                for (int index = 0; index < count; index++)
                {
                    string key = environment.TryGetValue("GIT_CONFIG_KEY_" + index, out string value) ? value : String.Empty;
                    string setting = environment.TryGetValue("GIT_CONFIG_VALUE_" + index, out string settingValue) ? settingValue : String.Empty;
                    if (key.IndexOf(BackendDownloadSource.BaseUrl, StringComparison.OrdinalIgnoreCase) >= 0
                        || setting.IndexOf(BackendDownloadSource.BaseUrl, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        backendConfigured = true;
                        break;
                    }
                }
            }

            if (!backendConfigured) return;

            environment.Remove("npm_config_registry");
            environment.Remove("npm_config_cafile");
            environment.Remove("npm_config_strict_ssl");
            environment.Remove("npm_config_always_auth");
            environment.Remove("npm_config_noproxy");
            environment.Remove("NO_PROXY");
            environment.Remove("NODE_EXTRA_CA_CERTS");
            environment.Remove("GIT_TERMINAL_PROMPT");
            environment.Remove("GIT_HTTP_LOW_SPEED_LIMIT");
            environment.Remove("GIT_HTTP_LOW_SPEED_TIME");
            environment.Remove("GIT_SSL_NO_VERIFY");
            environment.Remove("NODE_TLS_REJECT_UNAUTHORIZED");
            foreach (string key in new List<string>(environment.Keys))
            {
                if (key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase))
                    environment.Remove(key);
            }
        }

        internal static void ApplyBackend(ProcessStartInfo process, string baseUrl, string certificateFile)
        {
            string endpoint = baseUrl.TrimEnd('/');
            string[] secrets = { "GITHUB_TOKEN", "GH_TOKEN", "GITHUB_PAT", "GITHUB_AUTH_TOKEN",
                "NODE_AUTH_TOKEN", "NPM_TOKEN", "GIT_ASKPASS", "SSH_ASKPASS", "GIT_CONFIG_PARAMETERS",
                "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy", "npm_config_proxy", "npm_config_https_proxy" };
            var inherited = new List<string>(process.Environment.Keys);
            foreach (string key in inherited)
            {
                bool remove = key.StartsWith("GIT_CONFIG_", StringComparison.OrdinalIgnoreCase)
                    && (key.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
                        || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase)
                        || String.Equals(key, "GIT_CONFIG_COUNT", StringComparison.OrdinalIgnoreCase));
                foreach (string secret in secrets)
                    remove |= String.Equals(key, secret, StringComparison.OrdinalIgnoreCase);
                remove |= key.StartsWith("npm_config_", StringComparison.OrdinalIgnoreCase)
                    && (key.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0
                        || key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
                        || key.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0);
                if (remove) process.Environment.Remove(key);
            }

            process.Environment["npm_config_registry"] = endpoint + "/api/npm/";
            process.Environment["npm_config_proxy"] = "";
            process.Environment["npm_config_https_proxy"] = "";
            process.Environment["npm_config_noproxy"] = "202.189.21.218,api.ramirinko.top";
            process.Environment["NO_PROXY"] = "202.189.21.218,api.ramirinko.top";
            process.Environment["npm_config_cafile"] = certificateFile;
            process.Environment["npm_config_strict_ssl"] = "true";
            process.Environment["npm_config_always_auth"] = "false";
            process.Environment["NODE_EXTRA_CA_CERTS"] = certificateFile;
            process.Environment.Remove("NODE_TLS_REJECT_UNAUTHORIZED");
            process.Environment.Remove("GIT_SSL_NO_VERIFY");
            process.Environment["GIT_TERMINAL_PROMPT"] = "0";

            // These are git -c settings inherited by git children created inside npm/pnpm.
            string[] keys = {
                "http." + endpoint + "/.sslCAInfo",
                "http." + endpoint + "/.sslVerify",
                "url." + endpoint + "/api/git/.insteadOf",
                "http." + endpoint + "/.extraHeader",
                "credential." + endpoint + "/.helper",
                "core.askPass", "http." + endpoint + "/.proxy"
            };
            string[] values = { certificateFile, "true", "https://github.com/", "", "", "", "" };
            process.Environment["GIT_CONFIG_COUNT"] = keys.Length.ToString(CultureInfo.InvariantCulture);
            for (int index = 0; index < keys.Length; index++)
            {
                process.Environment["GIT_CONFIG_KEY_" + index] = keys[index];
                process.Environment["GIT_CONFIG_VALUE_" + index] = values[index];
            }
            // pnpm's fetch timeout does not cover Git's smart-HTTP child. These
            // Git-supported environment settings stop a backend cache fill that
            // has made no progress, so the launcher can report/retry it.
            process.Environment["GIT_HTTP_LOW_SPEED_LIMIT"] = "1";
            process.Environment["GIT_HTTP_LOW_SPEED_TIME"] = "30";
        }
    }
}
