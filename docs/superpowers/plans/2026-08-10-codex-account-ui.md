# Codex Account UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an operator connect, inspect, cancel, sign out, and switch the single global ChatGPT subscription account entirely through Virtua Agent Settings, including Docker deployments.

**Architecture:** Correct the existing Codex app-server client to use WebSocket frames over its private Unix socket, then add one concrete singleton account manager above that connection. Expose four global management routes and a Settings panel. Keep credentials in Codex-owned `codex_home`; Virtua Agent stores no account data.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, `ClientWebSocket`, `SocketsHttpHandler.ConnectCallback`, Unix domain sockets, SQLite, xUnit, React 19, TypeScript, Mantine, Vite, Docker Compose.

## Global Constraints

- Target pinned Codex CLI `0.147.0` and its documented app-server protocol.
- Keep the sidecar private: no host port, database mount, or endpoint secrets.
- Keep one global account and one active login. Do not add account-manager interfaces, factories, connection pools, or generic JSON-RPC infrastructure.
- Never persist or log email, plan, verification URL, user code, Codex login ID, tokens, or raw protocol payloads.
- Require JSON `{}` for login; use `DELETE` for cancel and logout. Do not add permissive CORS.
- Write failing focused tests before each production change.
- Preserve unrelated worktree changes. Commit only files belonging to each task.

---

### Task 1: Rename Endpoint `kind` to `type`

**Files:**
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/ModelEndpointModels.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/SqliteModelEndpointStore.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/ModelEndpointDispatcher.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/ModelEndpointsEndpoint.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/ModelEndpoints/IModelEndpointStore.cs` if signatures contain `Kind`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/Endpoints/ChatCompletionsEndpoint.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/Orchestration/PipelineExecutor.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/SqliteModelEndpointStoreTests.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/ModelEndpointsEndpointTests.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/ModelEndpointDispatcherTests.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/ChatCompletionsEndpointTests.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/PipelineExecutorTests.cs`
- Modify: `src/virtua-agent-app/src/types.ts`
- Modify: `src/virtua-agent-app/src/App.tsx`

- [ ] **Step 1: Add failing schema and API tests**

Add tests proving:

1. A database containing `kind` is migrated in place to `type` and retains each row's value.
2. A new database creates `type`, not `kind`.
3. Endpoint request/response JSON accepts and emits `type` and does not emit `kind`.
4. Both allowed values still dispatch correctly.

Use a real temporary SQLite file for migration coverage. Create the legacy table directly before constructing `SqliteModelEndpointStore`.

- [ ] **Step 2: Run focused tests and confirm failure**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.slnx --filter "FullyQualifiedName~SqliteModelEndpointStoreTests|FullyQualifiedName~ModelEndpointsEndpointTests|FullyQualifiedName~ModelEndpointDispatcherTests"
```

Expected: failures reference missing `type`, emitted `kind`, or absent migration.

- [ ] **Step 3: Rename the domain and public contract**

Use this concrete vocabulary:

```csharp
public static class ModelEndpointTypes
{
    public const string OpenAiCompatible = "openai_compatible";
    public const string CodexSubscription = "codex_subscription";
}
```

Rename model members from `Kind` to `Type`. Rename frontend `ModelEndpointKind` to `ModelEndpointType`, request/state properties to `type`, and visible label to **Type**. Do not accept a compatibility alias because this branch is unpublished.

- [ ] **Step 4: Implement the exact SQLite migration**

Inside one transaction, inspect `PRAGMA table_info(model_endpoints)`:

- `kind` exists and `type` does not: `ALTER TABLE model_endpoints RENAME COLUMN kind TO type`.
- neither exists: add `type TEXT NOT NULL DEFAULT 'openai_compatible'`.
- `type` exists: do nothing.

Update all reads, writes, and selects to use `type`.

- [ ] **Step 5: Update all callers and verify**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.slnx
npm run build --prefix src/virtua-agent-app
rg "ModelEndpointKind|ModelEndpointKinds|\.Kind\b|\bkind\b" src README.md docs -g "!docs/superpowers/specs/2026-08-10-codex-account-ui-design.md" -g "!docs/superpowers/plans/2026-08-10-codex-account-ui.md"
```

Expected: tests and build pass. Remaining `kind` matches are unrelated prose or deliberate migration-test SQL only; inspect each.

- [ ] **Step 6: Commit**

```powershell
git add src/virtua-agent-api src/virtua-agent-app
git commit -m "refactor: rename endpoint kind to type"
```

---

### Task 2: Replace Raw Unix JSONL with WebSocket Transport

