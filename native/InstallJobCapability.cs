namespace GameLibrary.Native;

internal sealed record InstallJobTargetSupport(bool Supported, string Target, string Message);

internal static class InstallWorkerCapability
{
    internal const string NativeWindowsTarget = "windows-docker";
    internal const string Wsl2Target = "wsl2";

    internal static InstallJobTargetSupport Check(string? target)
    {
        string normalized = (target ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == NativeWindowsTarget || normalized == "native-windows")
            return new InstallJobTargetSupport(true, NativeWindowsTarget, "The durable native worker can own the Windows Docker CLI process.");
        if (normalized == Wsl2Target)
            return new InstallJobTargetSupport(false, Wsl2Target, "WSL2 continues through the existing script and JobWindow route until the worker can own and reconcile its Linux process group safely.");
        if (normalized == "native-linux")
            return new InstallJobTargetSupport(false, normalized, "Native Linux Bash is an export target; choose the existing script route or Windows Docker for the durable native worker.");
        return new InstallJobTargetSupport(false, normalized, "The selected install target is not supported by the durable native worker.");
    }
}
