# Temporal .NET Order Saga

A small learning project that teaches [Temporal](https://temporal.io) through an order-processing Saga built with C#, ASP.NET Core and the Temporal .NET SDK (`Temporalio` 1.21.0).

> **Status:** the code and docs were written without access to a .NET SDK, so the solution has **not been compiled or tested yet**. See [Verification status](#verification-status) before relying on any command here.

## Overview

Three small services collaborate to process an order. Temporal coordinates them:

```text
Client ──HTTP──▶ OrderService ──start workflow──▶ Temporal Server
                                                      ▲   │ tasks
                                                      │   ▼
                                              OrderWorkflowWorker
                                              (workflow + activities)
                                                 │      │       │
                                          HTTP   ▼      ▼       ▼
                                        OrderService InventoryService PaymentService
```

Successful path:

```text
CreateOrder → [Approval] → ReserveInventory → ChargePayment → ConfirmOrder → FinalizeInventory → CompleteOrder
```

Failure paths compensate only what actually succeeded, in reverse order:

| Failure | Compensation |
| --- | --- |
| Out of stock | CancelOrder |
| Payment declined | ReleaseInventory → CancelOrder |
| Confirm fails after payment | RefundPayment → ReleaseInventory → CancelOrder |

Business data is in memory and the services are simulations. There is no real payment provider, database or authentication.

## Learning Objectives

Saga and compensation, durable execution, activity retries and timeouts, failure classification, idempotency, signals, queries, updates, heartbeats, determinism and replay, worker restarts, and testing workflows. The long-form explanation is in [docs/temporal-learning-guide.md](docs/temporal-learning-guide.md).

## Architecture

| Project | Responsibility |
| --- | --- |
| `OrderService` (port 5001) | Public API. Starts workflows, sends signals/updates/queries. Owns order state (`/internal/orders/*`). |
| `InventoryService` (port 5002) | Reserve, release and finalize stock reservations. |
| `PaymentService` (port 5003) | Charge and refund. Contains the simulated failure modes. |
| `OrderWorkflowWorker` | Hosts `OrderWorkflow`, `StockAuditWorkflow` and all activities. Activities call the services over HTTP. |
| `OrderSaga.Contracts` | DTOs, names and error codes shared by all projects. |

**Why a separate worker?** Hosting the worker inside a business service couples workflow deployment to that service and makes one service the owner of cross-service logic. A separate worker keeps orchestration in one place, and the services stay plain HTTP APIs that know nothing about Temporal, except that `OrderService` also acts as the client that starts workflows. The cost is one more process to run.

## Why Temporal?

A saga needs durable state ("what already happened?"), retries, timers and recovery after crashes. Temporal provides these, so the workflow code reads as ordinary sequential C# while the server persists its progress. See the [RabbitMQ comparison](#temporal-vs-rabbitmq).

## Project Structure

```text
src/
  OrderSaga.Contracts/      shared DTOs, error codes, workflow names
  OrderService/             public API + order state
  InventoryService/         reservations
  PaymentService/           charges and refunds, failure simulation
  OrderWorkflowWorker/
    Workflows/              OrderWorkflow, StockAuditWorkflow, ActivityProfiles
    Activities/             one class per service, plus heartbeat example
tests/OrderWorkflow.Tests/  workflow, activity tests (xUnit v3)
docs/temporal-learning-guide.md
docker-compose.yml          Temporal dev server only
```

## Prerequisites

- .NET 10 SDK (the projects target `net10.0`)
- Docker Desktop (only for the Temporal dev server)
- Optional: the [Temporal CLI](https://docs.temporal.io/cli) for inspecting workflows from a terminal

## Getting Started

All commands are PowerShell, run from the repository root.

```powershell
docker compose up -d                      # Temporal server on :7233, UI on http://localhost:8233
dotnet build
```

## Running with Docker Compose

`docker-compose.yml` runs only `temporalio/temporal:1.9.1` with `server start-dev --ip 0.0.0.0`. This is the development server with an in-memory SQLite database: **workflow history is lost when the container is removed or restarted**. It is for learning only; production uses a Temporal cluster (or Temporal Cloud) with a real database.

The .NET services run on the host, not in containers, to keep debugging simple.

## Starting the Services

Use four terminals:

```powershell
dotnet run --project src/InventoryService
dotnet run --project src/PaymentService
dotnet run --project src/OrderService
dotnet run --project src/OrderWorkflowWorker
```

Both `OrderService` and the worker retry the connection to Temporal for about a minute, so start order does not matter much.

## Creating an Order

```powershell
$body = '{"customerId":"c1","lines":[{"sku":"WIDGET","quantity":2,"unitPrice":10}]}'
$order = Invoke-RestMethod -Method Post -Uri http://localhost:5001/orders -ContentType 'application/json' -Body $body
$order
Invoke-RestMethod http://localhost:5001/orders/$($order.orderId)
Invoke-RestMethod http://localhost:5001/orders/$($order.orderId)/workflow
```

`POST /orders` returns `202 Accepted` as soon as the workflow is started. The order record appears when the first activity has run, so a very early `GET` can return 404.

## Inspecting the Workflow in Temporal UI

Open <http://localhost:8233>, select the workflow `order-<orderId>`, and open the **History** tab. Each activity appears as scheduled, started and completed events. The **Queries** feature can call `GetState`.

CLI equivalent (with the Temporal CLI installed):

```powershell
temporal workflow show --workflow-id order-<orderId>
```

## Workflow and Activity Design

- `OrderWorkflow` (`src/OrderWorkflowWorker/Workflows/OrderWorkflow.cs`) only orchestrates. It performs no I/O and all side effects are activities.
- Activities (`Activities/`) are thin HTTP calls that translate responses into Temporal failures (`ServiceCall.cs`).
- Activity options live together in `ActivityProfiles.cs`: `Forward`, `PointOfNoReturn`, `Compensation`, `HeartbeatingJob`.
- Every service operation is idempotent per order id, so retries are safe.

## Saga and Compensation

The workflow keeps a stack of compensations. A compensation is pushed **when its forward step succeeds**, or when its outcome is *unknown* (timeout, exhausted retries). It is not pushed when the service explicitly rejected the step (`OutOfStock`, `PaymentDeclined`), because nothing happened.

Ambiguous charge: if `ChargePayment` times out, the workflow cannot know whether money moved. It registers `RefundPayment`, which is idempotent: it refunds a captured charge, or if none exists, records a *void* so a late charge is refused (`PaymentVoided`). The charge itself is keyed by order id, so retrying it never double-charges. Inventory follows the same rule: a release that arrives before its reserve records a tombstone and the late reserve is refused (`ReservationReleased`).

Compensation can fail too. Compensation activities retry indefinitely for transient errors. Non-retryable errors (for example a refund against an already completed order) do not block the other compensations: the workflow runs the rest, then fails with `CompensationIncomplete` and phase `ManualInterventionRequired`.

Cancellation and failure are handled separately: a business failure ends with a normal `Cancelled` result, while an external cancel request compensates and then ends the workflow as *Canceled*. Compensation and post-confirmation activities use `CancellationToken.None` so a cancel request cannot interrupt them.

Limitations: compensation does not restore the exact earlier state (a refund is a new business fact), and a human may need to act when it fails.

## Failure Scenarios

Scenarios are selected with the `scenario` field of `POST /orders`, which the worker forwards to the service as the `X-Demo-Scenario` header.

| # | Scenario | How to trigger |
| --- | --- | --- |
| 1 | Success | default body |
| 2 | Payment failure | `"scenario":"PaymentDeclined"` |
| 3 | Failure after payment | `"scenario":"ConfirmFails"` |
| 4 | Transient failure then success | `"scenario":"TransientInventory"` (first two reserve attempts return 503; watch attempts 1-3 in the UI) |
| 5 | Worker restart | see below |
| 6 | Human approval | order total ≥ 500 |
| 7 | Compensation failure | see below |
| - | Ambiguous charge | `"scenario":"PaymentTimeoutAfterCharge"`: the charge is recorded, the reply is delayed past the 5 s HTTP timeout, the retry gets the existing receipt, and the order completes with a single charge |
| - | Heartbeat | `POST /demo/stock-audits` |

Example, scenario 3:

```powershell
$body = '{"customerId":"c1","scenario":"ConfirmFails","lines":[{"sku":"WIDGET","quantity":1,"unitPrice":10}]}'
$o = Invoke-RestMethod -Method Post -Uri http://localhost:5001/orders -ContentType 'application/json' -Body $body
Start-Sleep 3
Invoke-RestMethod http://localhost:5001/orders/$($o.orderId)/workflow | ConvertTo-Json -Depth 5
```

**Worker restart (5).** Create an order large enough to need approval (total ≥ 500, for example quantity 60 at 10), stop the worker, approve the order, then start the worker again:

```powershell
$body = '{"customerId":"c1","lines":[{"sku":"WIDGET","quantity":60,"unitPrice":10}]}'
$o = Invoke-RestMethod -Method Post -Uri http://localhost:5001/orders -ContentType 'application/json' -Body $body
# Ctrl+C the worker, then:
Invoke-RestMethod -Method Post -Uri http://localhost:5001/orders/$($o.orderId)/approve -ContentType 'application/json' -Body '{"approvedBy":"alice"}'
# start the worker again and watch the order complete
```

The signal is stored in history while no worker is running; the new worker replays history and continues. Activities that were *in flight* when a worker died are retried, which is why they must be idempotent.

**Compensation failure (7).** Enable a refund outage, run scenario 3, and watch the refund retry in the UI. Then disable the outage and the compensation completes:

```powershell
Invoke-RestMethod -Method Put -Uri http://localhost:5003/admin/refund-outage -ContentType 'application/json' -Body '{"enabled":true}'
# create a ConfirmFails order, observe RefundPayment retrying
Invoke-RestMethod -Method Put -Uri http://localhost:5003/admin/refund-outage -ContentType 'application/json' -Body '{"enabled":false}'
```

## Retry Policies and Timeouts

Defined in `ActivityProfiles.cs`:

| Profile | Timeouts | Retries | Why |
| --- | --- | --- | --- |
| Forward | StartToClose 10 s, ScheduleToClose 1 min | 5 attempts, 1 s ×2 up to 10 s | Bounded so the saga compensates instead of hanging. |
| PointOfNoReturn | StartToClose 10 s | unlimited, up to 30 s | The order is confirmed; it must finish. |
| Compensation | StartToClose 10 s | unlimited, up to 10 s | Undo must eventually happen. |
| HeartbeatingJob | StartToClose 10 min, Heartbeat 5 s | default | Detect a dead worker quickly. |

Business error codes (`OutOfStock`, `PaymentDeclined`, ...) are listed as non-retryable: retrying will not change the answer. Activity retries are automatic; the workflow itself has no retry policy because rerunning the whole saga is not what we want.

## Heartbeats

`StockAuditActivities.AuditStockAsync` (run by `StockAuditWorkflow`) processes batches and heartbeats the number of finished batches, with `HeartbeatTimeout = 5 s`.

```powershell
Invoke-RestMethod -Method Post -Uri 'http://localhost:5001/demo/stock-audits?batches=30'
# after ~10 s, kill the worker process (not Ctrl+C), then start it again
Get-Process OrderWorkflowWorker | Stop-Process -Force
dotnet run --project src/OrderWorkflowWorker
```

Expected (not yet observed): after about the heartbeat timeout the attempt is retried and the log shows `resuming from batch N`.

- The SDK throttles heartbeats (about 0.8 × the heartbeat timeout, so ~4 s here), so `N` can be lower than the last `Audited batch` line: recent progress can be lost and a few batches repeat. They are idempotent here; real side effects need their own idempotency.
- A hard kill is detected by the heartbeat timeout. Ctrl+C is a graceful shutdown: the activity is cancelled through its cancellation token (`GracefulShutdownTimeout` defaults to zero). How the server then retries it was not verified; watch the UI.
- Heartbeats are also how cancellation reaches an activity, which is why the timeout is set.

Details: [guide chapter 11](docs/temporal-learning-guide.md#11-heartbeats).

## Signals, Queries, and Updates

| Mechanism | In this project | Notes |
| --- | --- | --- |
| Signal `ApproveOrder` | `POST /orders/{id}/approve` | Fire-and-forget: delivery is not acceptance. Query to see the effect. |
| Query `GetState` | `GET /orders/{id}/workflow` | Read-only, must not change state. |
| Update `RejectOrder` | `POST /orders/{id}/reject` | Has a validator and returns a result or an error to the caller. |

Waiting for approval is `Workflow.WaitConditionAsync` with a timeout: a durable timer, no thread blocked.

## Determinism and Durable Execution

Workflow code is replayed from history after a restart, so it must make the same decisions every time. Do not call `DateTime.UtcNow`, `Guid.NewGuid()`, `Random`, HTTP, databases or files from workflow code. Use `Workflow.UtcNow`, `Workflow.NewGuid()`, `Workflow.Random` and activities. See [guide chapter 8](docs/temporal-learning-guide.md#8-history-durable-execution-determinism-and-replay).

## Testing

```powershell
dotnet test
```

- **Workflow tests** (`OrderWorkflowSagaTests`, `OrderWorkflowApprovalTests`) use `WorkflowEnvironment.StartTimeSkippingAsync()` with fake activities. They need the SDK to download the Temporal test server on first run, so they need internet access.
- **Activity tests** (`ActivityTests`) use `ActivityEnvironment` and a stub `HttpMessageHandler`; no server needed. They cover HTTP-to-failure mapping and the heartbeat example: progress heartbeats, resume from recorded details, clamping, and cancellation. They do not cover heartbeat throttling, the server-side heartbeat timeout or worker shutdown.
- **Store tests** (`ServiceIdempotencyTests`) check the idempotency and void/tombstone rules of the in-memory services.
- **Integration**: the scenarios above, run manually against Docker.

## Temporal vs RabbitMQ

A broker moves messages between processes and tracks delivery (ack/nack). Temporal tracks the *progress of a business process* and re-executes code deterministically. A broker answers "was the message delivered?"; Temporal answers "where is this order and what must happen next?". Temporal does not replace a broker for fan-out, streaming or decoupled event distribution; a broker does not give you durable timers, saga state or replay. Combining them is common: events in via a broker, a workflow started per event. Neither is universally better.

## Common Mistakes

- Doing I/O or reading the clock inside workflow code.
- Non-idempotent activities.
- Retrying business rejections.
- Compensating steps that never ran, or running every compensation blindly.
- Treating a signal as proof of completion.
- Changing workflow code with running executions and no patching.

## Limitations

In-memory state, so services lose data on restart. No authentication, no workflow versioning demo and no replay test. A cancel request that arrives after the order is confirmed is ignored by design (the final steps are shielded from cancellation). The services are simulations, not payment or stock integrations.

## Production Considerations

Use a production Temporal deployment with a persistent database, TLS and namespaces; persistent business stores; versioning (`Workflow.Patched`) or worker versioning for workflow changes; metrics and alerting on failing workflows and `ManualInterventionRequired`; payload encryption for sensitive data.

## Further Learning

[Temporal documentation](https://docs.temporal.io), [.NET SDK](https://github.com/temporalio/sdk-dotnet), and [docs/temporal-learning-guide.md](docs/temporal-learning-guide.md).

## Verification status

| Item | State |
| --- | --- |
| SDK API usage checked against SDK source/API reference and docs.temporal.io | Yes (read, not compiled) |
| `docker compose config` | **Passed** (file is valid; the image tag was not pulled) |
| `dotnet restore` / `build` / `test` | **Not run**: no .NET SDK and no NuGet access in the authoring environment |
| `docker compose up` and all end-to-end scenarios | **Not run**: no Docker daemon in the authoring environment |

Run `dotnet build` and `dotnet test` first and expect to fix a few compile errors. The most likely friction points are listed in the final report of the session that produced this repo: xUnit collection-expression overloads in `Assert.Equal`, the `xunit.v3` and `xunit.runner.visualstudio` pairing on the .NET 10 SDK, and `ActivityEnvironment.DefaultInfo` payload conversion in the resume test.