**Files:**
- Create: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexAppServerConnection.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexAppServerClient.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexAppServerProtocol.cs`
- Create: `src/virtua-agent-api/VirtuaAgent.Tests/ScriptedUnixWebSocketServer.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/CodexAppServerClientTests.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/CodexSubscriptionClientTests.cs`

- [ ] **Step 1: Replace the test server contract with WebSocket frames**

Build one test-only scripted Unix-socket server. It should:

1. Accept the Unix socket.
2. Read the HTTP WebSocket upgrade request.
3. Compute `Sec-WebSocket-Accept` from the request key plus `258EAFA5-E914-47DA-95CA-C5AB0DC85B11` using SHA-1 and Base64.
4. Return HTTP `101 Switching Protocols`.
5. Wrap the stream with `WebSocket.CreateFromStream(..., isServer: true, ...)`.
6. Exchange scripted text frames and record requests.

Add failing tests for initialization, model discovery, turn streaming, interruption, server close, and a fragmented text response. Assert every connection closes.

- [ ] **Step 2: Run focused tests and confirm failure**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.slnx --filter "FullyQualifiedName~CodexAppServerClientTests|FullyQualifiedName~CodexSubscriptionClientTests"
```

Expected: current raw JSONL client cannot complete the WebSocket upgrade.

- [ ] **Step 3: Add the minimal internal connection**

`CodexAppServerConnection` owns only transport, JSON-RPC correlation, initialization, and disposal. Connect using .NET standard library:

```csharp
var handler = new SocketsHttpHandler
{
    ConnectCallback = async (_, cancellationToken) =>
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(
            new UnixDomainSocketEndPoint(options.SocketPath),
            cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    }
};

var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
var webSocket = new ClientWebSocket();
await webSocket.ConnectAsync(new Uri("ws://localhost/"), invoker, cancellationToken);
```

Send JSON as WebSocket text messages. Accumulate fragmented text frames until `EndOfMessage`. Reject binary frames. Treat premature close as a connection error. Dispose WebSocket, invoker/handler, and Unix socket deterministically.

- [ ] **Step 4: Keep authentication outside initialization**

`OpenAsync` performs transport plus `initialize` / `initialized` only. Add or retain a pipeline-specific open path that calls `account/read` and requires `account.type == "chatgpt"` before discovery or turns. Return a concise Settings-oriented unauthenticated error; do not expose raw responses or socket paths.

- [ ] **Step 5: Preserve turn semantics and verify**

Keep one connection per discovery or turn. Preserve existing streaming and fresh-token interrupt cleanup behavior.

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.slnx --filter "FullyQualifiedName~CodexAppServerClientTests|FullyQualifiedName~CodexSubscriptionClientTests|FullyQualifiedName~ModelEndpointDispatcherTests"
```

Expected: all focused tests pass over WebSocket frames.

- [ ] **Step 6: Commit**

```powershell
git add src/virtua-agent-api/VirtuaAgent.Api/Codex src/virtua-agent-api/VirtuaAgent.Tests
git commit -m "fix: use Codex websocket transport"
```

---

### Task 3: Add the Global Codex Account Manager

**Files:**
- Create: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexAccountModels.cs`
- Create: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexAccountManager.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexAppServerProtocol.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/Codex/CodexOptions.cs`
- Create: `src/virtua-agent-api/VirtuaAgent.Tests/CodexAccountManagerTests.cs`

- [ ] **Step 1: Write concrete manager tests**

Using `ScriptedUnixWebSocketServer`, add failing tests for:

- connected, disconnected, and unavailable reads;
- start returning verification URL and code;
- matching completion notification followed by `account/read`;
- duplicate and concurrent starts sharing exactly one `account/login/start`;
- cancellation sending `account/login/cancel` with the Codex login ID;
- timeout becoming `error` and cancelling the Codex login;
- logout;
- logout response failure followed by a fresh verified `account/read`;
- disposal closing an active login connection.

Configure a one-second login timeout in timeout tests; production default remains 600 seconds.

- [ ] **Step 2: Define the narrow state model**

```csharp
public static class CodexAccountStatuses
{
    public const string Unavailable = "unavailable";
    public const string Disconnected = "disconnected";
    public const string Connecting = "connecting";
    public const string Connected = "connected";
    public const string Error = "error";
}

public sealed record CodexAccountState(
    string Status,
    string? Email = null,
    string? PlanType = null,
    string? VerificationUrl = null,
    string? UserCode = null,
    string? Error = null);
