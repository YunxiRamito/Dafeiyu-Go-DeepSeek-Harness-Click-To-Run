using System;
using System.Globalization;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 独立的产品版本比较（SemVer 2.0 子集）。启动器清单、DSH 本体和安装器现在
    /// 各自和自己的本地版本比较，共用同一份实现，避免三套规则漂移。
    /// build metadata（加号后的部分）不参与比较。
    /// </summary>
    internal static class ProductVersion
    {
        /// <summary>远端比本机新就返回 true；任一侧无法解析时返回 false。</summary>
        internal static bool IsNewer(string remoteVersion, string localVersion)
        {
            return Compare(remoteVersion, localVersion) > 0;
        }

        /// <summary>
        /// 版本串能否被解析。调用方用它区分「本机版本真的等于远端」和
        /// 「本机版本根本没读到/读成了垃圾」——后者不能当成已是最新。
        /// </summary>
        internal static bool IsValid(string text)
        {
            SemanticVersion parsed;
            return TryParseSemanticVersion(text, out parsed);
        }
        /// <summary>
        /// 比较 SemVer。核心版本先比,预发布版本按 SemVer 2.0 规则比较,
        /// build metadata(加号后的部分)不参与比较。
        /// </summary>
        internal static int Compare(string leftText, string rightText)
        {
            SemanticVersion left;
            SemanticVersion right;
            if (!TryParseSemanticVersion(leftText, out left)
                || !TryParseSemanticVersion(rightText, out right))
            {
                return 0;
            }

            int core = left.Major.CompareTo(right.Major);
            if (core == 0)
            {
                core = left.Minor.CompareTo(right.Minor);
            }

            if (core == 0)
            {
                core = left.Patch.CompareTo(right.Patch);
            }

            if (core == 0)
            {
                core = left.Revision.CompareTo(right.Revision);
            }

            if (core != 0)
            {
                return core;
            }

            if (left.PreRelease == null && right.PreRelease == null)
            {
                return 0;
            }

            if (left.PreRelease == null)
            {
                return 1;
            }

            if (right.PreRelease == null)
            {
                return -1;
            }

            string[] leftParts = left.PreRelease.Split('.');
            string[] rightParts = right.PreRelease.Split('.');
            int count = Math.Min(leftParts.Length, rightParts.Length);
            for (int index = 0; index < count; index++)
            {
                int comparison = CompareIdentifier(
                    leftParts[index],
                    rightParts[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return leftParts.Length.CompareTo(rightParts.Length);
        }

        private static bool TryParseSemanticVersion(
            string text,
            out SemanticVersion version)
        {
            version = null;
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string value = text.Trim();
            if (value.Length > 0
                && (value[0] == 'v' || value[0] == 'V'))
            {
                value = value.Substring(1);
            }

            int buildMetadata = value.IndexOf('+');
            if (buildMetadata >= 0)
            {
                value = value.Substring(0, buildMetadata);
            }

            string preRelease = null;
            int preReleaseIndex = value.IndexOf('-');
            if (preReleaseIndex >= 0)
            {
                preRelease = value.Substring(preReleaseIndex + 1);
                value = value.Substring(0, preReleaseIndex);
                if (preRelease.Length == 0)
                {
                    return false;
                }
            }

            string[] coreParts = value.Split('.');
            if (coreParts.Length < 2 || coreParts.Length > 4)
            {
                return false;
            }

            int major;
            int minor;
            int patch = 0;
            int revision = 0;
            if (!Int32.TryParse(
                    coreParts[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out major)
                || !Int32.TryParse(
                    coreParts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out minor)
                || (coreParts.Length == 3
                    && !Int32.TryParse(
                        coreParts[2],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out patch))
                || (coreParts.Length == 4
                    && (!Int32.TryParse(
                            coreParts[2],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out patch)
                        || !Int32.TryParse(
                            coreParts[3],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out revision))))
            {
                return false;
            }

            if (major < 0 || minor < 0 || patch < 0 || revision < 0)
            {
                return false;
            }

            version = new SemanticVersion
            {
                Major = major,
                Minor = minor,
                Patch = patch,
                Revision = revision,
                PreRelease = preRelease
            };
            return true;
        }

        private static int CompareIdentifier(string left, string right)
        {
            bool leftNumeric = IsNumericIdentifier(left);
            bool rightNumeric = IsNumericIdentifier(right);
            if (leftNumeric && rightNumeric)
            {
                string leftTrimmed = left.TrimStart('0');
                string rightTrimmed = right.TrimStart('0');
                if (leftTrimmed.Length == 0)
                {
                    leftTrimmed = "0";
                }

                if (rightTrimmed.Length == 0)
                {
                    rightTrimmed = "0";
                }

                int length = leftTrimmed.Length.CompareTo(rightTrimmed.Length);
                return length != 0
                    ? length
                    : String.CompareOrdinal(leftTrimmed, rightTrimmed);
            }

            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }

            return String.CompareOrdinal(left, right);
        }

        private static bool IsNumericIdentifier(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class SemanticVersion
        {
            public int Major { get; set; }
            public int Minor { get; set; }
            public int Patch { get; set; }
            public int Revision { get; set; }
            public string PreRelease { get; set; }
        }
    }
}