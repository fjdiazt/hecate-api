# Codex Subscription Provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a Virtua Agent pipeline stage run through the official Codex CLI using the deployment owner's ChatGPT subscription while preserving existing OpenAI-compatible responses, streaming, and traces.

**Architecture:** Add a `codex_subscription` saved endpoint kind. Run `codex app-server` in a dedicated Docker Compose sidecar and connect through a private Unix socket; open and initialize one connection per discovery or turn operation, use an ephemeral Codex thread, then close it. The sidecar receives no Virtua Agent database, upstream keys, or host port. Direct Chat Completions use remains unsupported because Codex turns cannot faithfully reproduce arbitrary OpenAI role and sampling semantics.

**Tech Stack:** ASP.NET Core 10, C# Unix domain sockets, Codex CLI `0.147.0`, Codex app-server JSON-RPC, SQLite, xUnit, React, TypeScript, Mantine, Docker Compose.

## Global Constraints

- Endpoint kinds are exactly `openai_compatible` and `codex_subscription`.
- Existing endpoint rows and API requests without `kind` remain `openai_compatible`.
- `codex_subscription` is allowed only for resolved pipeline stages. Direct top-level `endpoint_id` calls return HTTP `400` with code `codex_pipeline_only` before an SSE response starts.
- Use the official Codex CLI only. Never call private ChatGPT endpoints or read, return, log, trace, or store ChatGPT tokens.
- Require `account/read` to report `account.type == "chatgpt"`. Reject API-key, Bedrock, personal-token, and unauthenticated modes.
- Run Codex in a Docker Compose sidecar. Do not expose a host port or require an externally hosted agent service.
- Connect through `unix:///run/codex/app-server.sock`. Open one connection per operation; do not add connection pooling until measurements justify it.
- Do not pass `/data`, `virtua-agent.db`, `OPENAI_API_KEY`, `CODEX_API_KEY`, or upstream endpoint keys into the sidecar.
- Every turn uses `approvalPolicy: "never"`, `thread/start.ephemeral: true`, `thread/start.sandbox: "read-only"`, and `turn/start.sandboxPolicy: { "type": "readOnly", "networkAccess": false }`.
- Codex `0.147.0` has no readable-root restriction. This provider is experimental and trusted-input-only because Codex tools can read files visible inside the sidecar, including its own `CODEX_HOME`.
- Cancellation sends `turn/interrupt` and waits at most five seconds for `turn/completed`. On timeout, close that operation's socket and report cancellation; never restart the shared sidecar from the API.
- Reject `temperature`, `top_p`, `top_k`, `min_p`, `repeat_penalty`, `max_tokens`, and unknown Chat Completions extension fields for Codex stages. Never silently ignore them.
- Support text and standard OpenAI `image_url` parts. Pass HTTP(S) and validated data-image URLs directly as Codex `image` inputs.
- Limit decoded data-image payloads to 20 MiB and accept only PNG, JPEG, GIF, and WebP MIME types.
- Keep Codex credentials in a sidecar-only `codex_home` volume. The API mounts only the shared socket volume.
- Deployment remains single-user, trusted-network, and trusted-input only. Every caller shares the owner's subscription quota.
- No Claude provider, browser login UI, multi-user credentials, remote Codex host, tool approval UI, or automatic account provisioning.

---

## Locked API Contract

Existing endpoint requests remain valid:

```json
{
  "id": "local-llama",
  "name": "Local llama.cpp",
  "kind": "openai_compatible",
  "base_url": "http://localhost:8080",
  "api_key": null
}
```

Codex endpoint request:

```json
{
  "id": "codex-subscription",
  "name": "ChatGPT Codex",
  "kind": "codex_subscription"
}
```

Codex endpoint response:

```json
{
  "id": "codex-subscription",
  "name": "ChatGPT Codex",
  "kind": "codex_subscription",
  "base_url": null,
  "has_api_key": false
}
```

Pipeline selection remains unchanged:

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

Authenticate before starting the sidecar:

