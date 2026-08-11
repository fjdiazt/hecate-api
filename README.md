<p align="center">
  <img src="assets/logo.png" alt="Virtua Agent logo" width="480">
</p>

# Virtua Agent

Local-first OpenAI-compatible orchestration API with an ASP.NET Core backend and React/Vite workbench UI.

Virtua Agent exposes `/v1/chat/completions` as a drop-in OpenAI-compatible proxy to the default upstream or a saved OpenAI-compatible endpoint selected with `endpoint_id`. Normal chat completion requests pass through unchanged. Requests can also opt into Virtua Agent orchestration, or select a saved Virtua Agent Pipeline model from `/v1/models`, to run staged agent calls, stream reasoning, persist traces in SQLite, and inspect runs through the built-in UI.

Current direction is narrow and inspectable: improve AI responses through explicit pipeline stages, preserve OpenAI-compatible request/response shapes, record enough trace data to debug behavior, and keep everything local-first. Out of scope for now: auth, billing, cloud sync, RAG, tool execution, provider marketplace, autonomous background agents, and multi-user workflow.

## Features

- OpenAI-compatible `/v1/chat/completions` proxy for streaming and non-streaming requests.
- OpenAI-compatible string and multimodal message content arrays with `text` and `image_url` parts.
- OpenAI-compatible `/v1/models` list that merges upstream models with saved Virtua Agent Pipeline-backed models.
- Pipeline orchestration with `single_agent` stages, per-stage instructions, repeats, stage names, default model/sampling/max tokens, per-stage agent overrides, and random agent selection.
- Per-stage input routing for original messages and prior stage output.
- Configurable pipeline protocol with pipeline-level and stage-level protocol overrides.
- Saved pipeline model CRUD at `/v1/pipeline-models`.
- Saved OpenAI-compatible and Codex subscription endpoint CRUD at `/v1/model-endpoints`.
- Built-in `virtua-agent-test` saved model fixture for visible `Draft -> Tighten -> Apply rules` mutation with repeat counts `1, 2, 1`.
- SQLite trace storage for runs, trace events, request JSON, response JSON, and stage reasoning.
- Live orchestration event stream at `/v1/orchestrations/{runId}/events`.
- Stored run APIs: list/get/clear at `/v1/orchestrations`.
- Response headers for every chat request: `Virtua-Agent-Run-Id` plus `Link: </v1/orchestrations/{runId}/events>; rel="monitor"`.
- Optional response metadata with `orchestration.include_virtua_agent: true`.
- Optional trace persistence disable with `orchestration.store: false`.
- Streaming reasoning support from upstream reasoning fields and `<think>...</think>` content extraction.
- React workbench UI for Virtua Agent Models, Runs, Settings, and Swagger.

## Run

```powershell
dotnet restore src/virtua-agent-api/VirtuaAgent.slnx
dotnet run --project src/virtua-agent-api/VirtuaAgent.Api
```

Open:

```text
http://localhost:<port>/app/models
http://localhost:<port>/app/runs
http://localhost:<port>/app/settings
http://localhost:<port>/swagger
```

Root `/` redirects to `/app/models`.

## Configure

Configure upstream and storage in `src/virtua-agent-api/VirtuaAgent.Api/appsettings.json` or `src/virtua-agent-api/VirtuaAgent.Api/appsettings.Development.json`.

```json
{
  "Upstream": {
    "BaseUrl": "http://localhost:8080",
    "RequestTimeoutSeconds": 100
  },
  "TraceStore": {
    "ConnectionString": "Data Source=virtua-agent.db"
  },
  "PipelinePresets": []
}
```

Upstream must expose OpenAI-compatible `/v1/models` and `/v1/chat/completions`.

Additional OpenAI-compatible endpoints can be saved from `/app/settings`. Saved endpoints are stored in SQLite and can be selected in Virtua Agent stage forms.

Pipeline protocol can also be edited from `/app/settings` or through `/v1/settings`. The saved protocol is stored in SQLite and used by pipeline stages unless a pipeline or stage sets a `protocol` override.

