# Codex Account UI Design

**Date:** 2026-08-10

## Goal

Make a Docker deployment fully usable from the Virtua Agent UI. An operator can connect a ChatGPT subscription, monitor device login, cancel it, sign out, and switch accounts without entering the container or running Codex CLI commands.

Codex credentials remain owned by Codex in the private `codex_home` volume. Virtua Agent never accepts, stores, or logs access or refresh tokens.

## Scope

- Add one global Codex account section to Settings.
- Support ChatGPT device-code login, cancellation, logout, and account switching.
- Show connected email and plan transiently.
- Correct the Codex `0.147.0` Unix-socket transport to use WebSocket framing.
- Rename model endpoint discriminator `kind` to `type` throughout the public interface, UI, domain models, and SQLite schema.
- Preserve the existing single-user, trusted-network deployment model.

## Out Of Scope

- Multiple Codex accounts or per-endpoint credentials.
- API-key, personal-token, or Amazon Bedrock authentication.
- Virtua Agent user authentication or authorization.
- Durable login-attempt recovery across API restarts.
- Exposing a Codex host port.
- A generic JSON-RPC framework or connection pool.

## External Protocol

The implementation targets the pinned Codex CLI version `0.147.0`.

- Unix-socket app-server traffic uses a standard WebSocket HTTP upgrade and WebSocket text frames, not raw JSONL.
- Device login starts with `account/login/start` and `type: "chatgptDeviceCode"`.
- The start response contains `loginId`, `verificationUrl`, and `userCode`.
- Completion arrives through `account/login/completed`.
- Cancellation uses `account/login/cancel` with the Codex `loginId`.
- Logout uses `account/logout`.
- Account status uses `account/read`.

