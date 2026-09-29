using System;
using System.Diagnostics;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 打开网址。
    ///
    /// 为什么要单独抽出来 + 三级兜底:原来只有一句
    /// <c>Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })</c>,而且
    /// 把所有异常都吞掉 —— 用户看到的现象是"点了没反应",我们这边一点线索都没有。
    ///
    /// 实测过两种情况:
    ///   1. 有的机器(尤其测试虚拟机)https 关联是商店应用那条(AppX...),直接调用
    ///      **不报错也不开浏览器**,返回的进程连 pid 都没有;
    ///   2. 系统里没有能用的默认浏览器时,ShellExecute 同样安静地失败。
    /// 所以这里按"直接调用 → 交给资源管理器 → 报错"三级走,并且**必须把失败原因带出去**,
    /// 由调用方决定怎么告诉用户。
    /// </summary>
    internal static class UrlLauncher
    {
        /// <summary>打开成功返回 true;失败返回 false 并给出原因(不为空)。</summary>
        internal static bool TryOpen(string url, out string error)
        {
            error = null;

            if (String.IsNullOrWhiteSpace(url))
            {
                error = "网址是空的。";
                return false;
            }

            string first = null;

            // 一级:直接让 shell 处理(正常机器上这一步就开了)
            try
            {
                Process.Start(new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception exception)
            {
                first = exception.Message;
            }

            // 二级:交给资源管理器。商店应用关联那条路上,这条路往往还能走通。
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"")
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception exception)
            {
                error = "直接调用失败(" + first + ");交给资源管理器也失败(" + exception.Message + ")";
                return false;
            }
        }
    }
}
