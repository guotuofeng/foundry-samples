# What this sample demonstrates

This sample hosts a model-backed Agent Framework workflow with resilient background Responses
enabled. The workflow contains an Agent Executor with a declarative `simulate_crash` tool. When the
agent calls that tool, a second workflow executor intentionally terminates the process. AgentServer
starts a replacement process and Agent Framework resumes the same pending tool call from the durable
workflow checkpoint.

> [!WARNING]
> This sample deliberately terminates the agent process. Deploy it only to a development or test
> project.

## How it works

The workflow contains two executors:

```mermaid
flowchart LR
    Agent[Crash Recovery Agent]
    Tool[Crash Tool Executor]

    Agent -->|FunctionCallContent: simulate_crash| Tool
    Tool -->|FunctionResultContent| Agent
```

The agent receives an `AIFunctionDeclaration`. A declaration describes a tool to the model but has
no local implementation:

```csharp
AIFunctionDeclaration simulateCrash = AIFunctionFactory.CreateDeclaration(
    name: "simulate_crash",
    description: "Terminate the current agent process to demonstrate durable workflow recovery.",
    jsonSchema: JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
        additionalProperties = false,
    }));
```

The Agent Executor intercepts the unterminated function call and sends it through the workflow:

```csharp
ExecutorBinding agentExecutor = crashAgent.BindAsExecutor(new AIAgentHostOptions
{
    InterceptUnterminatedFunctionCalls = true,
});
```

The agent uses a fixed `Id` and `Name`. A replacement process must reconstruct the same executor
identity for the persisted workflow checkpoint to remain compatible.

At the end of that workflow superstep, the Agent Executor checkpoints its agent session and the
pending `FunctionCallContent`. The Crash Tool Executor receives the call in the next superstep.

On the first execution, it waits five seconds so hosting can persist the completed Agent Executor
superstep, then calls `Environment.Exit(70)`. In the replacement process,
`OnCheckpointRestoredAsync` runs before the pending function call is delivered again. The executor
then returns a `FunctionResultContent` with the same call ID, and the Agent Executor continues the
original turn.

No marker file or external application state is used. Run each crash demonstration in a new
session and conversation so a checkpoint restoration unambiguously belongs to that interrupted
turn.

Resilience is enabled when the Responses server is first registered:

```csharp
builder.Services.AddFoundryResponses(
    agent,
    configure: options => options.ResilientBackground = true);
```

Recovery applies only to stored background requests. Use `background=true` with `store=true`.

## Prerequisites

