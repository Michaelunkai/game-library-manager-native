using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

public sealed class DockerAuthenticationException : HttpRequestException
{
    public DockerAuthenticationException(string message, System.Net.HttpStatusCode? status = null) : base(message, null, status) { }
}

public static class DockerHubAccess
{
    // Reuse Invoke-DockerHubLogin.ps1's current-user DPAPI credential contract.
    // Desktop's helper can see an empty virtualized store in packaged hosts.
    // No plaintext credential is written, logged, or placed in command arguments.
    public static async Task<string> GetToken(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var credential = ReadSavedCredential(SavedCredentialPaths()) ?? await ReadDesktopCredential(cancellation);
        using var login = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        return await Exchange(login, credential.Username, credential.Secret, cancellation);
    }

    private static IEnumerable<string> SavedCredentialPaths()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, @"Codex\DockerCredentials\hub-token.dpapi");
        yield return @"F:\backup\codex\latest\desktop-runtime\local-codex\DockerCredentials\hub-token.dpapi";
        string[] packages;
        try { packages = Directory.GetDirectories(Path.Combine(local, "Packages"), "OpenAI.Codex_*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { packages = Array.Empty<string>(); }
        foreach (string package in packages)
            yield return Path.Combine(package, @"LocalCache\Local\Codex\DockerCredentials\hub-token.dpapi");
    }

    internal static (string Username, string Secret)? ReadSavedCredential(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 262144) continue;
                byte[] encrypted = Convert.FromHexString(File.ReadAllText(path).Trim());
                byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                try
                {
                    string secret = Encoding.Unicode.GetString(plain);
                    if (secret.Length > 0 && secret.IndexOfAny(new[] { '\0', '\r', '\n' }) < 0)
                        return ("michadockermisha", secret); // Account bound by the existing login script.
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException) { }
        }
        return null;
    }

    private static async Task<(string Username, string Secret)> ReadDesktopCredential(CancellationToken cancellation)
    {
        string helper = Path.Combine(Path.GetDirectoryName(DockerScripts.Executable) ?? "", "docker-credential-desktop.exe");
        if (!File.Exists(helper)) throw new DockerAuthenticationException("Docker credential helper is unavailable.");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("get");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        await process.StandardInput.WriteLineAsync("https://index.docker.io/v1/"); process.StandardInput.Close();
        try { await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(15), cancellation); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new DockerAuthenticationException("Docker Desktop has no usable Docker Hub sign-in. Sign in there, then refresh.");
        var credentials = JsonNode.Parse(await stdout)!;
        _ = await stderr; // Never log credential-helper output.
        string username = DataJson.Text(credentials["Username"]), secret = DataJson.Text(credentials["Secret"]);
        if (username.Length == 0 || secret.Length == 0) throw new DockerAuthenticationException("Docker Desktop has no usable Docker Hub credential.");
        return (username, secret);
    }

    internal static async Task<string> Exchange(HttpClient login, string username, string secret, CancellationToken cancellation)
    {
        using var content = new StringContent(new JsonObject { ["identifier"] = username, ["secret"] = secret }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await login.PostAsync("https://hub.docker.com/v2/auth/token", content, cancellation);
        if (!response.IsSuccessStatusCode) throw new DockerAuthenticationException("Docker Hub API sign-in failed. Refresh Docker Desktop's sign-in, then retry.", response.StatusCode);
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))!;
        string token = DataJson.Text(result["access_token"]);
        if (token.Length == 0) throw new DockerAuthenticationException("Docker Hub requires an updated sign-in before full catalog access.");
        return token;
    }
}
