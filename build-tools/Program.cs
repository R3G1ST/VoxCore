using System;
using System.Globalization;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Diagnostics;

namespace XamlCompilerWrapper
{
    class Program
    {
        static int Main(string[] args)
        {
            CultureInfo enUS = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentCulture = enUS;
            Thread.CurrentThread.CurrentUICulture = enUS;
            CultureInfo.DefaultThreadCurrentCulture = enUS;
            CultureInfo.DefaultThreadCurrentUICulture = enUS;

            string toolsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @".nuget\packages\microsoft.windowsappsdk.winui\2.3.6\tools\net472");

            string compilerPath = Path.Combine(toolsDir, "XamlCompiler.exe");

            if (!File.Exists(compilerPath))
            {
                Console.Error.WriteLine("XamlCompiler.exe not found at: " + compilerPath);
                return 1;
            }

            string arguments = string.Join(" ", args);

            var psi = new ProcessStartInfo
            {
                FileName = compilerPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            var proc = Process.Start(psi);
            proc.WaitForExit();
            return proc.ExitCode;
        }
    }
}