1. An existing Foundry project with a deployed model, or create them during Option 1.
2. [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later.
3. The deployed agent identity needs the **Foundry User** role on the Foundry project so it can write
   durable workflow checkpoints.

## Option 1: Azure Developer CLI (`azd`)

### Prerequisites

1. [Azure Developer CLI (`azd`)](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd) 1.27.1 or later.
2. Install the Foundry extension:

   ```bash
   azd ext install microsoft.foundry
   ```

3. Authenticate:

   ```bash
   azd auth login
   ```

### Initialize the agent project

```bash
mkdir resilient-workflow-agent && cd resilient-workflow-agent
azd ai agent init \
  -m https://github.com/microsoft-foundry/foundry-samples/blob/main/samples/csharp/hosted-agents/agent-framework/resilient-workflow/azure.yaml \
  --deploy-mode container
cd resilient-workflow
```

The explicit deployment mode ensures `azd` builds the included `Dockerfile` instead of using its
default ZIP-based code deployment.

### Provision and run locally

```bash
azd provision
azd ai agent run
```

In another terminal, verify the non-destructive path:

```bash
azd ai agent invoke --local \
  "Reply briefly and include [LOCAL-READY]. Do not call any tools."
```

### Deploy

```bash
azd deploy
```

After the first deployment, assign the hosted agent identity the **Foundry User** role on the target
Foundry project:

```powershell
$agent = azd ai agent show resilient-workflow -o json | ConvertFrom-Json
$projectId = azd env get-value AZURE_AI_PROJECT_ID

az role assignment create `
  --assignee-object-id $agent.instance_identity.principal_id `
  --assignee-principal-type ServicePrincipal `
  --role "Foundry User" `
  --scope $projectId
```

Allow a few minutes for role assignment propagation before the first workflow request. If you
invoked the workflow before assigning the role, redeploy after assigning it so the hosted process
does not continue using a managed identity token acquired before the permission existed.

### Verify the deployed agent

```bash
azd ai agent invoke --new-session --new-conversation \
  "Reply briefly and include [DEPLOYED-READY]. Do not call any tools."
```

## Exercise crash recovery

Run this PowerShell script from the initialized project directory:

```powershell
$agent = azd ai agent show resilient-workflow -o json | ConvertFrom-Json
$endpoint = $agent.agent_endpoints.responses
$responsesBase = $endpoint.Split("?")[0]
$accessToken = az account get-access-token `
  --resource https://ai.azure.com `
  --query accessToken `
  -o tsv
$headers = @{
  Authorization = "Bearer $accessToken"
  "Content-Type" = "application/json"
  "Foundry-Features" = "HostedAgents=V1Preview"
}
$body = @{
  model = "resilient-workflow"
  input = "Call simulate_crash to demonstrate crash recovery, then report the result."
  background = $true
  store = $true
  stream = $false
} | ConvertTo-Json

$response = Invoke-RestMethod `
  -Method POST `
  -Uri $endpoint `
  -Headers $headers `
  -Body $body

Write-Host "Accepted $($response.id) with status $($response.status)."

$deadline = (Get-Date).AddMinutes(6)
do {
  Start-Sleep 2
  try {
    $response = Invoke-RestMethod `
      -Method GET `
      -Uri ($responsesBase + "/" + $response.id + "?api-version=v1") `
      -Headers $headers
  }
  catch {
    $statusCode = $_.Exception.Response.StatusCode.value__
    if ($_.Exception -is [System.Threading.Tasks.TaskCanceledException] `
      -or $statusCode -in 404, 409, 424, 500, 502, 503) {
      continue
    }
    throw
  }
} while ($response.status -notin "completed", "failed", "cancelled", "incomplete" `
  -and (Get-Date) -lt $deadline)

$response | ConvertTo-Json -Depth 20
```

The initial POST returns before the tool terminates the process. Polling can temporarily time out or
return `404`, `409`, `424`, or a retryable `5xx` while Foundry starts the replacement process.
Continue retrieving the same response ID. The final response must be `completed` and explain that
the crash recovery succeeded.

## Option 2: VS Code (Foundry Toolkit)

### Prerequisites

1. VS Code with the [Foundry Toolkit](https://marketplace.visualstudio.com/items?itemName=ms-windows-ai-studio.windows-ai-studio) extension.
2. The [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) extension.
3. Azure CLI authenticated with `az login`.

### Run and debug

Press **F5**. The agent starts and Agent Inspector opens automatically.

For a manual run, copy `.env.example` to `.env`, fill in the values, then run:

```bash
dotnet restore
dotnet run
```

Use Agent Inspector for normal prompts. Use the PowerShell script above for the crash demonstration
because it preserves the stored response ID while the hosted process restarts.

### Deploy

Run **Foundry Toolkit: Deploy Hosted Agent**, select **Container** as the deployment method, select
the Foundry project, confirm the deployment settings, and deploy. Assign the resulting agent
identity the **Foundry User** role on the project before invoking the workflow.

## Recovery and side effects

Workflow checkpoints, stored response events, and external side effects are not one transaction.
Recovery can repeat work after the last confirmed checkpoint. A real email, payment, queue
publication, or write API must accept an idempotency key so repeating a tool executor does not
repeat the business effect.

## Troubleshooting

**The response fails before the tool runs.** Assign **Foundry User** to the hosted agent managed
identity, wait for propagation, and redeploy if the agent was invoked before the role was assigned.

**The model does not call the tool.** Use the exact crash prompt from this README. The agent
instructions prohibit calling `simulate_crash` unless the user explicitly requests it.

**The process exits but the response never resumes.** Confirm the request used both
`background=true` and `store=true`, then keep polling the same response ID rather than submitting the
input again.

**A later crash request completes without terminating the process.** Start every demonstration with
a new session and conversation. `OnCheckpointRestoredAsync` indicates that the workflow instance was
restored from a checkpoint; this sample intentionally consumes that signal once.

**Recovery starts the wrong workflow shape.** Keep the workflow agent and executor IDs stable across
deployments. Changing them prevents persisted checkpoints from matching the reconstructed workflow.

**The CLI returns no visible assistant text.** Inspect the complete Responses event stream:

```bash
azd ai agent invoke --new-session --new-conversation --output raw \
  "Reply with [DIAGNOSTIC]. Do not call any tools."
```

Check the final `response.completed` or `response.failed` event. The friendly CLI output can be empty
for a failed response even when the command itself exits successfully.

## Next steps

- [Steering sample](../steering/)
- [Hosted agents overview](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/hosted-agents)
