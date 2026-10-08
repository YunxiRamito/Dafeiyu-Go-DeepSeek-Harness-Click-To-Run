using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace DeepSeekHarnessLauncher
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            UnhandledException += App_UnhandledException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs arguments)
        {
            try
            {
                if (Program.Settings != null)
                {
                    CornerRadiusHelper.SetWindowStyle(
                        Program.Settings.WindowStyle);
                }

                CornerRadiusHelper.ApplyApplicationResources(Resources);
            }
            catch (Exception exception)
            {
                WriteAppLog("[theme] corner radius setup failed: " + exception);
            }

            Program.OnApplicationLaunched();
        }

        private static void App_UnhandledException(
            object sender,
            Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            WriteAppLog("[unhandled] " + args.Exception);
        }

        private static void WriteAppLog(string message)
        {
            LauncherLog.Write(Path.Combine(LauncherSettingsStore.DirectoryPath, "launcher-boot.log"),
                "[caller=" + LauncherLog.CallerLocation() + "] " + message);
        }
    }
}
