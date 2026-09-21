// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure.AI.AgentServer.Core;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

Env.NoClobber().TraversePath().Load();

var input = new ResilientInputExecutor();
var work = new ResilientWorkExecutor();
var output = new ResilientOutputExecutor();

AIAgent agent = new WorkflowBuilder(input)
    .AddEdge(input, work)
    .AddEdge(work, output)
    .WithOutputFrom(output)
    .Build()
    .AsAIAgent(
        id: "resilient-workflow",
        name: "resilient-workflow",
        includeExceptionDetails: true,
        includeWorkflowOutputsInResponse: true);

var builder = AgentHost.CreateBuilder(args);

// AgentServer must register durable background tasks on the first Responses registration.
builder.Services.AddFoundryResponses(
    agent,
    configure: options => options.ResilientBackground = true);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();

internal sealed class ResilientInputExecutor()
    : ChatProtocolExecutor("resilient-input", new() { AutoSendTurnToken = false })
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<string>();

    protected override ValueTask TakeTurnAsync(
        List<ChatMessage> messages,
        IWorkflowContext context,
        bool? emitEvents,
        CancellationToken cancellationToken = default)
    {
        string request = messages.LastOrDefault()?.Text
            ?? throw new InvalidOperationException("The resilient workflow requires an input message.");
        return context.SendMessageAsync(request, cancellationToken: cancellationToken);
    }
}

internal sealed class ResilientWorkExecutor()
    : Executor<string, string>("resilient-work")
{
    private static readonly string s_processIncarnation = Guid.NewGuid().ToString("N");

    public override async ValueTask<string> HandleAsync(
        string message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        string[] parts = message.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new InvalidOperationException("Expected '<mode>:<token>'.");
        }

        string mode = parts[0];
        string token = parts[1];

        if (string.Equals(mode, "echo", StringComparison.Ordinal))
        {
            return $"ECHO-COMPLETE:{token}";
        }

        if (string.Equals(mode, "long", StringComparison.Ordinal))
        {
            await Task.Delay(
                TimeSpan.FromSeconds(GetLongRunningDelaySeconds()),
                cancellationToken).ConfigureAwait(false);
            return $"LONG-RUN-COMPLETE:{token}";
        }

        if (string.Equals(mode, "crash", StringComparison.Ordinal))
        {
            EnsureCrashDemoEnabled();

            if (TryCreateCrashMarker(token, out string crashedProcessIncarnation))
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(GetCrashDelaySeconds()),
                    cancellationToken).ConfigureAwait(false);
                Console.Out.Flush();
                Console.Error.Flush();
                Environment.Exit(70);
                throw new InvalidOperationException("Process termination did not stop execution.");
            }

            // A persisted marker created by this incarnation would mean the process never restarted.
            if (string.Equals(
                crashedProcessIncarnation,
                s_processIncarnation,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The crash recovery stage resumed in the original process.");
            }

            return $"CRASH-RECOVERED:{token}:PROCESS-CHANGED";
        }

        throw new InvalidOperationException(
            $"Unknown mode '{mode}'. Expected echo, long, or crash.");
    }

    private static void EnsureCrashDemoEnabled()
    {
        string? value = Environment.GetEnvironmentVariable("ENABLE_CRASH_RECOVERY_DEMO");
        if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Crash recovery is disabled. Set ENABLE_CRASH_RECOVERY_DEMO=true to enable it.");
        }
    }

    private static int GetLongRunningDelaySeconds() =>
        ReadNonNegativeInteger("LONG_RUNNING_DELAY_SECONDS", defaultValue: 20);

    private static int GetCrashDelaySeconds() =>
        ReadNonNegativeInteger("CRASH_DELAY_SECONDS", defaultValue: 3);

    private static int ReadNonNegativeInteger(string name, int defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int parsed)
            && parsed >= 0
                ? parsed
                : defaultValue;
    }

    private static bool TryCreateCrashMarker(
        string token,
        out string crashedProcessIncarnation)
    {
        string home = Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException("HOME is not set.");
        string markerDirectory = Path.Combine(
            home,
            ".foundry-hosted-samples",
            "resilient-workflow");
        Directory.CreateDirectory(markerDirectory);

        // Hash untrusted input before using it as a file name.
        string markerName =
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            + ".crashed";
        string markerPath = Path.Combine(markerDirectory, markerName);

        try
        {
            using FileStream marker = new(
                markerPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            byte[] incarnation = Encoding.UTF8.GetBytes(s_processIncarnation);
            marker.Write(incarnation);
            marker.Flush(flushToDisk: true);
            crashedProcessIncarnation = s_processIncarnation;
            return true;
        }
        catch (IOException) when (File.Exists(markerPath))
        {
            crashedProcessIncarnation =
                File.ReadAllText(markerPath, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(crashedProcessIncarnation))
            {
                throw new InvalidOperationException(
                    "The crash marker does not contain a process incarnation.");
            }

            return false;
        }
    }
}

[YieldsOutput(typeof(string))]
internal sealed class ResilientOutputExecutor()
    : Executor<string>("resilient-output")
{
    public override ValueTask HandleAsync(
        string message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default) =>
        context.YieldOutputAsync(message, cancellationToken);
}