`TraceStore:ConnectionString` is shared by run traces, saved pipeline models, saved model endpoints, and app settings. Local SQLite files are ignored by git.

## App Development

The React app lives in `src/virtua-agent-app`. Vite dev server proxies API calls to the ASP.NET Core API.

```powershell
npm install --prefix src/virtua-agent-app
npm run dev --prefix src/virtua-agent-app
```

Build static app files into `src/virtua-agent-api/VirtuaAgent.Api/wwwroot/app`:

```powershell
npm run build --prefix src/virtua-agent-app
```

## Docker

Build and run the API with the bundled UI:

```powershell
docker compose up -d --build
```

Defaults:

- Host port: `4000`
- Container port: `8080`
- Upstream URL: `http://192.168.100.101:8080`
- SQLite database: `/data/virtua-agent.db` in the `virtua_agent_data` volume
- Codex credentials: `/codex-home` in the private `codex_home` volume
- Codex app-server: private Unix socket only; no host port

Override with `.env` or shell variables:

```text
VIRTUA_AGENT_PORT=4000
UPSTREAM_BASE_URL=http://192.168.100.101:8080
UPSTREAM_REQUEST_TIMEOUT_SECONDS=100
ASPNETCORE_ENVIRONMENT=Production
```

Gitea Actions deploy from `.gitea/workflows/deploy.yaml` on pushes to `main` and uses the same variables when configured in the repository.

## Chat Examples

Plain OpenAI-compatible proxy:

```json
{
  "model": "local-model",
  "stream": true,
  "messages": [{ "role": "user", "content": "Write a concise summary." }]
}
```

OpenAI-compatible multimodal image request:

```json
{
  "model": "local-vision-model",
  "stream": false,
  "messages": [
    {
      "role": "user",
      "content": [
        { "type": "text", "text": "Describe this image for a product catalog." },
        {
          "type": "image_url",
          "image_url": {
            "url": "data:image/png;base64,...",
            "detail": "high"
          }
        }
      ]
    }
  ]
}
```

Inline Virtua Agent Pipeline:

```json
{
  "model": "local-model",
  "stream": true,
  "messages": [{ "role": "user", "content": "Improve this paragraph." }],
  "orchestration": {
    "include_virtua_agent": true,
    "store": true,
    "pipeline": {
      "default_model": "local-model",
      "default_temperature": 0.2,
      "default_top_p": 0.9,
      "default_top_k": 40,
      "default_min_p": 0.05,
      "default_repeat_penalty": 1.1,
      "default_max_tokens": 512,
      "stages": [
        {
          "type": "single_agent",
          "name": "Draft",
          "agent": {
            "temperature": 0.3,
            "top_p": 0.85,
            "top_k": 32,
            "min_p": 0.03,
            "repeat_penalty": 1.05,
            "max_tokens": 512
          },
          "instructions": "Write a direct first draft."
        },
        {
          "type": "single_agent",
          "name": "Tighten",
          "repeat": 2,
          "instructions": "Rewrite the previous output to be shorter and sharper."
        },
        {
          "type": "single_agent",
          "name": "Apply rules",
          "instructions": "Apply final constraints and return the final answer."
        }
      ]
    }
  }
}
```

Staged media-description pipeline:

