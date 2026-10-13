using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security;

internal static class RuntimeBootstrapRegression
{
    private const string MainIdentity = "MicrosoftCorporationII.WinAppRuntime.Main.1.8";
    private const string FrameworkIdentity = "Microsoft.WindowsAppRuntime.1.8";
    private const string Minimum = "8000.946.1701.0";
    private static int checks;
    private static MethodInfo parse;
    private static MethodInfo detect;
    private static PropertyInfo highestVersion;
    private static PropertyInfo isReady;
    private static PropertyInfo hasReadErrors;

    private static int Main(string[] args)
    {
        try
        {
            Assembly bootstrap = Assembly.Load(File.ReadAllBytes(args[0]));
            Console.WriteLine("Bootstrap artifact assembly version: {0}", bootstrap.GetName().Version);
            Type program = bootstrap.GetType("DeepSeekHarnessBootstrap.Program", true);
            Type detector = bootstrap.GetType("DeepSeekHarnessLauncher.WindowsAppRuntimeDetector", true);
            Type result = bootstrap.GetType("DeepSeekHarnessLauncher.WindowsAppRuntimeDetectionResult", true);
            parse = Method(detector, "ParsePackageVersion", typeof(string));
            detect = Method(detector, "Detect", typeof(Func<int, IEnumerable<string>>));
            highestVersion = result.GetProperty("HighestVersion", BindingFlags.Instance | BindingFlags.NonPublic);
            isReady = result.GetProperty("IsReady", BindingFlags.Instance | BindingFlags.NonPublic);
            hasReadErrors = result.GetProperty("HasReadErrors", BindingFlags.Instance | BindingFlags.NonPublic);
            RunPackageCases();
            RunStoreCases();

            // Invoke the compiled outer bootstrap's read-only detector, never its GUI entry point.
            object liveResult = Method(detector, "Detect").Invoke(null, null);
            bool live = (bool)Method(program, "HasWindowsAppRuntime").Invoke(null, null);
            Check(live == (bool)isReady.GetValue(liveResult, null), "bootstrap entry uses the shared production detector");
            Version liveVersion = (Version)highestVersion.GetValue(liveResult, null);
            Console.WriteLine("Native bootstrap detector smoke: highest={0}, ready={1}",
                liveVersion == null ? "(none)" : liveVersion.ToString(), live);
            Console.WriteLine("PASS {0} production bootstrap regression checks", checks);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static MethodInfo Method(Type program, string name, params Type[] parameters)
    {
        MethodInfo method = program.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic,
            null, parameters, null);
        if (method == null) throw new Exception("Missing production detector method: " + name);
        return method;
    }

    private static string Package(string identity, string version, string architecture)
    {
        return identity + "_" + version + "_" + architecture + "__8wekyb3d8bbwe";
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++;
    }

    private static void ParseCase(string package, string expected, string label)
    {
        Version actual = (Version)parse.Invoke(null, new object[] { package });
        Check((actual == null ? null : actual.ToString()) == expected, label);
    }

    private static void RunPackageCases()
    {
        ParseCase(Package(MainIdentity, Minimum, "x64"), Minimum, "Main x64");
        ParseCase(Package(MainIdentity, Minimum, "neutral"), Minimum, "Main neutral");
        ParseCase(Package(FrameworkIdentity, Minimum, "x64"), Minimum, "framework x64");
        ParseCase(Package(MainIdentity.ToLowerInvariant(), Minimum, "x64"), Minimum, "Main identity is case insensitive");
        ParseCase(Package(FrameworkIdentity.ToUpperInvariant(), Minimum, "x64"), Minimum, "framework identity is case insensitive");
        ParseCase(Package(MainIdentity, "8000.921.1539.0", "x64"), "8000.921.1539.0", "old valid package is parsed before threshold check");
        ParseCase(Package(FrameworkIdentity, "8000.1000.1.0", "x64"), "8000.1000.1.0", "future numeric version");

        foreach (string identity in new string[] { MainIdentity, FrameworkIdentity })
        {
            foreach (string architecture in new string[] { "x86", "arm", "arm64", "", "x64extra" })
            {
                ParseCase(Package(identity, Minimum, architecture), null, identity + " rejects " + architecture);
            }
        }
        ParseCase(Package(FrameworkIdentity, Minimum, "neutral"), null, "neutral framework cannot satisfy x64 dependency");

        foreach (string identity in new string[]
        {
            "MicrosoftCorporationII.WinAppRuntime.Main.1.7",
            "MicrosoftCorporationII.WinAppRuntime.Main.1.9",
            "MicrosoftCorporationII.WinAppRuntime.Main.1.80",
            "MicrosoftCorporationII.WinAppRuntime.Singleton",
            "MicrosoftCorporationII.WinAppRuntime.DDLM.1.8",
            "Microsoft.WindowsAppRuntime.1.7",
            "Microsoft.WindowsAppRuntime.1.9",
            "Microsoft.WindowsAppRuntime.1.80",
            "Unrelated.Microsoft.WindowsAppRuntime.1.8"
        })
        {
            ParseCase(Package(identity, Minimum, "x64"), null, "reject unrelated identity " + identity);
        }

        foreach (string version in new string[]
        {
            "", "8000", "8000.946", "8000.946.1701", "8000.946.1701.0.1",
            "8000.946.1701.-1", "8000.-1.1701.0", "8000.946.bad.0", "2147483648.946.1701.0"
        })
        {
            ParseCase(Package(MainIdentity, version, "x64"), null, "reject incomplete or invalid version " + version);
        }
        ParseCase(null, null, "null name");
        ParseCase("", null, "empty name");
        ParseCase(MainIdentity + "_" + Minimum, null, "version alone is not a full package name");
        ParseCase(MainIdentity + "_" + Minimum + "_x64", null, "missing package suffix");
        ParseCase(Package(MainIdentity, Minimum, "x64") + "_extra", null, "extra package field");
    }

    private static void StoreCase(string label, IEnumerable<string> machine, IEnumerable<string> user,
        int inaccessible, bool expectedReady, string expectedVersion, bool enumerationFailure = false)
    {
        int[] calls = new int[2];
        Func<int, IEnumerable<string>> readNames = delegate(int index)
        {
            calls[index]++;
            if (index == 0 && (inaccessible & 1) != 0) throw new UnauthorizedAccessException("HKLM denied");
            if (index == 1 && (inaccessible & 2) != 0) throw new SecurityException("HKCU denied");
            return index == 0 ? machine : user;
        };
        object result = detect.Invoke(null, new object[] { readNames });
        Check((bool)isReady.GetValue(result, null) == expectedReady, label + " readiness");
        Check(calls[0] == 1 && calls[1] == 1, label + " reads both stores independently");
        Version version = (Version)highestVersion.GetValue(result, null);
        Check((version == null ? null : version.ToString()) == expectedVersion, label + " highest valid version");
        Check((bool)hasReadErrors.GetValue(result, null) == (inaccessible != 0 || enumerationFailure),
            label + " preserves read failures for component Unknown state");
    }

    private static IEnumerable<string> FailsDuringEnumeration(string first)
    {
        if (first != null) yield return first;
        throw new UnauthorizedAccessException("Registry enumeration failed");
    }

    private static void RunStoreCases()
    {
        string[] empty = new string[0];
        string main = Package(MainIdentity, Minimum, "x64");
        string framework = Package(FrameworkIdentity, Minimum, "x64");
        string neutral = Package(MainIdentity, Minimum, "neutral");
        string old = Package(MainIdentity, "8000.921.1539.0", "x64");
        string future = Package(FrameworkIdentity, "8000.1000.1.0", "x64");
        string wrongArchitecture = Package(FrameworkIdentity, "9999.9999.9999.9999", "x86");
        StoreCase("both absent", empty, empty, 0, false, null);
        StoreCase("both inaccessible", empty, empty, 3, false, null);
        StoreCase("HKLM Main at minimum", new string[] { main }, empty, 0, true, Minimum);
        StoreCase("HKLM framework at minimum", new string[] { framework }, empty, 0, true, Minimum);
        StoreCase("HKCU-only browser framework", empty, new string[] { framework }, 0, true, Minimum);
        StoreCase("HKCU-only Main", empty, new string[] { main }, 0, true, Minimum);
        StoreCase("HKCU-only neutral Main", empty, new string[] { neutral }, 0, true, Minimum);
        StoreCase("denied HKLM with valid HKCU", empty, new string[] { framework }, 1, true, Minimum);
        StoreCase("denied HKCU with valid HKLM", new string[] { main }, empty, 2, true, Minimum);
        StoreCase("old HKLM with new HKCU", new string[] { old }, new string[] { framework }, 0, true, Minimum);
        StoreCase("old runtime alone", new string[] { old }, empty, 0, false, "8000.921.1539.0");
        StoreCase("one revision below minimum", empty,
            new string[] { Package(FrameworkIdentity, "8000.946.1700.9999", "x64") }, 0, false, "8000.946.1700.9999");
        StoreCase("one revision above minimum", empty,
            new string[] { Package(FrameworkIdentity, "8000.946.1701.1", "x64") }, 0, true, "8000.946.1701.1");
        StoreCase("highest across identities and stores", new string[] { main, wrongArchitecture },
            new string[] { future, old, framework }, 0, true, "8000.1000.1.0");
        StoreCase("highest remains when order reversed", new string[] { future, main, old },
            new string[] { wrongArchitecture, framework }, 0, true, "8000.1000.1.0");
        StoreCase("wrong architectures cannot satisfy", new string[] { wrongArchitecture },
            new string[] { Package(MainIdentity, Minimum, "arm64"), Package(FrameworkIdentity, Minimum, "neutral") },
            0, false, null);
        StoreCase("malformed candidates do not hide valid package",
            new string[] { null, "", MainIdentity + "_9999.9999_x64__8wekyb3d8bbwe", framework }, empty,
            0, true, Minimum);
        StoreCase("failed HKLM enumeration still checks HKCU", FailsDuringEnumeration(null),
            new string[] { framework }, 0, true, Minimum, true);
        StoreCase("partial HKLM result survives failed enumeration", FailsDuringEnumeration(future),
            new string[] { framework }, 0, true, "8000.1000.1.0", true);
        StoreCase("failed HKCU enumeration preserves HKLM", new string[] { main },
            FailsDuringEnumeration(null), 0, true, Minimum, true);
    }
}