```powershell
docker compose stop codex
docker compose run --rm --entrypoint codex codex login --device-auth
docker compose up -d codex api
```

## File Map

- Modify `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/ModelEndpointModels.cs`: endpoint kind and nullable URL contract.
- Modify `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/SqliteModelEndpointStore.cs`: backward-compatible `kind` migration and persistence.
- Modify `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/ModelEndpointsEndpoint.cs`: kind validation and routed discovery.
- Create `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/ModelEndpointDispatcher.cs`: concrete HTTP/Codex selection module.
- Create `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexOptions.cs`: socket, sidecar working directory, connection timeout, and interrupt timeout.
- Create `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexAppServerProtocol.cs`: JSON-RPC creation and parsing helpers.
- Create `src/virtua-agent-api/VirtuaAgent.Api/Codex/ICodexAppServerClient.cs`: external app-server seam and its narrow input/result records, used by production and fake-test adapters.
- Create `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexAppServerClient.cs`: per-operation Unix-socket client.
- Create `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexSubscriptionClient.cs`: OpenAI DTO and SSE mapping.
- Modify `src/virtua-agent-api/VirtuaAgent.Api/Orchestration/PipelineExecutor.cs`: dispatch saved endpoints and reject unsupported Codex options.
- Modify `src/virtua-agent-api/VirtuaAgent.Api/Endpoints/ChatCompletionsEndpoint.cs`: reject direct Codex use before SSE starts.
- Modify `src/virtua-agent-api/VirtuaAgent.Api/Program.cs`: register options and concrete modules.
- Modify `src/virtua-agent-api/VirtuaAgent.Api/appsettings.json`: non-secret socket defaults.
- Add focused tests under `src/virtua-agent-api/VirtuaAgent.Tests/` for migration, socket lifecycle, mapping, routing, and direct rejection.
- Modify `src/virtua-agent-app/src/types.ts` and `src/virtua-agent-app/src/App.tsx`: endpoint-kind UI.
- Modify `Dockerfile`: pinned Codex sidecar target.
- Modify `docker-compose.yml`: private sidecar, credential volume, and socket volume.
- Modify `README.md`: setup, authentication, limits, security, and examples.

---

### Task 1: Persist Endpoint Kinds

**Files:**
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/ModelEndpointModels.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/SqliteModelEndpointStore.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/ModelEndpointsEndpoint.cs`
- Create: `src/virtua-agent-api/VirtuaAgent.Tests/SqliteModelEndpointStoreTests.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/ModelEndpointsEndpointTests.cs`

**Interfaces:**
- Produces: `ModelEndpointKinds.OpenAiCompatible`, `ModelEndpointKinds.CodexSubscription`, and `ModelEndpointDefinition.Kind`.
- Produces: API `kind` and nullable `base_url` fields consumed by Tasks 4 and 5.

- [ ] **Step 1: Write failing endpoint contract tests**

Add `SaveCodexSubscriptionEndpointNeedsNoUrlOrKey`, `MissingKindDefaultsToOpenAiCompatible`, and `UnknownKindIsRejected`. Assert Codex saves with `base_url: null` and `has_api_key: false`; the legacy shape still validates its URL; unknown kinds return `invalid_endpoint_kind`.

- [ ] **Step 2: Confirm the tests fail**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.Tests/VirtuaAgent.Tests.csproj --filter "FullyQualifiedName~ModelEndpointsEndpointTests" -p:UseAppHost=false
```

Expected: FAIL because endpoint DTOs have no `Kind` and `base_url` is mandatory.

- [ ] **Step 3: Add the endpoint kind contract**

```csharp
public static class ModelEndpointKinds
{
    public const string OpenAiCompatible = "openai_compatible";
    public const string CodexSubscription = "codex_subscription";
}
```

Add `Kind` to `ModelEndpointDefinition`, defaulting to `OpenAiCompatible`. Make request and response `BaseUrl` nullable. Normalize missing or blank kinds to `openai_compatible`; map the stored empty URL to API `null` for Codex.

