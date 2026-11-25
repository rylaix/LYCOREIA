// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System;
using System.Diagnostics;

namespace Lycoreia
{
    /// <summary>Windows firewall exceptions through netsh. Elsewhere it does nothing.</summary>
    public sealed class NetshFirewall : IFirewall
    {
        static string Rule(int port) => "Lycoreia UDP " + port;
        /// <summary>Throws when netsh refuses (it needs administrator rights, which the game normally does not have).</summary>
        public void Allow(int port)
        {
            int code = Run($"advfirewall firewall add rule name=\"{Rule(port)}\" dir=in action=allow protocol=UDP localport={port}");
            if (code != 0) throw new InvalidOperationException("netsh exit code " + code + (code == 1 ? " (administrator rights needed)" : ""));
        }
        public void Remove(int port) => Run($"advfirewall firewall delete rule name=\"{Rule(port)}\"");
        static int Run(string args)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return 0;
            using var p = Process.Start(new ProcessStartInfo("netsh", args) { CreateNoWindow = true, UseShellExecute = false });
            if (p == null) return -1;
            if (!p.WaitForExit(5000)) return -2;
            return p.ExitCode;
        }
    }
}