```

Add only protocol records/parsers needed by `account/read`, `account/login/start`, `account/login/completed`, `account/login/cancel`, and `account/logout`. Add `LoginTimeoutSeconds = 600` to `CodexOptions` for deterministic timeout tests.

- [ ] **Step 3: Implement one concrete singleton manager**

Expose:

```csharp
Task<CodexAccountState> GetStateAsync(CancellationToken cancellationToken = default);
Task<CodexAccountState> StartLoginAsync(CancellationToken cancellationToken = default);
Task<CodexAccountState> CancelLoginAsync(CancellationToken cancellationToken = default);
Task<CodexAccountState> LogoutAsync(CancellationToken cancellationToken = default);
```

Implement `IAsyncDisposable`, but do not add `ICodexAccountManager`.

Use one lock only for state transitions. The first start reserves a shared `Task<CodexAccountState>` under the lock, releases the lock, then opens the socket. Concurrent starts await that task. Request cancellation cancels only that caller's wait, not the global attempt.

The attempt owner is the connection's sole reader. It retains verification data and Codex login ID in memory, waits for the matching completion notification, then performs `account/read`. Cancel and timeout signal the owner; the owner sends `account/login/cancel` with a fresh short timeout and closes the connection. Never hold the state lock during I/O.

- [ ] **Step 4: Implement state and failure rules**

- Connected start returns connected without opening a login.
- Connecting start returns the existing attempt.
- No ChatGPT account maps to disconnected.
- Connect/initialize failure maps to unavailable.
- Login failure or timeout maps to error with sanitized text.
- Logout first cancels an active attempt.
- After uncertain logout failure, perform fresh `account/read`; return verified state or unavailable, never stale connected metadata.
- Do not log state values or raw payloads.

- [ ] **Step 5: Run focused tests**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.slnx --filter "FullyQualifiedName~CodexAccountManagerTests"
```

Expected: all account state, concurrency, timeout, and cleanup tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/virtua-agent-api/VirtuaAgent.Api/Codex src/virtua-agent-api/VirtuaAgent.Tests
git commit -m "feat: manage Codex account login"
```

---

### Task 4: Expose Account Management Routes

**Files:**
- Create: `src/virtua-agent-api/VirtuaAgent.Api/Endpoints/CodexAccountEndpoint.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Api/Program.cs`
- Create: `src/virtua-agent-api/VirtuaAgent.Tests/CodexAccountEndpointTests.cs`
- Modify: `src/virtua-agent-api/VirtuaAgent.Tests/SwaggerRouteTests.cs`

- [ ] **Step 1: Write route tests first**

Cover exactly:

```text
GET    /v1/codex/account
POST   /v1/codex/account/login
DELETE /v1/codex/account/login
DELETE /v1/codex/account
```

Assert:

- GET returns `200` for all five representable states.
- POST login binds an empty request DTO from JSON `{}`.
- POST login without `application/json` returns `415`.
- unavailable command results return `503`.
- other command responses return their resulting state.
- response JSON uses `plan_type`, `verification_url`, and `user_code`.
- all four operations appear in Swagger.

- [ ] **Step 2: Run route tests and confirm failure**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.slnx --filter "FullyQualifiedName~CodexAccountEndpointTests|FullyQualifiedName~SwaggerRouteTests"
```

Expected: routes are absent.

- [ ] **Step 3: Map the minimal endpoints**

Add an empty `StartCodexLoginRequest` DTO so ASP.NET requires a JSON body. Map the concrete manager directly. Do not add an endpoint service layer.

Register once:

```csharp
builder.Services.AddSingleton<CodexAccountManager>();
```

Map state to `200` for GET. For commands, map resulting `unavailable` to `503`; return the resulting state otherwise. Keep route names and OpenAPI metadata consistent with existing endpoint modules.

- [ ] **Step 4: Verify routes and full backend**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.slnx
```

Expected: full backend suite passes.

- [ ] **Step 5: Commit**

```powershell
git add src/virtua-agent-api/VirtuaAgent.Api/Endpoints/CodexAccountEndpoint.cs src/virtua-agent-api/VirtuaAgent.Api/Program.cs src/virtua-agent-api/VirtuaAgent.Tests
git commit -m "feat: expose Codex account routes"
```

---

### Task 5: Add Codex Account Settings UI

**Files:**
- Create: `src/virtua-agent-app/src/CodexAccountPanel.tsx`
- Modify: `src/virtua-agent-app/src/types.ts`
- Modify: `src/virtua-agent-app/src/api.ts`
- Modify: `src/virtua-agent-app/src/App.tsx`
- Modify: `src/virtua-agent-app/src/styles.css`

- [ ] **Step 1: Add frontend contracts and API calls**

Define the exact state union and response:

```typescript
export type CodexAccountStatus =
  | "unavailable"
  | "disconnected"
  | "connecting"
  | "connected"
  | "error";