- [ ] **Step 4: Add the backward-compatible SQLite migration**

Inspect `PRAGMA table_info(model_endpoints)` during initialization. When `kind` is absent, execute:

```sql
ALTER TABLE model_endpoints
ADD COLUMN kind TEXT NOT NULL DEFAULT 'openai_compatible';
```

Include `kind` in every endpoint `SELECT`, `INSERT`, conflict update, and reader mapping. Store `base_url = ''` and `api_key = NULL` for Codex. Preserve blank-key-means-keep only for HTTP endpoint updates.

- [ ] **Step 5: Apply kind-specific validation**

Reject unknown kinds with parameter `kind` and code `invalid_endpoint_kind`. Require an absolute HTTP(S) URL only for `openai_compatible`. Ignore any submitted URL/key for Codex by storing empty/null values.

- [ ] **Step 6: Test old-schema migration and persistence**

Create a temporary database with the old endpoint table, initialize the store, and assert its row becomes `openai_compatible`. Save/reload a Codex endpoint and assert kind, empty stored URL, and null key.

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.Tests/VirtuaAgent.Tests.csproj --filter "FullyQualifiedName~ModelEndpoint" -p:UseAppHost=false
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints src/virtua-agent-api/VirtuaAgent.Tests/ModelEndpointsEndpointTests.cs src/virtua-agent-api/VirtuaAgent.Tests/SqliteModelEndpointStoreTests.cs
git commit -m "feat: add model endpoint kinds"
```

---

### Task 2: Add the Codex Unix-Socket Client

**Files:**
- Create: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexOptions.cs`
- Create: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexAppServerProtocol.cs`
- Create: `src/virtua-agent-api/VirtuaAgent.Api/Codex/ICodexAppServerClient.cs`
- Create: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexAppServerClient.cs`
- Create: `src/virtua-agent-api/VirtuaAgent.Tests/CodexAppServerClientTests.cs`

**Interfaces:**
- Produces: `ICodexAppServerClient.ListModelsAsync(CancellationToken)`.
- Produces: `ICodexAppServerClient.RunTurnAsync(string? model, IReadOnlyList<CodexInputItem> input, Func<CodexTurnDelta, CancellationToken, Task>?, CancellationToken)`.
- Produces: `CodexTurnResult` with authoritative final text, actual model, and optional token usage.

- [ ] **Step 1: Define the narrow records and seam**

```csharp
public sealed record CodexInputItem(string Type, string? Text = null, string? Url = null);
public sealed record CodexTurnDelta(string? Content = null, string? Reasoning = null);
public sealed record CodexModel(string Id);

public sealed record CodexTurnResult
{
    public string Id { get; init; } = "";
    public string Model { get; init; } = "";
    public string Content { get; init; } = "";
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

public interface ICodexAppServerClient
{
    Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<CodexTurnResult> RunTurnAsync(
        string? model,
        IReadOnlyList<CodexInputItem> input,
        Func<CodexTurnDelta, CancellationToken, Task>? onDelta = null,
        CancellationToken cancellationToken = default);
}
```

This seam is earned: production uses a Unix-socket adapter and Task 3 tests use an in-memory fake adapter.

- [ ] **Step 2: Define non-secret options**

```csharp
public sealed record CodexOptions
{
    public string SocketPath { get; init; } = "/run/codex/app-server.sock";
    public string WorkingDirectory { get; init; } = "/work";
    public int ConnectTimeoutSeconds { get; init; } = 5;
    public int InterruptTimeoutSeconds { get; init; } = 5;
}
```

- [ ] **Step 3: Write a scripted Unix-socket test server**

Inside `CodexAppServerClientTests`, bind `Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)` to a short temporary `.sock` path. Accept one connection, read newline-delimited requests, and return deterministic JSON-RPC responses. Delete the socket file in test cleanup.

Add these tests:

- `ListModelsInitializesAuthenticatesAndPaginates`
- `RunTurnUsesEphemeralReadOnlyThreadAndStreamsDeltas`
- `CancellationSendsTurnInterrupt`
- `NonChatGptAccountIsRejectedWithoutLeakingAccountData`
- `ClosedSocketReturnsCodexUnavailableError`

Assert the exact outgoing fields:

```json
{"method":"thread/start","params":{"cwd":"/work","approvalPolicy":"never","sandbox":"read-only","ephemeral":true,"serviceName":"virtua-agent"}}
{"method":"turn/start","params":{"threadId":"thr_1","input":[],"sandboxPolicy":{"type":"readOnly","networkAccess":false}}}
```

- [ ] **Step 4: Confirm socket tests fail**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.Tests/VirtuaAgent.Tests.csproj --filter "FullyQualifiedName~CodexAppServerClientTests" -p:UseAppHost=false
```

Expected: FAIL because the Codex client does not exist.

- [ ] **Step 5: Implement one connection per operation**

For each public method:

1. Validate `SocketPath` is an absolute Unix path.
2. Create an Unix-domain `Socket` and connect using a linked token capped by `ConnectTimeoutSeconds`.
3. Wrap it in `NetworkStream`, `StreamReader`, and `StreamWriter` with UTF-8 and `AutoFlush = true`.
4. Send `initialize`, wait for its matching response, then send the `initialized` notification.
5. Send `account/read` with `{ "refreshToken": true }`.
6. Continue only when `result.account.type` equals `chatgpt`.
7. Dispose the connection after the operation; do not cache sockets or background readers.

Map connection refusal, missing socket, and premature EOF to a stable exception message: `Codex sidecar is unavailable.` Never include raw payloads or account fields.

- [ ] **Step 6: Implement JSON-RPC reading without a correlation layer**

Use monotonically increasing request IDs within one connection. While waiting for a response ID, read lines sequentially and dispatch any interleaved notifications to the active operation. No `TaskCompletionSource` dictionary, channel, singleton process state, or connection pool is needed.

`CodexAppServerProtocol` must provide pure helpers for response IDs, errors, model pages, thread/turn IDs, reasoning deltas, content deltas, final agent messages, token totals, and `turn/completed` status.

- [ ] **Step 7: Implement model discovery**

Send `model/list` with `{ "limit": 100, "includeHidden": false }`. Follow `nextCursor` until null. Return distinct model IDs in server order. Reject non-ChatGPT auth before model discovery.

- [ ] **Step 8: Implement one ephemeral turn**

Send `thread/start` with the configured working directory, optional model, `approvalPolicy: "never"`, `sandbox: "read-only"`, `serviceName: "virtua-agent"`, and `ephemeral: true`. Send `turn/start` with input and:

```json
{"type":"readOnly","networkAccess":false}
```

Forward `item/reasoning/summaryTextDelta`, `item/reasoning/textDelta`, and `item/agentMessage/delta`. Use completed `agentMessage.text` as authoritative. Read token totals from `thread/tokenUsage/updated` when present. Treat `turn.status == "failed"` as an error using only its sanitized message.

- [ ] **Step 9: Implement cancellation**

When caller cancellation occurs after `turn/start` returns IDs, create a fresh timeout token, send `turn/interrupt`, and read until `turn/completed` reports `interrupted` or five seconds expire. Dispose the socket either way. Throw `OperationCanceledException` tied to the caller token. Do not send `thread/delete`; the thread is ephemeral.

- [ ] **Step 10: Run socket tests**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.Tests/VirtuaAgent.Tests.csproj --filter "FullyQualifiedName~CodexAppServerClientTests" -p:UseAppHost=false
```

Expected: PASS without Codex installation or network access.

- [ ] **Step 11: Commit**

```powershell
git add src/virtua-agent-api/VirtuaAgent.Api/Codex src/virtua-agent-api/VirtuaAgent.Tests/CodexAppServerClientTests.cs
git commit -m "feat: add Codex socket client"
```

---

### Task 3: Map Pipeline Requests to Codex Turns

