# Development cloud AI configuration

This prepares an explicit, Web-only opt-in for the existing narration and Decision-1 routing clients. The default is **off**. Merging this change or setting GitHub variables does not update a running Container App: an operator must separately authorize and run the release deployment. No inference request, cloud configuration change or new resource is part of this preparation.

## Operator settings

Set these **nonsecret variables** in the repository's GitHub **development environment** before an authorized release preview/deployment. Keep the existing deployment identity, registry and External ID variables required by `deploy-development.yml`.

| GitHub development variable | Value for the existing development Foundry resource | Bicep parameter | Web runtime setting |
| --- | --- | --- | --- |
| `WEB_AI_ENABLED` | `true` to opt in; unset or `false` keeps AI off | `webAiEnabled` | `MatchChat__Enabled` and `DecisionRouting__Enabled` |
| `MATCH_CHAT_ENDPOINT` | `https://mm-foundry-dev-7ntncaxgzqagy.cognitiveservices.azure.com/` | `matchChatEndpoint` | `MatchChat__Endpoint` |
| `MATCH_CHAT_DEPLOYMENT` | `matchmate-luna` | `matchChatDeployment` | `MatchChat__Deployment` |
| `DECISION_ROUTING_ENDPOINT` | `https://mm-foundry-dev-7ntncaxgzqagy.cognitiveservices.azure.com/` | `decisionRoutingEndpoint` | `DecisionRouting__Endpoint` |
| `DECISION_ROUTING_DEPLOYMENT` | `matchmate-decision-1` | `decisionRoutingDeployment` | `DecisionRouting__Deployment` |

The resource and deployments above were confirmed by a read-only Azure inspection on 25 October 2026. Both deployments reported `Succeeded`; the Foundry resource has local/key authentication disabled. The existing Web user-assigned managed identity already has `Cognitive Services User` at the Foundry resource scope. Those checks establish available configuration and access assignments, not a successful inference call or a verified hosted chat experience. Recheck the existing resource, deployment readiness and role assignment before activation; this template does not provision or repair them.

The hosted Web app already registers `ManagedIdentityCredential` using `Runtime__ManagedIdentityClientId`; the model clients receive that credential through dependency injection. The template continues to use the existing Web identity. Do not supply API keys or change `AZURE_CLIENT_ID` for the model clients. The workflow's `AZURE_CLIENT_ID` remains the deployment login identity, a separate purpose.

This configuration enables the existing default Luna narrator and Decision-1 router together. It does not add a Sol profile or configure the separate historical-chat model override. The existing Sol deployment alone does not activate that profile. Model availability, routing quality and the unfinished league-routing checkpoint retain their separate acceptance gates.

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

After a separately authorized deployment, verify the Web revision has the expected flags, endpoints and deployment names and that its existing managed identity still has access. Then verify real authenticated model behavior under a separately authorized inference/dogfood gate. `/alive` and `/health` establish hosting and Web-to-API health; they do not test model access or chat quality. No browser or paid-inference gate was run for this preparation.

Microsoft guidance: [Container Apps runtime environment variables](https://learn.microsoft.com/azure/container-apps/environment-variables) and [Foundry keyless authentication and the Cognitive Services User role](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/configure-entra-id).
