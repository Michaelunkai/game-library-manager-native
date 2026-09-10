using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameLibrary.Native;

public static class DockerHubAccess
{
    // Uses the same installed credential helper as Docker Desktop. Secrets remain in
    // process memory and are sent only to Docker Hub's HTTPS login endpoint.
    public static async Task<string> GetToken(CancellationToken cancellation)
    {
        string helper = Path.Combine(Path.GetDirectoryName(DockerScripts.Executable) ?? "", "docker-credential-desktop.exe");
        if (!File.Exists(helper)) throw new HttpRequestException("Sign in to Docker Desktop to refresh catalogs with more than 1,000 tags.");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("get");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        await process.StandardInput.WriteLineAsync("https://index.docker.io/v1/"); process.StandardInput.Close();
        try { await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(15), cancellation); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new HttpRequestException("Docker Desktop has no usable Docker Hub sign-in. Sign in there, then refresh.");
        var credentials = JsonNode.Parse(await stdout)!;
        _ = await stderr; // Never log credential-helper output.
        string username = DataJson.Text(credentials["Username"]), secret = DataJson.Text(credentials["Secret"]);
        if (username.Length == 0 || secret.Length == 0) throw new HttpRequestException("Docker Desktop has no usable Docker Hub credential.");
        using var login = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await login.PostAsync("https://hub.docker.com/v2/users/login/", new StringContent(new JsonObject { ["username"] = username, ["password"] = secret }.ToJsonString(), Encoding.UTF8, "application/json"), cancellation);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Docker Hub API sign-in failed (HTTP " + (int)response.StatusCode + "). Refresh Docker Desktop's sign-in, then retry.");
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))!;
        string token = DataJson.Text(result["token"]);
        if (token.Length == 0) throw new HttpRequestException("Docker Hub requires an updated sign-in before full catalog access.");
        return token;
    }
}
