﻿using System.Text.Json;
using ActionsImporter.Interfaces;
using ActionsImporter.Models.Docker;

namespace ActionsImporter.Services;

public class DockerService : IDockerService
{
    private readonly IProcessService _processService;

    public DockerService(IProcessService processService)
    {
        _processService = processService;
    }

    public async Task UpdateImageAsync(string image, string server, string version, string? username, string? password, bool passwordStdin = false)
    {
        if (passwordStdin && Console.IsInputRedirected)
        {
            password = await Console.In.ReadToEndAsync();
        }

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            await DockerLoginAsync(server, username, password);
        }
        else
        {
            Console.WriteLine("INFO: using cached credentials because no GHCR credentials were provided.");
        }
        await DockerPullAsync(image, server, version);
    }

    public Task ExecuteCommandAsync(string image, string server, string version, params string[] arguments)
    {
        var actionsImporterArguments = new List<string>
        {
            "run --rm -t"
        };
        actionsImporterArguments.AddRange(GetEnvironmentVariableArguments());

        var dockerArgs = Environment.GetEnvironmentVariable("DOCKER_ARGS");
        if (dockerArgs is not null)
        {
            actionsImporterArguments.Add(dockerArgs);
        }

        actionsImporterArguments.Add($"-v \"{Directory.GetCurrentDirectory()}\":/data");
        actionsImporterArguments.Add($"{server}/{image}:{version}");
        actionsImporterArguments.AddRange(arguments);

        return _processService.RunAsync(
            "docker",
            string.Join(' ', actionsImporterArguments),
            Directory.GetCurrentDirectory(),
            new[] { ("MSYS_NO_PATHCONV", "1") }
        );
    }

    public async Task VerifyDockerRunningAsync()
    {
        try
        {
            await _processService.RunAsync(
                "docker",
                "info",
                output: false
            );
        }
        catch (Exception)
        {
            throw new Exception("Please ensure docker is installed and the docker daemon is running");
        }
    }

    public async Task VerifyImagePresentAsync(string image, string server, string version)
    {
        try
        {
            await _processService.RunAsync(
                "docker",
                $"image inspect {server}/{image}:{version}",
                output: false
            );
        }
        catch (Exception)
        {
            throw new Exception("Unable to locate GitHub Actions Importer image locally. Please run `gh actions-importer update` to fetch the latest image prior to running this command.");
        }
    }

    public async Task<string?> GetLatestImageDigestAsync(string image, string server)
    {
        var (standardOutput, _, _) = await _processService.RunAndCaptureAsync("docker", $"manifest inspect {server}/{image}:latest");
        Manifest? manifest = JsonSerializer.Deserialize<Manifest>(standardOutput);

        return manifest?.GetDigest();
    }

    public async Task<string?> GetCurrentImageDigestAsync(string image, string server)
    {
        var (standardOutput, _, _) = await _processService.RunAndCaptureAsync("docker", $"image inspect --format={{{{.Id}}}} {server}/{image}:latest");

        return standardOutput.Split(":").ElementAtOrDefault(1)?.Trim();
    }

    private static IEnumerable<string> GetEnvironmentVariableArguments()
    {
        if (File.Exists(".env.local"))
        {
            yield return "--env-file .env.local";
        }

        foreach (var env in Constants.EnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(env);

            if (string.IsNullOrWhiteSpace(value)) continue;

            var key = env;
            if (key.StartsWith("GH_", StringComparison.Ordinal))
                key = key.Replace("GH_", "GITHUB_", StringComparison.Ordinal);

            yield return $"--env \"{key}={value}\"";
        }
    }

    private async Task DockerLoginAsync(string server, string username, string password)
    {
        var (standardOutput, standardError, exitCode) = await _processService.RunAndCaptureAsync(
            "docker",
            $"login {server} --username {username} --password-stdin",
            throwOnError: false,
            inputForStdIn: password
        ).ConfigureAwait(false);

        if (exitCode != 0)
        {
            string message = standardError.Trim();
            string? errorMessage = message == $"Error response from daemon: Get \"https://{server}/v2/\": denied: denied"
                ? @"You are not authorized to access GitHub Actions Importer yet. Please ensure you've completed the following:
- Requested access to GitHub Actions Importer and received onboarding instructions via email.
- Accepted all of the repository invites sent after being onboarded."
                : $"There was an error authenticating with the {server} docker repository.\nError: {message}";

            throw new Exception(errorMessage);
        }

        Console.WriteLine(standardOutput);
    }

    private async Task DockerPullAsync(string image, string server, string version)
    {
        Console.WriteLine($"Updating {server}/{image}:{version}...");
        var (_, standardError, exitCode) = await _processService.RunAndCaptureAsync(
            "docker",
            $"pull {server}/{image}:{version} --quiet",
            throwOnError: false
        );

        if (exitCode != 0)
        {
            string message = standardError.Trim();
            string errorMessage = $"There was an error pulling the {server}/{image}:{version}.\nError: {message}";

            if (message == "Error response from daemon: denied"
                || message == $"Error response from daemon: Head \"https://{server}/v2/actions-importer/cli/manifests/latest\": unauthorized")
            {
                errorMessage = @"You are not authorized to access GitHub Actions Importer yet. Please ensure you've completed the following:
- Requested access to GitHub Actions Importer and received onboarding instructions via email.
- Accepted all of the repository invites sent after being onboarded.
- The GitHub personal access token used above contains the 'read:packages' scope.";
            }

            throw new Exception(errorMessage);
        }
        Console.WriteLine($"{server}/{image}:{version} up-to-date");
    }
}
