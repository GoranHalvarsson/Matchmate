# Development cloud AI configuration

Hosted AI is an explicit, Web-only opt-in for narration and Decision-1 workflow routing. The deployment template defaults to **off**. Configure your own Azure resource and deployments below, then run an authorized release deployment; setting GitHub variables alone does not update a running Container App.

## Operator settings

Set these **nonsecret variables** in the repository's GitHub **development environment** before an authorized release preview/deployment. Keep the existing deployment identity, registry and External ID variables required by `deploy-development.yml`.

Replace `<your-foundry-resource>` with the Foundry resource name in **your own Azure account**, and use deployment names from that resource. The deployment names below show Matchmate's configuration; replace them if yours differ. Do not enter the endpoint placeholder literally.

| GitHub development variable | Example value — replace with your own Azure configuration | Bicep parameter | Web runtime setting |
| --- | --- | --- | --- |
| `WEB_AI_ENABLED` | `true` to opt in; unset or `false` keeps AI off | `webAiEnabled` | `MatchChat__Enabled` and `DecisionRouting__Enabled` |
| `MATCH_CHAT_ENDPOINT` | `https://<your-foundry-resource>.cognitiveservices.azure.com/` | `matchChatEndpoint` | `MatchChat__Endpoint` |
| `MATCH_CHAT_DEPLOYMENT` | `matchmate-luna` | `matchChatDeployment` | `MatchChat__Deployment` |
| `DECISION_ROUTING_ENDPOINT` | `https://<your-foundry-resource>.cognitiveservices.azure.com/` | `decisionRoutingEndpoint` | `DecisionRouting__Endpoint` |
| `DECISION_ROUTING_DEPLOYMENT` | `matchmate-decision-1` | `decisionRoutingDeployment` | `DecisionRouting__Deployment` |

Before enabling AI, verify that your Foundry resource and model deployments are ready and that the Web user-assigned managed identity has `Cognitive Services User` at the Foundry resource scope. This template does not provision or repair the resource, deployments or access assignment.

The hosted Web app already registers `ManagedIdentityCredential` using `Runtime__ManagedIdentityClientId`; the model clients receive that credential through dependency injection. The template continues to use the existing Web identity. Do not supply API keys or change `AZURE_CLIENT_ID` for the model clients. The workflow's `AZURE_CLIENT_ID` remains the deployment login identity, a separate purpose.

## Model roles

Matchmate has three deployed models in Microsoft Foundry:

| Model | Deployment | Role in the current hosted configuration |
| --- | --- | --- |
| Microsoft Decision-1 | `matchmate-decision-1` | Selects a fixed workflow for the application to run |
| OpenAI GPT-6 Luna | `matchmate-luna` | Current shared narrator for replay, league and historical evidence |
| OpenAI GPT-6.1 Sol | `matchmate-sol61` | Deployed and available as an optional historical narrator; the current hosted configuration has no historical override selected |

Historical narration falls back to `MatchChat:Deployment` when no override is set. To select Sol for historical narration, set the optional Web application configuration `HistoricalReplyModel:Deployment` (environment variable `HistoricalReplyModel__Deployment`) to `matchmate-sol61`. It shares the `MatchChat` connection settings. The existing deployment template does not emit this optional override or expose a GitHub variable or Bicep parameter for it. Model availability alone does not establish inference access or routing quality.

## Validation and disabling

`.github/scripts/deployment-config.mjs` validates the complete deployment configuration before one append to `GITHUB_ENV`. It exports only the named nonsecret allowlist, including the five AI variables. A validation error exports none of the deployment variables and does not echo the rejected value.

- `WEB_AI_ENABLED` accepts only literal `true`, `false`, or unset/empty. Unset/empty becomes `false`.
- When enabled, all four endpoint/deployment settings are required.
- When disabled, either all four settings must be empty, or all four must remain valid and complete. Partial or invalid settings fail even when disabled. To disable an already configured environment, change only `WEB_AI_ENABLED` to `false`; the four valid values can remain in GitHub.
- Endpoints must be public Azure resource roots over HTTPS: one DNS label followed by `.services.ai.azure.com`, `.cognitiveservices.azure.com`, or `.openai.azure.com`, optionally with a trailing `/`. Credentials, explicit ports, extra path segments, queries, fragments, private/IP hosts and control characters are rejected. This is a conservative subset of the application's `AzureModelEndpoint` rules.
- Deployment names must contain 1–128 ASCII letters, digits, `_`, `.`, or `-`, starting with a letter or digit. Paths, queries, whitespace, control characters and shell punctuation are rejected.

With AI disabled, Bicep emits explicit `false` flags for both clients and no model endpoints or deployment names. The API and Worker receive no AI settings. Turning the flag off takes effect only after the separately authorized deployment creates the new revision.

## Preview and deployment boundary

Preview validation, what-if and deployment construct identical Bicep parameter arrays from the validated variables. The retained validator comes from the workflow source before the selected release is checked out. A selected older release without all five AI parameters can still be previewed with AI off; an enabled request fails with a clear error instead of silently dropping the AI configuration. Choose a release containing this template before enabling AI.

After a separately authorized deployment, verify the Web revision has the expected flags, endpoints and deployment names and that its existing managed identity still has access. Then verify real authenticated model behavior under a separately authorized inference/dogfood gate. `/alive` and `/health` establish hosting and Web-to-API health; they do not test model access or chat quality.

Microsoft guidance: [Container Apps runtime environment variables](https://learn.microsoft.com/azure/container-apps/environment-variables) and [Foundry keyless authentication and the Cognitive Services User role](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/configure-entra-id).
