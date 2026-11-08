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
        public void Allow(int port) => Run($"advfirewall firewall add rule name=\"{Rule(port)}\" dir=in action=allow protocol=UDP localport={port}");
        public void Remove(int port) => Run($"advfirewall firewall delete rule name=\"{Rule(port)}\"");
        static void Run(string args)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            using var p = Process.Start(new ProcessStartInfo("netsh", args) { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(5000);
        }
    }
}
