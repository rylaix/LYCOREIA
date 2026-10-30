using UnityEngine.InputSystem.Utilities;
using SR2MP.Shared.Utils;

namespace SR2MP.Components.UI;

/// <summary>
/// Operations used by the native <see cref="LycoreiaMenu"/>. They reuse the legacy window's join/host
/// logic so both UIs behave identically; the legacy F4 window stays available as a fallback.
/// </summary>
internal sealed partial class MultiplayerUI
{
    internal bool IsFirstTimeSetup => firstTime;
    internal bool IsHostAutoInProgress => hostAutoInProgress;
    internal string HostAutoError => hostAutoError;
    internal string HostJoinCode => hostJoinCode;
    internal string SavedJoinIp => joinIpInput;
    internal string SavedJoinPort => joinPortInput;
    internal string SavedHostPort => hostLocalPortInput;
    internal string NativeLastError { get; private set; } = string.Empty;

    internal void NativeJoinWithCode(string code)
    {
        joinCodeInput = (code ?? string.Empty).WithAllWhitespaceStripped();
        TryJoinWithCode();
        NativeLastError = joinCodeError;
    }

    internal void NativeJoinManual(string ip, string portText)
    {
        if (!NativeValidateJoinManual(ip, portText))
            return;

        TryJoinManual(joinIpInput, ushort.Parse(joinPortInput));
        NativeLastError = joinManualError;
    }

    /// <summary>Checks a join code without connecting (main menu: before a save is loaded).</summary>
    internal bool NativeValidateJoinCode(string code)
    {
        NativeLastError = string.Empty;
        if (JoinCode.TryDecode((code ?? string.Empty).WithAllWhitespaceStripped(), out _, out _, out var error))
            return true;

        NativeLastError = error;
        return false;
    }

    internal bool NativeValidateJoinManual(string ip, string portText)
    {
        joinIpInput = (ip ?? string.Empty).WithAllWhitespaceStripped();
        joinPortInput = (portText ?? string.Empty).WithAllWhitespaceStripped();
        NativeLastError = string.Empty;

        if (joinIpInput.Length == 0)
            NativeLastError = "Enter the host's IP address.";
        else if (joinIpInput == "127.0.0.1" && !DevMode)
            NativeLastError = "127.0.0.1 is not allowed. Use the host's LAN or public IP (PlayIt: the left-side address).";
        else if (!ushort.TryParse(joinPortInput, out var port) || port == 0)
            NativeLastError = "The port must be a number from 1 to 65535.";

        return NativeLastError.Length == 0;
    }

    internal bool NativeValidateHostPort(string portText)
    {
        NativeLastError = ushort.TryParse((portText ?? string.Empty).WithAllWhitespaceStripped(), out var port) && port != 0
            ? string.Empty
            : "The port must be a number from 1 to 65535.";
        return NativeLastError.Length == 0;
    }

    internal void NativeHostAutomatic()
    {
        NativeLastError = string.Empty;
        StartAutoHost();
    }

    internal void NativeHostOnPort(string portText)
    {
        hostLocalPortInput = (portText ?? string.Empty).WithAllWhitespaceStripped();
        NativeLastError = string.Empty;

        if (!ushort.TryParse(hostLocalPortInput, out var port) || port == 0)
        {
            NativeLastError = "The port must be a number from 1 to 65535.";
            return;
        }

        hostJoinCode = string.Empty;
        Host(port);
    }

    internal void NativeSaveSettings(string username, string color, bool allowCheats, bool disableAchievements)
    {
        usernameInput = (username ?? string.Empty).Trim();
        usernameColorInput = (color ?? string.Empty).WithAllWhitespaceStripped().TrimStart('#');
        allowCheatsInput = allowCheats;
        disableAchievementsInput = disableAchievements;
        NativeLastError = string.Empty;

        if (string.IsNullOrWhiteSpace(usernameInput))
        {
            NativeLastError = "Choose a username first.";
            return;
        }

        if (!Extensions.IsValidHexColor(usernameColorInput))
        {
            NativeLastError = "The name color must be 6 hex digits, e.g. FF8800.";
            return;
        }

        if (firstTime)
        {
            firstTime = false;
            Main.SetConfigValue("internal_setup_ui_new_new", false);
        }

        Main.SetConfigValue("username", usernameInput);
        Main.SetConfigValue("username_color", usernameColorInput);
        Main.SetConfigValue("allow_cheats", allowCheatsInput);
        Main.SetConfigValue("disable_achievements", disableAchievementsInput);
    }

    internal void ClearNativeError() => NativeLastError = string.Empty;
}