export interface CodexAccountState {
  status: CodexAccountStatus;
  email: string | null;
  plan_type: string | null;
  verification_url: string | null;
  user_code: string | null;
  error: string | null;
}
```

Add functions for GET state, POST login, DELETE login, and DELETE account. Login must explicitly send `Content-Type: application/json` with body `{}`. Reuse existing JSON/error handling.

- [ ] **Step 2: Build the panel with all five states**

`CodexAccountPanel` owns account loading, commands, polling, login dialog, and confirmation dialogs. Keep it independent from saved model endpoint state.

- `unavailable`: red status, sanitized message, Retry.
- `disconnected`: neutral status, Connect ChatGPT.
- `connecting`: modal with complete selectable code, Open login page, Copy code with visible copied feedback, Cancel.
- `connected`: green status, email, plan, Switch account, Sign out.
- `error`: error message, Retry.

Poll every two seconds only while `status === "connecting"`; clear the timer on state change and unmount. Refreshing while connecting must reopen the dialog from GET state. Close the dialog automatically on connected.

Use the browser clipboard API. Open the verification URL only after explicit user action with `target="_blank"` and `rel="noreferrer"`. Confirm sign out. Switch account confirms, logs out, then starts login.

- [ ] **Step 3: Place and style the section**

Insert **Codex account** after Pipeline protocol and before saved endpoints. Match existing Mantine controls and spacing. Add only local layout CSS needed for responsive account metadata and actions. Device code must be monospace, selectable, complete, and untruncated.

- [ ] **Step 4: Build frontend**

```powershell
npm run build --prefix src/virtua-agent-app
```

Expected: TypeScript and Vite production build pass.

- [ ] **Step 5: Verify rendered UI**

Run the app and use browser screenshots at `1440x900` and `390x844`. Exercise all five states with the API or a deterministic local scripted sidecar. Confirm:

- no horizontal scrolling or overlap;
- every action remains reachable;
- device code is complete and selectable;
- endpoint Type selector and conditional HTTP fields remain correct.

Save evidence under `.logs/`; do not commit screenshots.

- [ ] **Step 6: Commit**

```powershell
git add src/virtua-agent-app
git commit -m "feat: add Codex account settings"
```

---

### Task 6: Document and Verify the Docker Workflow

**Files:**
- Modify: `README.md`
- Modify: `docker-compose.yml` only if verification finds a real configuration defect
- Modify: `.gitignore` only if verification produces a new local artifact class

- [ ] **Step 1: Update deployment documentation**

Document the normal flow:

```powershell
docker compose up -d --build
```

Then configure the account at `/app/settings`. Explain Connect, device code, persistence through `codex_home`, Sign out, and Switch account. Keep container-shell Codex login as an emergency diagnostic path only. Retain the trusted-network/authenticating-reverse-proxy warning. Update endpoint JSON examples from `kind` to `type`.

- [ ] **Step 2: Run static verification**

```powershell
dotnet test src/virtua-agent-api/VirtuaAgent.slnx
npm run build --prefix src/virtua-agent-app
docker compose config
git diff --check
rg '"kind"|\bKind\b|ModelEndpointKinds|ModelEndpointKind' README.md docs src
```

Expected: tests/build/config/diff pass. Only deliberate legacy migration references remain.

- [ ] **Step 3: Run clean Docker end-to-end verification**

On a machine with Docker and a browser:

1. Back up any existing `codex_home` volume needed by the operator, then start with a clean test volume.
2. Run `docker compose up -d --build` without CLI login.
3. Open `/app/settings`, connect through the displayed URL/code, and confirm email and plan.
4. Confirm Codex model discovery and one non-streaming and one streaming pipeline call.
5. Restart both containers and confirm the account remains connected.
6. Sign out in Settings and confirm model discovery reports disconnected.
7. Use Switch account and complete a second login.
8. Inspect API logs, captured sidecar test logs, SQLite tables, and orchestration traces for account metadata, codes, URLs, login IDs, and tokens. None may be present.

Do not claim Docker acceptance complete if interactive device login was not performed.

- [ ] **Step 4: Review final scope**

```powershell
git status --short
git diff --stat
git log --oneline --decorate -6
```

Confirm no generic RPC layer, manager interface, host sidecar port, database credential storage, or unrelated refactor entered the branch.

- [ ] **Step 5: Commit documentation**

```powershell
git add README.md docker-compose.yml .gitignore
git commit -m "docs: explain Codex UI login"
```

Stage `docker-compose.yml` or `.gitignore` only if they changed for a verified reason.