Reference: [Codex app-server protocol at `rust-v0.147.0`](https://github.com/openai/codex/blob/rust-v0.147.0/codex-rs/app-server/README.md).

## Architecture

### Codex app-server connection

An internal connection module owns:

- Unix-domain socket creation.
- WebSocket upgrade and framing through .NET standard-library WebSocket support.
- JSON-RPC serialization and parsing.
- `initialize` / `initialized` handshake.
- Deterministic socket and WebSocket cleanup.

The module does not require authentication during initialization. Callers decide whether an unauthenticated account is valid for their operation.

This module replaces the current raw newline reader/writer. It remains small and specific to Codex app-server; it does not become a generic transport framework.

### Pipeline Codex client

The existing Codex pipeline client continues to own model discovery and turn execution. Before either operation, it reads the account and requires `account.type == "chatgpt"`.

Pipeline operations retain one connection per discovery or turn. No connection pooling is added.

### Codex account manager

A singleton account manager owns the temporary device-login lifecycle. It allows at most one active login and owns that login's WebSocket connection and reader.

Responsibilities:

- Read current account state.
- Start an idempotent device login.
- Retain verification URL and user code in memory while connecting.
- Wait for the matching completion notification.
- Cancel an active login.
- Time out an abandoned login after 10 minutes.
- Logout the current account.
- Return transient email and plan information.

The manager uses one state lock. It does not expose Codex `loginId` or an additional Virtua Agent attempt ID to callers. The project has one global account and one active attempt, so another identifier adds no useful isolation.

Completed credentials are not manager state. Codex persists them in `codex_home`; after an API restart, `account/read` reconstructs connected state. An unfinished attempt is intentionally lost and must be restarted.

### HTTP endpoints

Management routes are global rather than tied to a saved endpoint:

```text
GET    /v1/codex/account
POST   /v1/codex/account/login
DELETE /v1/codex/account/login
POST   /v1/codex/account/logout
```

`GET` returns the current state. `POST login` is idempotent: connected returns connected; connecting returns the existing attempt; disconnected or error starts a new attempt. `DELETE login` is idempotent when no attempt exists. Logout cancels an active attempt before signing out.

State response:

```json
{
  "status": "connected",
  "email": "user@example.com",
  "plan_type": "plus",
  "verification_url": null,
  "user_code": null,
  "error": null
}
```

`status` is exactly one of:

- `unavailable`: app-server cannot be reached or initialized.
- `disconnected`: no ChatGPT account is connected.
- `connecting`: device login is active.
- `connected`: `account/read` reports a ChatGPT account.
- `error`: the active attempt failed or timed out.

`GET` returns HTTP `200` for every representable state, including `unavailable` and `error`, because those are the resource's state. Login and logout command requests return HTTP `503` when the sidecar cannot accept the command. Other command results return the resulting state.

## Account Flow

1. Settings loads `GET /v1/codex/account`.
2. A disconnected account shows **Connect ChatGPT**.
3. Clicking Connect calls `POST /v1/codex/account/login`.
4. The account manager opens and initializes a dedicated app-server connection, starts `chatgptDeviceCode`, and returns `connecting` with URL and code.
5. The UI opens a dialog showing the code, **Copy code**, **Open login page**, and **Cancel**.
6. The UI polls `GET /v1/codex/account` every two seconds only while status is `connecting`.
7. The account manager receives `account/login/completed` on the login connection. On success it reads the account, closes the connection, and reports `connected`.
8. The dialog closes automatically and the account section shows email and plan.
9. Cancel signals the manager. The manager, as the connection's only reader, sends `account/login/cancel` with a fresh short timeout and closes the connection after acknowledgement or timeout.
10. Sign out requires confirmation and calls `POST /v1/codex/account/logout`.
11. Switch account confirms sign-out, logs out, and starts the same device-login flow.

A page refresh during login reads the manager's current `connecting` state and restores the dialog. An API restart during login loses the temporary state; the UI shows the state returned by a fresh `account/read` and permits a new attempt.

## Settings UI

The global **Codex account** section appears in Settings after Pipeline protocol and before saved endpoints.

States:

- `unavailable`: red status, concise sidecar error, Retry button.
- `disconnected`: neutral status and Connect ChatGPT button.
- `connecting`: device code, Open login page, Copy code, and Cancel.
- `connected`: green status, email, plan, Switch account, and Sign out.
- `error`: error message plus Retry.

Opening the verification URL is an explicit user action so browser popup blocking does not affect login. The URL opens in a new tab with safe external-link attributes. Copy code uses the browser clipboard and visible success feedback.

The dialog and section must fit desktop and mobile widths without horizontal scrolling. Buttons retain visible labels; the device code is selectable and does not truncate.

## Endpoint Type Rename

The endpoint discriminator becomes `type` everywhere:

```json
{
  "id": "codex-subscription",
  "name": "ChatGPT Codex",
  "type": "codex_subscription",
  "base_url": null,
  "has_api_key": false
}
```

Allowed values remain:

- `openai_compatible`
- `codex_subscription`

The UI label becomes **Type**. No temporary `kind` alias is added because the feature branch has not been merged or published.

SQLite migration handles both possible local starting states:

- Neither column exists: add `type` with default `openai_compatible`.
- `kind` exists and `type` does not: rename or migrate `kind` to `type` without changing values.
- `type` exists: make no schema change.

Existing endpoint rows remain OpenAI-compatible unless already marked as Codex subscription.

## Concurrency And Cleanup

- Only one login can be active.
- Duplicate Connect returns the same in-memory state.
- The login connection has one reader owner; cancel signals that owner instead of reading concurrently.
- Every operation closes its WebSocket and Unix socket on success, failure, cancellation, and timeout.
- Login timeout is 10 minutes.
- Cancellation and interrupt requests use fresh timeout tokens so an already-cancelled request token cannot prevent cleanup.

## Error Handling

- Sidecar connection or handshake failure maps to `unavailable` without exposing socket paths or raw payloads.
- Device-login errors are sanitized and shown in the dialog with Retry.
- A timed-out login becomes `error` with a login-timeout message.
- Logout failure keeps the last connected account visible and shows the operation error.
- Model discovery and pipeline execution retain the existing explicit unauthenticated error.
- Account operations do not create orchestration runs or trace events.
- Account email, plan, verification URL, user code, Codex login ID, and protocol payloads are excluded from application logs and SQLite.

## Security Model

The sidecar remains private:

- No host port.
- No Virtua Agent database mount.
- No upstream endpoint keys.
- Credentials remain in `codex_home`.
- The API receives account metadata and device-login values, never tokens.

Virtua Agent still has no application authentication. Anyone who can reach its management interface could connect or disconnect the global Codex account. This feature does not hide that risk; deployment documentation must continue to require a single-user trusted network or an authenticating reverse proxy.

## Verification

Automated coverage:

- WebSocket HTTP upgrade and text-frame JSON-RPC over a Unix socket.
- Initialization, model discovery, turn streaming, interruption, and cleanup over the corrected transport.
- Account states for connected, disconnected, and unavailable.
- Device login start and completion.
- Duplicate login start.
- Cancellation and timeout.
- Logout and switch-account sequence.
- Connection closure on every failure path.
- Endpoint `type` serialization, validation, persistence, and `kind`-to-`type` migration.
- Account route response shapes and command failure status codes.
- Frontend production build.

Visual verification at `1440x900` and `390x844`:

- Every account state renders without overlap or horizontal scrolling.
- Device code is complete and selectable.
- Login actions remain reachable on mobile.
- Endpoint Type selector and conditional HTTP fields still render correctly.

Docker end-to-end verification before merge:

1. Build and start clean Compose services without manual Codex CLI login.
2. Connect through Settings using the displayed URL and code.
3. Confirm email and plan appear and model discovery succeeds.
4. Restart both containers and confirm login survives through `codex_home`.
5. Sign out through Settings and confirm discovery reports disconnected.
6. Connect a second account through Switch account.
7. Confirm no account identifiers, codes, URLs, or tokens appear in API logs, Codex logs captured by the test, SQLite, or orchestration traces.

## Deployment Result

First deployment becomes:

```powershell
docker compose up -d --build
```

All account setup then occurs in `/app/settings`. Container-shell login remains an emergency diagnostic path, not the normal operator workflow.
