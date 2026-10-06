using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 启动器自己发起的出网请求统一从这里取代理。
    ///
    /// 三种模式：
    ///   None   —— 明确直连（把请求的 Proxy 置空）；
    ///   System —— 什么都不改，.NET 默认就是跟随 Windows 的 Internet 选项；
    ///   Custom —— 用设置里的协议 + 地址 + 端口。
    ///
    /// 本地回环一律绕开代理，否则探活自己的服务也会被丢进代理。
    ///
    /// 生效范围分成两个开关：<see cref="LauncherSettings.ProxyForLauncher"/>
    /// 管启动器自己（含它拉起的 pnpm / npm / git），默认开；
    /// <see cref="LauncherSettings.ProxyForDsh"/> 管 DSH 服务进程，默认关。
    /// </summary>
    internal static class ProxySupport
    {
        private static readonly string[] LocalBypass =
        {
            "localhost",
            "127.0.0.1",
            "::1"
        };

        internal static string Mode
        {
            get { return EffectiveMode(Program.Settings); }
        }

        /// <summary>
        /// 启动器实际使用的代理模式。把「对启动器生效」关掉时一律按直连处理，
        /// 这样 System / Custom 都碰不到启动器自己的请求。
        /// </summary>
        internal static string EffectiveMode(LauncherSettings settings)
        {
            if (settings == null || !settings.ProxyForLauncher)
            {
                return "None";
            }

            return Normalize(settings.ProxyMode);
        }

        internal static bool IsDirect
        {
            get { return Mode == "None"; }
        }

        /// <summary>拼出代理地址。返回 false 表示当前不该用代理。</summary>
        internal static bool TryBuildProxyUri(out Uri uri)
        {
            return TryBuildProxyUri(Program.Settings, out uri);
        }

        internal static IWebProxy CreateProxy()
        {
            Uri uri;
            return TryBuildProxyUri(Program.Settings, out uri)
                ? CreateProxy(uri)
                : null;
        }

        internal static bool TryBuildProxyUri(
            LauncherSettings settings,
            out Uri uri)
        {
            uri = null;
            if (settings == null || !settings.ProxyForLauncher)
            {
                return false;
            }

            return TryBuildRawProxyUri(settings, out uri);
        }

        /// <summary>
        /// 只看模式拼地址，不看「对启动器生效」开关 ——
        /// DSH 服务进程有自己独立的开关，不能被启动器那个挡住。
        /// </summary>
        private static bool TryBuildRawProxyUri(
            LauncherSettings settings,
            out Uri uri)
        {
            uri = null;
            if (settings == null
                || Normalize(settings.ProxyMode) != "Custom")
            {
                return false;
            }

            string host = (settings.ProxyHost ?? String.Empty).Trim();
            if (host.Length == 0
                || settings.ProxyPort < 1
                || settings.ProxyPort > 65535)
            {
                return false;
            }

            string scheme = "http";
            string protocol = (settings.ProxyProtocol ?? "Http").Trim();
            if (String.Equals(protocol, "Https", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "https";
            }
            else if (String.Equals(protocol, "Socks5", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "socks5";
            }

            string hostPart = host.IndexOf(':') >= 0
                && !host.StartsWith("[", StringComparison.Ordinal)
                    ? "[" + host + "]"
                    : host;
            return Uri.TryCreate(
                scheme + "://" + hostPart + ":" + settings.ProxyPort,
                UriKind.Absolute,
                out uri);
        }

        private static IWebProxy CreateProxy(Uri uri)
        {
            WebProxy proxy = new WebProxy(uri)
            {
                BypassProxyOnLocal = true,
                BypassList = LocalBypass
            };
            return proxy;
        }

        /// <summary>HttpWebRequest：None 要显式置空，System 保持默认。</summary>
        internal static void Apply(HttpWebRequest request)
        {
            if (request == null)
            {
                return;
            }

            switch (Mode)
            {
                case "None":
                    request.Proxy = null;
                    break;
                case "Custom":
                    request.Proxy = CreateProxy();
                    break;
            }
        }

        /// <summary>
        /// 按调用方持有的设置对象应用代理，避免后台服务读取到旧实例。
        /// </summary>
        internal static void Apply(
            HttpWebRequest request,
            LauncherSettings settings)
        {
            if (request == null || settings == null)
            {
                return;
            }

            switch (EffectiveMode(settings))
            {
                case "None":
                    request.Proxy = null;
                    break;
                case "System":
                    request.Proxy = WebRequest.GetSystemWebProxy();
                    break;
                case "Custom":
                    Uri uri;
                    request.Proxy = TryBuildRawProxyUri(settings, out uri)
                        ? CreateProxy(uri)
                        : null;
                    break;
            }
        }

        /// <summary>WebClient：同样三种口径。</summary>
        internal static void Apply(WebClient client)
        {
            if (client == null)
            {
                return;
            }

            switch (Mode)
            {
                case "None":
                    client.Proxy = null;
                    break;
                case "Custom":
                    client.Proxy = CreateProxy();
                    break;
            }
        }

        internal static void Apply(HttpClientHandler handler)
        {
            if (handler == null)
            {
                return;
            }

            switch (Mode)
            {
                case "None":
                    handler.UseProxy = false;
                    break;
                case "System":
                    handler.UseProxy = true;
                    handler.Proxy = null;
                    break;
                case "Custom":
                    handler.UseProxy = true;
                    handler.Proxy = CreateProxy();
                    break;
            }
        }

        /// <summary>
        /// 给启动器拉起的子进程（pnpm / npm / git）准备代理环境变量。
        /// 跟随「对启动器生效」开关。
        /// </summary>
        internal static void ApplyProcessEnvironment(ProcessStartInfo startInfo)
        {
            if (startInfo == null)
            {
                return;
            }

            LauncherSettings settings = Program.Settings;
            ApplyProxyEnvironment(startInfo, settings, EffectiveMode(settings));
        }

        /// <summary>
        /// 给 DSH 服务进程准备代理环境变量。
        ///
        /// 只有显式打开「对 DSH 服务生效」才动它的环境：开关关着时连
        /// 清空都不做，让 DSH 保持原本继承到的那套环境。
        /// </summary>
        internal static void ApplyDshProcessEnvironment(
            ProcessStartInfo startInfo,
            LauncherSettings settings,
            Func<Uri> systemProxyResolver = null)
        {
            if (startInfo == null
                || settings == null
                || !settings.ProxyForDsh)
            {
                return;
            }

            string error = DshConfigurationError(settings);
            if (error != null) throw new InvalidOperationException(error);
            ApplyProxyEnvironment(startInfo, settings, Normalize(settings.ProxyMode), systemProxyResolver);
            // The home .env can restore proxy URLs; inherited wildcard bypass wins.
            if (Normalize(settings.ProxyMode) == "None")
            {
                SetVariable(startInfo, "NO_PROXY", "*");
                SetVariable(startInfo, "no_proxy", "*");
            }
        }

        internal static string DshConfigurationError(LauncherSettings settings)
        {
            if (settings == null || !settings.ProxyForDsh
                || Normalize(settings.ProxyMode) != "Custom") return null;
            Uri uri;
            if (!TryBuildRawProxyUri(settings, out uri))
                return "DSH 自定义代理地址或端口无效，已阻止服务启动。";
            if (uri.Scheme != "http" && uri.Scheme != "https")
                return "DSH 不支持 SOCKS5 代理。请关闭 DSH 代理范围或选择 HTTP/HTTPS；启动器仍支持 SOCKS5。";
            return null;
        }

        /// <summary>
        /// 代理环境变量的唯一落点：先清掉可能继承来的那几个，再按模式写。
        /// </summary>
        private static void ApplyProxyEnvironment(
            ProcessStartInfo startInfo,
            LauncherSettings settings,
            string mode,
            Func<Uri> systemProxyResolver = null)
        {
            string[] names = { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" };
            for (int index = 0; index < names.Length; index++)
            {
                RemoveVariable(startInfo, names[index]);
                RemoveVariable(startInfo, names[index].ToLowerInvariant());
            }

            string noProxy = "localhost,127.0.0.1,::1";
            switch (mode)
            {
                case "None":
                    SetVariable(startInfo, "NO_PROXY", "*");
                    SetVariable(startInfo, "no_proxy", "*");
                    break;
                case "System":
                    Uri system = systemProxyResolver == null
                        ? ResolveSystemProxy()
                        : systemProxyResolver();
                    if (system != null)
                    {
                        SetVariable(startInfo, "HTTP_PROXY", system.AbsoluteUri);
                        SetVariable(startInfo, "HTTPS_PROXY", system.AbsoluteUri);
                        SetVariable(startInfo, "NO_PROXY", noProxy);
                        SetVariable(startInfo, "no_proxy", noProxy);
                    }
                    else
                    {
                        SetVariable(startInfo, "NO_PROXY", "*");
                        SetVariable(startInfo, "no_proxy", "*");
                    }
                    break;
                case "Custom":
                    Uri custom;
                    if (TryBuildRawProxyUri(settings, out custom))
                    {
                        SetVariable(startInfo, "HTTP_PROXY", custom.AbsoluteUri);
                        SetVariable(startInfo, "HTTPS_PROXY", custom.AbsoluteUri);
                        SetVariable(startInfo, "ALL_PROXY", custom.AbsoluteUri);
                        SetVariable(startInfo, "NO_PROXY", noProxy);
                    }
                    break;
            }
        }

        internal static string Describe()
        {
            return Describe(Program.Settings);
        }

        internal static string Describe(LauncherSettings settings)
        {
            if (settings != null && !settings.ProxyForLauncher)
            {
                return "直连（启动器代理已关闭）"
                    + (settings.ProxyForDsh ? " · DSH 服务仍按设置走代理" : String.Empty);
            }

            string mode = settings == null
                ? "None"
                : Normalize(settings.ProxyMode);
            switch (mode)
            {
                case "None":
                    return "直连（不使用代理）";
                case "System":
                    return "使用系统代理";
                default:
                    Uri uri;
                    return TryBuildProxyUri(settings, out uri)
                        ? "自定义代理 " + uri.AbsoluteUri
                        : "自定义代理（配置不完整，已按直连处理）";
            }
        }

        private static Uri ResolveSystemProxy()
        {
            try
            {
                IWebProxy proxy = WebRequest.GetSystemWebProxy();
                if (proxy == null)
                {
                    return null;
                }

                Uri probe = new Uri("https://registry.npmjs.org/");
                Uri resolved = proxy.GetProxy(probe);
                if (resolved == null
                    || resolved == probe
                    || String.Equals(
                        resolved.Host,
                        probe.Host,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (resolved.Scheme != "http" && resolved.Scheme != "https")
                {
                    return null;
                }
                return resolved;
            }
            catch
            {
                return null;
            }
        }

        private static void SetVariable(ProcessStartInfo startInfo, string name, string value)
        {
            try
            {
                startInfo.EnvironmentVariables[name] = value;
            }
            catch
            {
            }
        }

        private static void RemoveVariable(ProcessStartInfo startInfo, string name)
        {
            try
            {
                if (startInfo.EnvironmentVariables.ContainsKey(name))
                {
                    startInfo.EnvironmentVariables.Remove(name);
                }
            }
            catch
            {
            }
        }

        private static string Normalize(string value)
        {
            if (String.Equals(value, "System", StringComparison.OrdinalIgnoreCase))
            {
                return "System";
            }

            if (String.Equals(value, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                return "Custom";
            }

            return "None";
        }
    }
}