**Files:**
- Create: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexSubscriptionClient.cs`
- Create: `src/virtua-agent-api/VirtuaAgent.Tests/CodexSubscriptionClientTests.cs`

**Interfaces:**
- Consumes: `ICodexAppServerClient` from Task 2.
- Produces: `ListModelsAsync`, `ChatAsync`, and `StreamChatAsync` using existing OpenAI DTOs and callback data format.

- [ ] **Step 1: Write failing mapping tests with a fake adapter**

Use a fake `ICodexAppServerClient` that records inputs, emits reasoning/content deltas, and returns final text. Add tests for role-aware text, multimodal part order, data-image validation, final response shape, usage mapping, and SSE callback payloads.

- [ ] **Step 2: Map messages in stable order**

For each OpenAI message, emit text as:

```text
Role: user

message text
```

Preserve message order. For content arrays, preserve part order and emit each text/image part separately. Do not include `endpoint_id`, orchestration JSON, or extension fields in model input.

- [ ] **Step 3: Validate and pass image URLs directly**

Map HTTP(S) URLs to `CodexInputItem("image", Url: url)`. For data URLs, require base64 encoding and MIME type `image/png`, `image/jpeg`, `image/gif`, or `image/webp`. Calculate decoded size before allocation, reject payloads over `20 * 1024 * 1024`, then pass the original validated data URL as an `image` input. Use `PipelineValidationException` parameter `messages` and code `invalid_image_url` for malformed or unsupported images.

- [ ] **Step 4: Map completed turns**

Return one assistant choice with finish reason `stop`, ID `chatcmpl_` plus Codex turn ID, actual result model when present, and nullable usage. Leave usage null when both token counts are absent.

Map discovered models to `ModelDto` with `Object = "model"` and `OwnedBy = "openai-codex"` while preserving server order.

- [ ] **Step 5: Map streaming deltas**

Emit serialized callback data without a `data:` prefix because `PipelineExecutor` already parses callback payloads. Reasoning uses `choices[0].delta.reasoning`; answer text uses `choices[0].delta.content`; final chunk uses `finish_reason: "stop"`.

- [ ] **Step 6: Run mapping tests**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.Tests/VirtuaAgent.Tests.csproj --filter "FullyQualifiedName~CodexSubscriptionClientTests" -p:UseAppHost=false
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexSubscriptionClient.cs src/virtua-agent-api/VirtuaAgent.Tests/CodexSubscriptionClientTests.cs
git commit -m "feat: map Codex subscription turns"
```

---

### Task 4: Route Saved Endpoints and Enforce Pipeline-Only Use

**Files:**
- Create: `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/ModelEndpointDispatcher.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/ModelEndpointsEndpoint.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/Orchestration/PipelineExecutor.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/Endpoints/ChatCompletionsEndpoint.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/Program.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/appsettings.json`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/ModelEndpointsEndpointTests.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/PipelineExecutorTests.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/ChatCompletionsEndpointTests.cs`

**Interfaces:**
- Consumes: existing `IOpenAiCompatibleUpstreamClient` and Task 3 `CodexSubscriptionClient`.
- Produces: concrete `ModelEndpointDispatcher` used by model discovery and pipeline execution. Do not add an interface for this one implementation.

- [ ] **Step 1: Add failing routing tests**

Assert HTTP endpoints call `IOpenAiCompatibleUpstreamClient`, Codex endpoints call `CodexSubscriptionClient`, and endpoint model discovery uses the same concrete dispatcher.

- [ ] **Step 2: Add the concrete dispatcher**

Expose `ListModelsAsync`, `ChatAsync`, and `StreamChatAsync`. Each method switches on `endpoint.Kind`; HTTP calls the existing upstream client and Codex calls `CodexSubscriptionClient`. Throw for unknown stored kinds.

- [ ] **Step 3: Reject unsupported Codex stage options**

After resolving a Codex endpoint and before publishing `agent_request`, reject the first populated field among `temperature`, `top_p`, `top_k`, `min_p`, `repeat_penalty`, and `max_tokens`. Reject non-empty `ExtraFields` using parameter `request`. Use code `codex_parameter_unsupported` and name the rejected field.

- [ ] **Step 4: Route pipeline stages through the dispatcher**

Keep the existing default-upstream branch. Send only resolved saved endpoints through `ModelEndpointDispatcher`, for both streaming and non-streaming calls. Leave reasoning headers, stage outputs, trace storage, and final-answer behavior in `PipelineExecutor`.

- [ ] **Step 5: Reject direct Codex calls before SSE starts**

Resolve top-level `endpoint_id` before `Response.StartAsync`. When it selects Codex without a pipeline, return HTTP `400`, parameter `endpoint_id`, code `codex_pipeline_only`, and message `Codex subscription endpoints can only be used by Virtua Agent pipeline stages.` Test streaming and non-streaming requests.

- [ ] **Step 6: Register concrete modules and options**

Register `CodexOptions`, singleton `ICodexAppServerClient`/`CodexAppServerClient`, singleton `CodexSubscriptionClient`, and singleton concrete `ModelEndpointDispatcher`.

Add:

```json
"Codex": {
  "SocketPath": "/run/codex/app-server.sock",
  "WorkingDirectory": "/work",
  "ConnectTimeoutSeconds": 5,
  "InterruptTimeoutSeconds": 5
}
```

- [ ] **Step 7: Run backend tests**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.slnx -p:UseAppHost=false
```

