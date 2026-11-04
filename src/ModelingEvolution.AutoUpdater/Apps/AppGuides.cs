using System.Collections.Immutable;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>One install step. <see cref="Unverified"/>: nobody has confirmed it, in source or on a device.</summary>
public sealed record InstallStep(string Text, bool Unverified = false);

/// <summary>What a person needs to install one player. Written from native-player's source; no internal names or addresses.</summary>
public sealed record AppGuide(
    string Platform,
    string DisplayName,
    string Requirements,
    bool Preview,
    string? Warning,
    string? Note,
    string VerifyHashCommand,
    ImmutableArray<InstallStep> Steps);

public static class AppGuides
{
    public const string Windows = "windows";
    public const string Linux = "linux";
    public const string Android = "android";

    public static readonly ImmutableArray<AppGuide> All =
    [
        new AppGuide(
            Windows,
            "Windows",
            "Windows 10 22H2 or Windows 11, x64. Needs the Microsoft Edge WebView2 Runtime, which those normally include; the installer does not.",
            Preview: false,
            Warning: null,
            Note: "The installer is not signed, so Windows SmartScreen warns about it. This is expected for this build.",
            VerifyHashCommand: "certutil -hashfile <file> SHA256",
            [
                new InstallStep("Download the installer and run it. It installs for the current user and needs no administrator rights."),
                new InstallStep("When SmartScreen says \"Windows protected your PC\", choose \"More info\", then \"Run anyway\".", Unverified: true),
                new InstallStep("Finish the installer. It does not start the client."),
                new InstallStep("Start \"RocketWelder Client\" from the Start menu. If it reports that the WebView2 Runtime is not installed, install that from Microsoft and start the client again."),
                new InstallStep("On the start screen, enter the Neuron address with its port, http://<IP>:<port>, and choose Open."),
                new InstallStep("To upgrade, run a newer installer over the old one. Settings survive an upgrade and an uninstall."),
            ]),
        new AppGuide(
            Linux,
            "Linux kiosk",
            "arm64 only, built for Debian 13. There is no x64 package.",
            Preview: false,
            Warning: "Dedicated kiosk devices only. Do not install this on a working PC. It takes over the machine: it creates a kiosk user, logs in automatically on tty1 and disables the display manager. The machine reboots by itself after every install and upgrade.",
            Note: null,
            VerifyHashCommand: "sha256sum <file>",
            [
                new InstallStep("Copy the package to the kiosk device."),
                new InstallStep("Run sudo apt install ./<file>. A reboot follows within seconds of the install finishing. To hold it, run sudo systemctl stop native-player-reboot straight away."),
                new InstallStep("Set the page to show: sudo kiosk-config --url <URL>, using http://<IP>:<port> of the Neuron. If the machine rebooted first, do this over SSH, because the screen now belongs to the kiosk.", Unverified: true),
                new InstallStep("Run sudo reboot if you held the reboot, so the kiosk starts on the new page."),
            ]),
        new AppGuide(
            Android,
            "Android tablet",
            "Android 12 or newer, arm64-v8a.",
            Preview: true,
            Warning: "If the tablet already has a build signed with the development key, uninstall that first. Android cannot update across signing keys, and uninstalling erases the app's settings.",
            Note: null,
            VerifyHashCommand: "certutil -hashfile <file> SHA256  (or sha256sum <file>, on a PC)",
            [
                new InstallStep("Open this page on the tablet and download the APK.", Unverified: true),
                new InstallStep("Open the downloaded file. When Android asks, allow installs from this source, then choose Install.", Unverified: true),
                new InstallStep("Start RocketWelder Kiosk. With no Neuron set, the settings sheet opens by itself; later, swipe in from the right edge to open it."),
                new InstallStep("In \"Neuron base URL\", enter http://<IP>:<port>. The port is required, and https, host names and paths are refused."),
                new InstallStep("Choose \"Save & restart\"."),
            ]),
    ];

    public static bool IsKnownPlatform(string platform) => All.Any(g => g.Platform == platform);
}
