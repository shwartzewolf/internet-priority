using System;
using System.Threading;
using System.Windows.Forms;

namespace InternetPriority
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 0)
            {
                Application.Run(new MainForm());
                return 0;
            }

            if (args.Length < 2 || args[0] != "--apply")
            {
                return 2;
            }

            if (args.Length == 3 && args[1] == "adapter")
            {
                Guid adapterId;
                return Guid.TryParseExact(args[2], "D", out adapterId) && adapterId != Guid.Empty
                    ? Apply(Preference.Connection, adapterId)
                    : 2;
            }

            if (args.Length != 2)
            {
                return 2;
            }

            switch (args[1])
            {
                case "wifi":
                    return Apply(Preference.WiFi);
                case "ethernet":
                    return Apply(Preference.Ethernet);
                case "defaults":
                    return Apply(Preference.WindowsDefaults);
                default:
                    return 2;
            }
        }

        private static int Apply(Preference preference, Guid? adapterId = null)
        {
            using (var applyLock = new Mutex(false, @"Local\InternetPriority.Apply"))
            {
                bool acquired;
                try
                {
                    acquired = applyLock.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    return 8;
                }

                try
                {
                    return NetworkService.RunElevated(preference, adapterId);
                }
                finally
                {
                    applyLock.ReleaseMutex();
                }
            }
        }
    }
}