Expected: PASS without a real Codex sidecar.

- [ ] **Step 8: Commit**

```powershell
git add src/virtua-agent-api/VirtuaAgent.Api src/virtua-agent-api/VirtuaAgent.Tests
git commit -m "feat: route Codex pipeline stages"
```

---

### Task 5: Add Codex Endpoints to Settings

**Files:**
- Modify: `src/virtua-agent-app/src/types.ts`
- Modify: `src/virtua-agent-app/src/App.tsx`

**Interfaces:**
- Consumes: endpoint `kind` and nullable `base_url` from Task 1.
- Produces: saved Codex endpoint IDs consumed by existing stage endpoint/model selectors.

- [ ] **Step 1: Update TypeScript types**

Add `ModelEndpointKind = 'openai_compatible' | 'codex_subscription'`; make response `base_url` nullable and request URL/key optional nullable fields.

- [ ] **Step 2: Add a kind selector**

Default new drafts to `openai_compatible`. Add Mantine `Select` options `OpenAI-compatible` and `Codex subscription`. When switching to Codex, clear draft URL and key.

- [ ] **Step 3: Render only relevant fields**

Show URL and API key only for HTTP endpoints. Keep name, saved read-only ID, save, delete, and refresh-model controls for both kinds. Describe Codex list rows as `Codex subscription`.

- [ ] **Step 4: Save exact payloads**

Codex sends kind plus `base_url: null` and `api_key: null`. HTTP preserves existing blank-key-means-keep behavior.

- [ ] **Step 5: Build and inspect UI**

```powershell
npm run build --prefix src/virtua-agent-app
```

Run the app and inspect `/app/settings` at `1440x900` and `390x844`. Verify no overlap, endpoint ID remains visible, HTTP fields hide for Codex, and discovery failures appear in UI.

- [ ] **Step 6: Commit**

```powershell
git add src/virtua-agent-app/src/types.ts src/virtua-agent-app/src/App.tsx src/virtua-agent-api/VirtuaAgent.Api/wwwroot/app
git commit -m "feat: configure Codex endpoints"
```

---

### Task 6: Package the Private Codex Sidecar

**Files:**
- Modify: `Dockerfile`
- Modify: `docker-compose.yml`

**Interfaces:**
- Produces: Codex CLI `0.147.0` sidecar listening on `/run/codex/app-server.sock`.
- Produces: private `codex_home` credential volume and shared `codex_socket` volume.

- [ ] **Step 1: Add the pinned sidecar target**

Add this independent Docker target; do not copy Codex into the ASP.NET final image:

