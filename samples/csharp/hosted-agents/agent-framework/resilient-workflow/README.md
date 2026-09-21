# What this sample demonstrates

This sample hosts a deterministic Agent Framework workflow with resilient background Responses
enabled. Its crash mode intentionally terminates the hosted process while a workflow node is
running. AgentServer starts a replacement process and Agent Framework resumes the pending node from
the durable workflow checkpoint.

> [!WARNING]
> The `crash` mode deliberately terminates the agent process. Deploy this sample only to a
> development or test project.

## How it works

The workflow contains three executors:

```text
resilient-input -> resilient-work -> resilient-output
```

Send one of these inputs:

| Input | Behavior |
| --- | --- |
| `echo:<token>` | Completes immediately with `ECHO-COMPLETE:<token>`. |
| `long:<token>` | Waits for `LONG_RUNNING_DELAY_SECONDS`, then returns `LONG-RUN-COMPLETE:<token>`. |
| `crash:<token>` | Creates a durable crash marker, terminates the process, then returns `CRASH-RECOVERED:<token>:PROCESS-CHANGED` from the replacement process. |

The crash executor writes a marker beneath
`$HOME/.foundry-hosted-samples/resilient-workflow/`. The marker contains a random process
incarnation. On the first execution, the executor writes the marker and calls `Environment.Exit(70)`.
After AgentServer reclaims the stored background response, the replacement process reloads the
workflow checkpoint and repeats the pending executor. The executor finds the marker, verifies that
the process incarnation changed, and completes instead of terminating again.

Use a unique token for every crash test. Reusing a token intentionally reuses its existing marker.

Resilience is enabled when the Responses server is first registered:

```csharp
builder.Services.AddFoundryResponses(
    agent,
    configure: options => options.ResilientBackground = true);
```

Recovery applies only to stored background requests. Use `background=true` with `store=true`, or
omit `store` so the Responses API default remains enabled.

## Prerequisites

1. An existing Foundry project. This sample does not require a model deployment.
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
azd ai agent invoke --local "echo:LOCAL-READY"
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
azd ai agent invoke --new-session --new-conversation "echo:DEPLOYED-READY"
azd ai agent invoke --new-session --new-conversation "long:BACKGROUND-READY"
```

## Exercise crash recovery

Run this PowerShell script from the initialized project directory:

```powershell
$agent = azd ai agent show resilient-workflow -o json | ConvertFrom-Json
$endpoint = $agent.agent_endpoints.responses
$responsesBase = $endpoint.Split("?")[0]
$token = [Guid]::NewGuid().ToString("N")
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
  input = "crash:$token"
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

The initial POST returns before the process terminates. Polling can temporarily time out or return
`404`, `409`, `424`, or a retryable `5xx` while Foundry starts the replacement process. Continue
retrieving the same response ID. The final response must be `completed` and contain:

```text
CRASH-RECOVERED:<token>:PROCESS-CHANGED
```

## Option 2: VS Code (Foundry Toolkit)

### Prerequisites

1. VS Code with the [Foundry Toolkit](https://marketplace.visualstudio.com/items?itemName=ms-windows-ai-studio.windows-ai-studio) extension.
2. The [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) extension.
3. Azure CLI authenticated with `az login`.

### Run and debug

Press **F5**. The agent starts and Agent Inspector opens automatically.

For a manual run, copy `.env.example` to `.env`, then run:

```bash
dotnet restore
dotnet run
```

Use `echo:<token>` or `long:<token>` in Agent Inspector. Use the PowerShell script above for the
crash demonstration because it preserves the stored response ID while the hosted process restarts.

### Deploy

Run **Foundry Toolkit: Deploy Hosted Agent**, select **Container** as the deployment method, select
the Foundry project, confirm the deployment settings, and deploy. Assign the resulting agent
identity the **Foundry User** role on the project before invoking the workflow.

## Recovery and side effects

Workflow checkpoints, stored response events, crash markers, and external side effects are not one
transaction. Recovery can repeat work after the last confirmed checkpoint. A real email, payment,
queue publication, or write API must accept an idempotency key so repeating a node does not repeat
the business effect.

The marker in this sample prevents an intentional crash loop. It does not make unrelated external
operations idempotent.

## Troubleshooting

**The response fails after the first executor.** Assign **Foundry User** to the hosted agent managed
identity, wait for propagation, and redeploy if the agent was invoked before the role was assigned.

**The crash mode reports that it is disabled.** Set `ENABLE_CRASH_RECOVERY_DEMO=true` and redeploy.

**The process exits but the response never resumes.** Confirm the request used both
`background=true` and `store=true`, then keep polling the same response ID rather than submitting the
input again.

**The process does not terminate for a reused token.** Every token maps to one durable crash marker.
Generate a unique token for each demonstration.

**Recovery starts the wrong workflow shape.** Keep workflow agent and executor IDs stable across
deployments. Changing them prevents persisted checkpoints from matching the reconstructed workflow.

**The CLI returns no visible assistant text.** Inspect the complete Responses event stream:

```bash
azd ai agent invoke --new-session --new-conversation --output raw "echo:DIAGNOSTIC"
```

Check the final `response.completed` or `response.failed` event. The friendly CLI output can be empty
for a failed response even when the command itself exits successfully.

## Next steps

- [Steering sample](../steering/)
- [Hosted agents overview](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/hosted-agents)