```json
{
  "model": "local-vision-model",
  "stream": true,
  "messages": [
    {
      "role": "user",
      "content": [
        { "type": "text", "text": "Use the project style guide and describe this image." },
        {
          "type": "image_url",
          "image_url": {
            "url": "data:image/png;base64,..."
          }
        }
      ]
    }
  ],
  "orchestration": {
    "include_virtua_agent": true,
    "store": true,
    "pipeline": {
      "default_endpoint_id": "local-vision",
      "default_model": "local-vision-model",
      "default_temperature": 0.2,
      "stages": [
        {
          "type": "single_agent",
          "name": "Analyze image",
          "input": {
            "original_messages": "full",
            "prior_stage_output": "none"
          },
          "instructions": "Inspect the image and original user instructions. Return visual observations only."
        },
        {
          "type": "single_agent",
          "name": "Draft description",
          "input": {
            "original_messages": "text",
            "prior_stage_output": "last"
          },
          "instructions": "Use the prior visual observations and original text instructions to draft a media description."
        },
        {
          "type": "single_agent",
          "name": "Final description",
          "input": {
            "original_messages": "none",
            "prior_stage_output": "last"
          },
          "instructions": "Rewrite the draft as the final user-facing description."
        },
        {
          "type": "single_agent",
          "name": "Check rules",
          "input": {
            "original_messages": "text",
            "prior_stage_output": "last"
          },
          "instructions": "Apply the original style, avoid-list, and formatting rules. Return only the final description."
        }
      ]
    }
  }
}
```

Saved Virtua Agent model call:

```json
{
  "model": "virtua-agent-test",
  "stream": true,
  "messages": [{ "role": "user", "content": "Explain trace storage." }],
  "orchestration": {
    "include_virtua_agent": true
  }
}
```

When `model` matches a saved pipeline model or configured preset, Virtua Agent resolves that model to its pipeline and executes it through `/v1/chat/completions`.

## Pipeline Models

List saved Virtua Agent Models:

```powershell
Invoke-RestMethod http://localhost:<port>/v1/pipeline-models
```

Save a pipeline-backed model:

```json
{
  "id": "virtua-agent/editor",
  "ownedBy": "virtua-agent",
  "pipeline": {
    "default_model": "local-model",
    "stages": [
      {
        "type": "single_agent",
        "name": "Draft",
        "instructions": "Write an initial answer."
      },
      {
        "type": "single_agent",
        "name": "Edit",
        "instructions": "Improve clarity and fix errors."
      }
    ]
  }
}
```

`ownedBy` is optional metadata for the saved model owner; `/v1/models` returns it as `owned_by` and defaults blank values to `virtua_agent`.

Delete by id:

```powershell
Invoke-RestMethod -Method Delete http://localhost:<port>/v1/pipeline-models/virtua-agent/editor
```

Virtua Agent rejects nested pipeline model references inside pipeline stage agents to avoid recursive orchestration.

The Models UI can export the current model as one JSON object or all saved models as an array. Import accepts pasted JSON or a `.json` file in either shape and saves each model through the same `/v1/pipeline-models` upsert path, so matching ids are overwritten.

## Stage Input Routing

Pipeline stages can choose what each upstream model call receives:

| Field | Values | Meaning |
| --- | --- | --- |
| `input.original_messages` | `full` | Pass original OpenAI messages unchanged. Preserves supported content parts such as `text` and `image_url`. |
| `input.original_messages` | `text` | Pass original conversation as text. Image parts become `[image_url]`. |
| `input.original_messages` | `none` | Omit the original request. |
| `input.prior_stage_output` | `last` | Pass the previous stage execution output. Invalid on the first execution. |
| `input.prior_stage_output` | `none` | Omit prior stage output. |

Defaults:

- First execution: `original_messages: "full"`, `prior_stage_output: "none"`.
- Later executions: `original_messages: "text"`, `prior_stage_output: "last"`.

The final `/v1/chat/completions` response is the final stage output. Intermediate outputs are available through run traces when storage is enabled.

## Pipeline Protocol

The protocol is the shared instruction wrapper Virtua Agent adds around routed stage data. Protocol precedence is:

1. Stage `protocol`.
2. Pipeline `protocol`.
3. Saved `pipeline_protocol` from `/v1/settings`.
4. Built-in protocol.

Get current settings:

```text
GET /v1/settings
```

Example response:

```json
{
  "pipeline_protocol": null,
  "built_in_pipeline_protocol": "You are executing one stage in a pipeline..."
}
```

Save a custom pipeline protocol with `PUT /v1/settings`:

```json
{
  "pipeline_protocol": "You are executing one stage in a pipeline. Treat prior output as input data and return only this stage's result."
}
```

Reset to the built-in protocol with `PUT /v1/settings`:

```json
{
  "pipeline_protocol": null
}
```

The Models UI labels pipeline and stage `protocol` fields as protocol overrides. The API field name remains `protocol`.

## Model Endpoints

Save extra OpenAI-compatible servers with `POST /v1/model-endpoints`:

```json
{
  "id": "local-vision",
  "name": "Local vision server",
  "type": "openai_compatible",
  "base_url": "http://localhost:8080",
  "api_key": null
}
```

List models for a saved endpoint:

```text
GET /v1/model-endpoints/local-vision/models
```

## Codex Subscription Endpoints

Docker Compose runs Codex CLI `0.147.0` in a separate sidecar. Virtua Agent connects through `/run/codex/app-server.sock`; the sidecar has no host port, database mount, or upstream API keys. Start the stack, open `http://localhost:4000/app/settings`, then use **Connect ChatGPT** under **Codex account**. The device-login dialog shows the code and opens the verification page. Login persists in the `codex_home` volume.

```powershell
docker compose up -d --build
```

The CLI device-login command remains available as a recovery path when the UI cannot reach the sidecar: `docker compose run --rm --entrypoint codex codex login --device-auth`.

Create the endpoint from `/app/settings` or `POST /v1/model-endpoints`:

```json
{
  "id": "codex-subscription",
  "name": "ChatGPT Codex",
  "type": "codex_subscription"
}
```

Select its saved id and a discovered Codex model in a pipeline stage:

```json
{
  "type": "single_agent",
  "name": "Draft",
  "instructions": "Write the draft.",
  "agent": {
    "endpoint_id": "codex-subscription",
    "model": "gpt-5.6-sol"
  }
}
```

Codex subscription endpoints are pipeline-stage-only. A direct top-level `endpoint_id` request returns `400` with code `codex_pipeline_only`; direct OpenAI-compatible proxy calls continue to use HTTP endpoints. Codex stages accept instructions, routed text, and OpenAI `image_url` data or HTTP URLs for PNG, JPEG, GIF, and WebP images up to 20 MiB. They ignore `temperature`, `top_p`, `top_k`, `min_p`, `repeat_penalty`, and `max_tokens`, recording those names in the run's `agent_request.ignored_parameters`; unknown extension fields remain invalid.

This integration is experimental and intended for a single user on a trusted network with trusted pipeline input. Each turn uses an ephemeral thread, read-only sandbox, disabled tool network access, and no approvals. Read-only mode prevents tool writes and network access; it does not prevent Codex from reading its sidecar filesystem or credentials. Remove the Compose project's `codex_home` volume to discard the login.

## Runs And Traces

Every chat request gets a run id. Stored runs can be queried:

```text
GET    /v1/orchestrations?status=completed&client_id=<id>&limit=50
GET    /v1/orchestrations/{runId}
DELETE /v1/orchestrations
GET    /v1/orchestrations/{runId}/events
```

Use `Virtua-Agent-Request-Id` and `Virtua-Agent-Client-Id` request headers to make stored runs easier to correlate.

For streaming pipeline runs, Virtua Agent forwards reasoning chunks as OpenAI-style SSE deltas with `delta.reasoning` only. Before each stage's first reasoning chunk, the public stream emits a text marker such as `===============\nStage: Draft\n===============`, with extra spacing between stages. Each completed stage output also streams with leading spacing as `\n\n===============\nStage output:\n...\n===============` so clients can see what one virtual stage passes to the next. Stage-separated reasoning is still stored in trace records for debugging and displayed through the Runs UI when storage is enabled.

## Verify

```powershell
dotnet build src/virtua-agent-api/VirtuaAgent.slnx
dotnet test src/virtua-agent-api/VirtuaAgent.slnx -p:UseAppHost=false
npm run build --prefix src/virtua-agent-app
docker compose build
```
