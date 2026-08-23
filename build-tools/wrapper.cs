using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

class Program
{
    [DllImport("kernel32.dll")]
    static extern bool SetProcessDefaultLanguage(ushort wLanguage);

    static int Main(string[] args)
    {
        CultureInfo enUS = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = enUS;
        Thread.CurrentThread.CurrentUICulture = enUS;
        CultureInfo.DefaultThreadCurrentCulture = enUS;
        CultureInfo.DefaultThreadCurrentUICulture = enUS;

        SetProcessDefaultLanguage(0x0409);

        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string realCompiler = Path.Combine(dir, "XamlCompiler.original.exe");

        if (!File.Exists(realCompiler))
        {
            Console.Error.WriteLine("Original compiler not found");
            return 1;
        }

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = realCompiler;
        psi.Arguments = string.Join(" ", args);
        psi.UseShellExecute = false;

        Process proc = Process.Start(psi);
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