```dockerfile
FROM node:22-bookworm-slim AS codex-runtime
ARG CODEX_VERSION=0.147.0
RUN npm install --global "@openai/codex@${CODEX_VERSION}" \
    && codex --version \
    && mkdir -p /codex-home /run/codex /work
ENV CODEX_HOME=/codex-home
ENTRYPOINT ["codex"]
```

- [ ] **Step 2: Add the Codex service**

Configure `codex` with build target `codex-runtime`, no `ports`, `CODEX_HOME=/codex-home`, and volumes `codex_home:/codex-home` plus `codex_socket:/run/codex`.

Override its entrypoint to `/bin/sh -c` and run:

```sh
rm -f /run/codex/app-server.sock && exec codex app-server --listen unix:///run/codex/app-server.sock
```

The removal handles stale socket files after an unclean restart.

- [ ] **Step 3: Connect the API service**

Mount only `codex_socket:/run/codex` into `api`. Add `Codex__SocketPath=/run/codex/app-server.sock` and `Codex__WorkingDirectory=/work`. Add `depends_on: [codex]`. Do not mount `codex_home` or `/data` into the opposite service.

- [ ] **Step 4: Build and inspect isolation**

```powershell
docker compose build api codex
docker compose run --rm --entrypoint codex codex --version
docker compose config
```

Expected: version includes `codex-cli 0.147.0`; Codex has no host port and no `/data`; API has no `codex_home` mount.

- [ ] **Step 5: Verify persistent authentication**

```powershell
docker compose stop codex
docker compose run --rm --entrypoint codex codex login --device-auth
docker compose run --rm --entrypoint codex codex login status
docker compose up -d codex api
docker compose restart codex
docker compose run --rm --entrypoint codex codex login status
```

Expected: ChatGPT login survives restart. Do not record account identifiers.

- [ ] **Step 6: Commit**

```powershell
git add Dockerfile docker-compose.yml
git commit -m "feat: add Codex sidecar"
```

---

### Task 7: Document and Verify the Complete Flow

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: Tasks 1-6.
- Produces: operator setup and explicit security limits.

- [ ] **Step 1: Document setup and use**

Add `Codex Subscription Endpoints` covering sidecar architecture, stop/run/up device authentication commands, Settings/API endpoint creation, stage selection, credential persistence, and removal of `codex_home` to log out. Include the locked JSON examples from this plan.

- [ ] **Step 2: Document hard limits**

State plainly:

- Codex subscription endpoints are pipeline-stage-only.
- Sampling, max-token, and unknown extension fields are rejected.
- Direct OpenAI-compatible proxy calls still use HTTP endpoints.
- The feature is experimental, single-user, trusted-network, and trusted-input only.
- Read-only prevents writes and tool network access but does not prevent Codex from reading its own sidecar filesystem and credentials.
- The sidecar is intentionally denied access to Virtua Agent's database and upstream endpoint keys.

- [ ] **Step 3: Run automated verification**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.slnx -p:UseAppHost=false
npm run build --prefix src/virtua-agent-app
docker compose build api codex
git diff --check
```

Expected: all commands exit `0`.

- [ ] **Step 4: Run authenticated end-to-end checks**

Create a two-stage pipeline with one Codex stage. Call `/v1/chat/completions` once non-streaming and once streaming. Verify final answer shape, reasoning deltas, Runs-stage output/reasoning, model discovery, and trace behavior. Cancel a live request and verify the socket test plus runtime logs show `turn/interrupt` rather than a sidecar restart.

Inspect the API database, API logs, Codex logs, and HTTP responses for token material. Verify the Codex container has no `/data` mount and restarting it preserves login.

- [ ] **Step 5: Commit**

```powershell
git add README.md
git commit -m "docs: explain Codex subscription setup"
```

- [ ] **Step 6: Review final scope**

```powershell
git status --short
git diff --stat HEAD~7..HEAD
git log -7 --oneline
```

Expected: only planned source, tests, built UI assets, Docker files, and README changes; no database, auth, socket, log, or temporary files tracked.
