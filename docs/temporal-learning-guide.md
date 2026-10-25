# Temporal Learning Guide

A mini-course for a .NET developer who knows basic C# but is new to distributed systems. It follows this repository's code, so keep the source open while you read.

> **Honesty note.** This project was written in an environment with no .NET SDK or Docker daemon, so none of the code or scenarios here have been run yet. Statements about *Temporal's behavior* come from the official documentation and the SDK API reference; where I could not confirm something, the text says so. The commands in chapter 24 are how you check everything yourself.

Contents

1. [The problem](#1-the-problem-unreliable-long-running-processes)
2. [Distributed systems and distributed transactions](#2-distributed-systems-and-distributed-transactions)
3. [The Saga pattern](#3-the-saga-pattern)
4. [Orchestration vs choreography](#4-orchestration-vs-choreography)
5. [What Temporal is (and is not)](#5-what-temporal-is-and-is-not)
6. [Architecture and core components](#6-architecture-and-core-components)
7. [Workflows, Activities, Workers, Task Queues](#7-workflows-activities-workers-task-queues)
8. [History, durable execution, determinism and replay](#8-history-durable-execution-determinism-and-replay)
9. [Activity retries and error classification](#9-activity-retries-and-error-classification)
10. [Timeouts](#10-timeouts)
11. [Heartbeats](#11-heartbeats)
12. [Signals, Queries and Updates](#12-signals-queries-and-updates)
13. [Compensation and Saga recovery](#13-compensation-and-saga-recovery)
14. [Idempotency and ambiguous outcomes](#14-idempotency-and-ambiguous-outcomes)
15. [Cancellation and long-running workflows](#15-cancellation-and-long-running-workflows)
16. [Workflow retries vs activity retries](#16-workflow-retries-vs-activity-retries)
17. [Code walkthrough](#17-code-walkthrough)
18. [Failure scenarios](#18-failure-scenarios-and-what-to-look-for)
19. [Testing](#19-testing)
20. [Temporal vs RabbitMQ](#20-temporal-vs-rabbitmq)
21. [Versioning workflow code](#21-changing-workflow-code-safely)
22. [Production considerations](#22-production-considerations)
23. [Common mistakes](#23-common-mistakes)
24. [Running and verifying the project](#24-running-and-verifying-the-project)
25. [Next steps](#25-next-steps)

---

## 1. The problem: unreliable long-running processes

Placing an order is several steps: record the order, hold stock, take money, confirm. Each step may take seconds, fail, or need a human. A request/response handler that does all of this has no good answer when the process crashes after taking the money but before confirming: nothing remembers where it was.

Hand-built solutions (status columns, polling jobs, retry tables) work but you end up writing a workflow engine without meaning to. Temporal is that engine, as a product.

## 2. Distributed systems and distributed transactions

A *distributed system* is several independent processes that cooperate over a network. Networks drop and delay messages, and processes crash. Here: `InventoryService`, `PaymentService` and `OrderService` each own their data.

A normal database transaction gives all-or-nothing across rows in **one** database. It cannot span three services' separate stores. Two-phase commit can, but it needs every participant to support it, holds locks while waiting, and blocks if the coordinator dies, so most service architectures avoid it. The alternative is accepting that steps commit one at a time and designing for the in-between states: a Saga.

## 3. The Saga pattern

A Saga is a sequence of local transactions. If a later step fails, you run **compensating actions** for the earlier steps that succeeded.

| Idea | Meaning in this project |
| --- | --- |
| Forward action | `ReserveInventory`, `ChargePayment`, `ConfirmOrder` |
| Compensation | `ReleaseInventory`, `RefundPayment`, `CancelOrder` |
| Retry | run the *same* failed action again; nothing committed yet |
| Rollback | database undoes uncommitted work; does not exist across services |
| Compensation | a *new* business operation that offsets a committed one |

A refund is not "the charge never happened": the customer sees a charge and a refund, fees may remain, and the stock might have been sold meanwhile. Compensation can also fail, which is why chapter 13 exists.

## 4. Orchestration vs choreography

- **Choreography:** services react to each other's events; no central owner. Flexible, but the overall flow is spread across services and hard to follow.
- **Orchestration:** one coordinator tells each service what to do and tracks progress. This project uses orchestration; the coordinator is `OrderWorkflow`.

## 5. What Temporal is (and is not)

Temporal is a **durable execution** platform: you write a function (the workflow) and Temporal makes its progress survive crashes, restarts and long waits by recording what happened.

It is not a message broker, not a general job queue for fire-and-forget tasks (although it can do that), and not a database for your business data. Chapter 20 compares it with RabbitMQ.

## 6. Architecture and core components

```text
        start / signal / query                         poll for tasks
Client ───────────────────────▶ Temporal Server ◀────────────────────── Worker
(OrderService API)               (stores history,                    (OrderWorkflowWorker:
                                  schedules tasks,                    runs workflow code and
                                  holds timers)                       activity code)
```

- **Temporal Server** persists each workflow's *Event History*, runs timers and hands out tasks. In this repo it is the dev server from `docker-compose.yml` (in-memory database, local learning only).
- **Worker** is *your* process. It polls a Task Queue and runs your code. The server never executes your code.
- **Client** starts workflows and sends signals/queries/updates (`OrderEndpoints.cs`).

Starting a workflow only records a "workflow started" event and schedules work. The workflow code then runs on a worker, and each activity is scheduled as a separate task that some worker picks up.

## 7. Workflows, Activities, Workers, Task Queues

**Workflow** (`Workflows/OrderWorkflow.cs`): the orchestration code. It decides *what happens next*. It must be deterministic (chapter 8) and must not perform side effects.

**Activity** (`Activities/*.cs`): code that does side effects (HTTP calls, I/O). It may fail and is retried by Temporal. Our activities are thin wrappers around HTTP calls to the services. `ServiceCall.cs` translates HTTP outcomes into Temporal failure types.

**Worker** (`OrderWorker.cs`): registers workflows and activities and polls the Task Queue:

```csharp
new TemporalWorkerOptions(taskQueue)
    .AddAllActivities(instance)   // every [Activity] method on that object
    .AddWorkflow<OrderWorkflow>()
```

**Task Queue** (`TaskQueues.OrderSaga` = `"order-saga"`): a named queue that connects a client's start request to workers listening on that name. The worker and the `OrderService` client must use the same name (`Temporal:TaskQueue` in both `appsettings.json` files). If no worker polls the queue, workflows start but sit waiting; nothing fails.

Why activities are called through a lambda:

```csharp
await Workflow.ExecuteActivityAsync(
    (PaymentActivities a) => a.ChargePaymentAsync(command), options);
```

The lambda is never executed in the workflow. The SDK reads it to learn the activity name and arguments and schedules a command.

## 8. History, durable execution, determinism and replay

Every important thing that happens to a workflow is appended to its **Event History**: started, activity scheduled, activity completed (with result), timer fired, signal received. Open any workflow in the Temporal UI (<http://localhost:8233>) and read the History tab.

**Durable execution scenario**

```text
Worker starts, runs OrderWorkflow
  ReserveInventory scheduled → completed          (recorded in history)
  ChargePayment scheduled → completed             (recorded in history)
Worker process crashes before ConfirmOrder is scheduled
Worker (or another) starts again
  Server gives it the workflow task with the full history
  SDK re-runs OrderWorkflow.RunAsync from the top
  At each activity call it finds the recorded result and returns it instead of calling the service
  When it reaches ConfirmOrder there is no result yet, so it schedules it
Workflow continues from its logical position
```

This re-run is **replay**. The workflow's local variables (`completedSteps`, `pendingCompensations`) are rebuilt by replaying; they are not stored anywhere else.

What replay does *not* do: it does not call the payment service again for a completed `ChargePayment`. That result is in history. What it cannot protect you from: an activity that was *running* when the worker died. Its result was never recorded, so it will be retried (or time out and be retried). That is why activities must be idempotent (chapter 14).

**Determinism.** Replay only works if the code makes the same decisions each time given the same history. Therefore, inside workflow code do not use:

| Avoid | Use instead |
| --- | --- |
| `DateTime.UtcNow` | `Workflow.UtcNow` |
| `Guid.NewGuid()` | `Workflow.NewGuid()` |
| `new Random()` | `Workflow.Random` |
| `Task.Delay` | `Workflow.DelayAsync` |
| HTTP, database, file I/O | an Activity |
| `ILogger` of your own | `Workflow.Logger` (does not duplicate on replay) |
| `Task.Run`, `lock`, threads | the workflow's own scheduler and `Workflow.WaitConditionAsync` |

This is not "never use `Guid`": these are fine in an Activity, and pure computation with no external state is fine in a workflow. The rule is *same inputs, same decisions*. Look at `OrderWorkflow.cs`: it uses only its own fields, the input, and `Workflow.*` APIs.

If you change workflow code so a replay of an old history would make a different decision (reorder two activities, add one in the middle), the SDK reports a non-determinism error for those running executions. See chapter 21.

## 9. Activity retries and error classification

Activities are retried automatically according to a **Retry Policy** (`ActivityProfiles.cs`):

| Setting | Meaning | `Forward` profile |
| --- | --- | --- |
| `InitialInterval` | delay before first retry | 1 s |
| `BackoffCoefficient` | multiplier per retry | 2 |
| `MaximumInterval` | cap on the delay | 10 s |
| `MaximumAttempts` | 0 means unlimited | 5 |
| `NonRetryableErrorTypes` | error types that stop retrying | all business codes |

Classify failures:

| Failure | Retry? | How it is expressed |
| --- | --- | --- |
| Network error, timeout, 5xx | yes | ordinary exception (`HttpRequestException`) or `ApplicationFailureException` with type `ServiceTimeout` |
| Business rejection (`OutOfStock`, `PaymentDeclined`) | no: same answer next time | HTTP 422 → `ApplicationFailureException(errorType: code)`, code listed in `NonRetryableErrorTypes` |
| Unexpected 4xx (our bug) | no | `ApplicationFailureException(..., nonRetryable: true)` |

`ServiceCall.cs` implements this mapping; `ActivityTests` verifies it. Retrying "insufficient balance" ten times is not resilience, it is noise.

## 10. Timeouts

| Timeout | Measures | When it fires |
| --- | --- | --- |
| Schedule-To-Start | queue wait before a worker takes the task | no worker is polling, or workers are overloaded |
| Start-To-Close | one attempt | the attempt hangs or is too slow; triggers a retry |
| Schedule-To-Close | whole activity including all retries | overall deadline; ends retrying |
| Heartbeat | gap between heartbeats | a long activity's worker died or got stuck |

At least one of Start-To-Close or Schedule-To-Close is required. Our `Forward` profile uses 10 s per attempt and 1 min overall, so a dead dependency leads to compensation within about a minute instead of hanging.

## 11. Heartbeats

**The problem.** A 10-minute activity on a crashed worker would only be noticed at Start-To-Close, 10 minutes later. A heartbeat is a "still alive" signal that lets the server notice in seconds.

**The mechanism** (verified against the Temporal docs and .NET API reference):

- An activity calls `ActivityExecutionContext.Current.Heartbeat(details...)`. It is **not** a workflow concept: workflows do not heartbeat; workers do not heartbeat on their own behalf; and a workflow task timing out is a different mechanism. A heartbeat belongs to one running activity.
- `HeartbeatTimeout` in `ActivityOptions` is the maximum gap between heartbeats. If it passes with none, the server fails *that attempt* and, if the retry policy allows, schedules another. No heartbeat timeout means heartbeats are not used for liveness at all (`0s` means disabled).
- `Heartbeat` is **throttled in the SDK**. Per the docs, the interval is `0.8 × HeartbeatTimeout` (30 s default if no timeout, 60 s max); the SDK sends the *latest* heartbeat when the interval elapses. You can call it as often as you like. In `StockAuditActivities` the heartbeat timeout is 5 s, so the effective interval is about 4 s.
- **Heartbeat details** are payload data saved with the heartbeat. On the next attempt `ActivityExecutionContext.Current.Info.HeartbeatDetails` holds the *last details the server received*, and `Info.HeartbeatDetailAtAsync<T>(0)` converts one.
- **Cancellation is delivered through heartbeats.** Per the docs, "Activity Cancellations are delivered to Activities from the Temporal Service when they Heartbeat". An activity that never heartbeats will not learn it was cancelled; `HeartbeatTimeout` must be set for the .NET `CancellationToken` to fire on server-side cancellation. Throttling can delay the notification.

**Our example** (`StockAuditActivities.AuditStockAsync`, run by `StockAuditWorkflow`):

1. On entry, read the last recorded batch from `HeartbeatDetails` (clamped to `[0, TotalBatches]`).
2. For each remaining batch: wait (`Task.Delay(..., context.CancellationToken)`), then heartbeat the completed batch number.
3. Options: `HeartbeatTimeout = 5 s`, `StartToCloseTimeout = 10 min`, retry `InitialInterval` 1 s, `MaximumInterval` 5 s, unlimited attempts.

**What you must understand about it**

- **Lost progress is normal.** Because heartbeats are throttled and sent in the background, the progress the server holds can trail the real progress by up to about one throttle interval. After a crash the retry resumes from the last *received* batch and repeats some work. You will see this in the logs: "resuming from batch 6" after you saw "Audited batch 9". The simulated batches are idempotent, so this is safe. **Heartbeat details do not make a real external side effect safe to resume**; a payment call is not idempotent just because you recorded "done up to item 7".
- **Worker death vs worker shutdown are different.**
  - *Hard kill / crash / lost machine*: heartbeats stop; after `HeartbeatTimeout` the server fails the attempt and retries it on any worker. This is the case the heartbeat timeout exists for.
  - *Graceful shutdown (Ctrl+C)*: the worker cancels running activities. `ActivityExecutionContext.WorkerShutdownToken` fires first; `CancellationToken` fires after `TemporalWorkerOptions.GracefulShutdownTimeout`, which defaults to `TimeSpan.Zero` (immediately). This project does not change it, so Ctrl+C cancels the audit immediately via `context.CancellationToken`. I could not confirm from documentation exactly how the .NET SDK reports this to the server or how quickly the retry then starts; observe it in the UI (chapter 18) rather than assuming. To demonstrate the heartbeat *timeout* itself reliably, kill the process (`Stop-Process -Force`) instead of pressing Ctrl+C.
- **Cancellation.** If the workflow is cancelled, the server asks the activity to cancel via the next heartbeat; `context.CancellationToken` fires and `Task.Delay` throws, ending the activity. Default `CancellationType` is `TryCancel`: the workflow does not wait for the activity to finish stopping.
- **Heartbeating inside the logic matters.** A background timer that heartbeats regardless of progress would keep a hung activity "alive". We heartbeat only after real work.

**Tests** (`ActivityTests`, using `ActivityEnvironment`, no server): heartbeats are emitted per batch; a retry with recorded progress 3 resumes at batch 3 and heartbeats 4 and 5; out-of-range recorded progress is clamped; a cancel after two heartbeats stops the loop. What those tests **cannot** show: throttling, the server-side heartbeat timeout, real worker shutdown and the server's cancellation delivery. Those need a running server (chapter 18).

## 12. Signals, Queries and Updates

| | Signal | Query | Update |
| --- | --- | --- | --- |
| Changes workflow state | yes | **no** | yes |
| Caller gets a result | no | yes | yes |
| Can be rejected up front | no | n/a | yes, with a validator |
| In history | yes | no | yes (accepted ones) |
| Here | `ApproveOrder` | `GetState` | `RejectOrder` |

- **Signal:** "tell the workflow something". The API call returns when the server has *accepted the message*, not when the workflow acted on it. `POST /orders/{id}/approve` therefore returns `202` and says so; query to see the effect.
- **Query:** read-only snapshot of state (`OrderWorkflowState`). It must not change anything or call activities.
- **Update:** like a signal that returns a result and can be validated first. `RejectOrder` has a validator (`ValidateRejectOrder`) that refuses it unless the order is awaiting a decision; a refused update never enters history and the caller gets the error. `POST /orders/{id}/reject` returns `409` in that case.

All three differ from a normal HTTP endpoint: the target is a running workflow's durable state, delivered even if no worker is currently running (it is stored in history and processed when a worker returns).

**Waiting without a thread.** `Workflow.WaitConditionAsync(() => approval is not null, timeout)` suspends the workflow. No thread is blocked and the timer is a durable server-side timer; the worker can restart during the wait.

## 13. Compensation and Saga recovery

`OrderWorkflow` keeps a `Stack<Compensation>`. The rule for pushing:

| Situation | Push compensation? | Why |
| --- | --- | --- |
| step succeeded | yes | its effect exists |
| service explicitly rejected (`OutOfStock`, `PaymentDeclined`) | **no** | nothing happened; compensating would be wrong |
| outcome unknown (timeouts, retries exhausted, cancelled mid-call) | yes | it might have happened; compensation must be safe either way |

`CancelOrder` is pushed first, so it runs last. Compensations run in reverse order (`CompensateAsync`): `RefundPayment`, `ReleaseInventory`, `CancelOrder`.

The three paths, and what the tests assert (`OrderWorkflowSagaTests`):

```text
Success:        Create, Reserve, Charge, Confirm, Finalize, Complete
Payment fails:  Create, Reserve, Charge(x) → Release, Cancel
Confirm fails:  Create, Reserve, Charge, Confirm(x) → Refund, Release, Cancel
Out of stock:   Create, Reserve(x) → Cancel                (no Release: nothing reserved)
```

**Compensation can fail.** Compensation activities use the `Compensation` profile: unlimited retries for transient errors. Errors that can never succeed (`OrderAlreadyCompleted`, `ReservationFinalized`) are non-retryable. If one is hit, the workflow still runs the *remaining* compensations, then fails with `CompensationIncomplete` and its state shows `ManualInterventionRequired`. A human must resolve it; monitor for workflows in that state. Test: `Compensation_that_cannot_succeed_still_runs_independent_compensations_then_fails_the_workflow`.

**The point of no return.** After `ConfirmOrder`, `FinalizeInventory` and `CompleteOrder` run with the `PointOfNoReturn` profile: unlimited retries and shielded from workflow cancellation, because stopping halfway leaves a confirmed order unfinished.

Limits: this is not a rollback. The customer may see both charge and refund, and compensations are business operations that may need manual follow-up.

## 14. Idempotency and ambiguous outcomes

An activity may run more than once (retry after timeout, worker crash mid-call). An operation is **idempotent** if doing it twice has the same effect as once. Every service operation is keyed by the order id:

| Operation | Second call does |
| --- | --- |
| reserve inventory | returns OK, holds nothing extra |
| charge payment | returns the existing payment, charges nothing |
| refund | `AlreadyRefunded` |
| release | OK |

(Verified by `ServiceIdempotencyTests`, which run without any server.)

**The ambiguous charge.** The worker's HTTP call to the payment service times out. Did the money move? Unknown. The workflow:

1. Retries the charge. Because it is idempotent per order id, a retry that finds the earlier charge returns it; no double charge. (`PaymentTimeoutAfterCharge` scenario: the first reply is delayed past the 5 s HTTP timeout; the retry gets the existing receipt and the order completes.)
2. If retries are exhausted, the outcome is still unknown, so `RefundPayment` is registered anyway. A refund for a charge that exists refunds it; a refund when **no** charge exists records a *void*, so a delayed charge that arrives afterwards is refused (`PaymentVoided`). Without the void, a late charge could land after the refund and nobody would reverse it.
3. Inventory uses the same idea: a release that arrives before its reserve records a tombstone, and the late reserve is refused (`ReservationReleased`).

Real payment providers offer idempotency keys and lookups for the same purpose. Reconciliation (comparing your records to the provider's) is the safety net for what idempotency misses.

## 15. Cancellation and long-running workflows

A caller can cancel a workflow (`POST /orders/{id}/cancel`). Cancellation is a request: the workflow's cancellable awaits throw, and `OrderWorkflow.RunAsync` catches it (`TemporalException.IsCanceledException`), compensates, and rethrows so the workflow ends as *Canceled*. A business failure, by contrast, ends normally with `OrderOutcome.Cancelled`. Two different things: "a caller asked us to stop" vs "the order could not be fulfilled".

Compensation and point-of-no-return activities set `CancellationToken = CancellationToken.None` so a cancelled workflow can still undo its work. A consequence: a cancel request that arrives after the order is confirmed is effectively ignored and the order completes. That is deliberate.

For human approval, orders with total ≥ 500 wait in `AwaitingApproval` for the signal/update, up to `Orders:ApprovalTimeout` (default one day), then are cancelled. Workflows may run for months this way; the history grows with events, and very long-lived workflows eventually use *Continue-As-New* (not demonstrated here).

## 16. Workflow retries vs activity retries

Activities retry automatically; workflows do **not** by default (`WorkflowOptions.RetryPolicy` unset). Re-running a whole workflow after failure would redo business steps and is rarely what you want; a saga handles failure by compensating. This project sets no workflow retry policy on purpose. Use workflow retries only for workflows that are safe to rerun from scratch.

## 17. Code walkthrough

1. `POST /orders` (`OrderEndpoints.cs`): validates, builds `OrderWorkflowInput`, decides `RequiresApproval`, calls `client.StartWorkflowAsync(...)` with id `order-<orderId>` and returns `202`. The workflow id is deterministic, so a duplicate start with the same order id would conflict instead of creating a second saga.
2. The worker (`OrderWorker.cs`) connects, registers workflows and activities, polls `order-saga`.
3. `OrderWorkflow.RunAsync` → `ProcessOrderAsync`:
   - push `CancelOrder` compensation, run `CreateOrder`
   - if approval required: `WaitForApprovalAsync`
   - `ReserveInventoryAsync`, `ChargePaymentAsync` (each pushes its compensation per chapter 13)
   - `ConfirmOrder`, then `FinalizeInventory`, `CompleteOrder` (point of no return)
4. Failure handling: `catch` blocks in `RunAsync` choose between cancel-by-caller and business failure; both call `CompensateAsync`.
5. Each activity (`Activities/*.cs`) makes one HTTP call through `ServiceCall.SendAsync`, which maps the response to a Temporal failure.
6. The services (`InventoryStore`, `PaymentStore`, `OrderStore`) are in-memory simulations that enforce idempotency and state rules; scenarios are triggered by the `X-Demo-Scenario` header.
7. State for observers: the `GetState` query returns phase, completed steps, pending and completed compensations, the approval decision and failure reason.

## 18. Failure scenarios and what to look for

Use the commands in the README. For each, open the workflow in the UI and read History.

| Scenario | In History / `GetState` you should see |
| --- | --- |
| Success | activities completed in order; phase `Completed` |
| `PaymentDeclined` | `ChargePayment` failed once (non-retryable); then `ReleaseInventory`, `CancelOrder` |
| `ConfirmFails` | `RefundPayment`, `ReleaseInventory`, `CancelOrder` after the failed confirm |
| `TransientInventory` | `ReserveInventory` attempts 1 and 2 fail, 3 succeeds (attempt number visible on the pending activity / worker log) |
| Worker restart | stop worker while waiting for approval; approve; the signal sits in history; start worker; order completes |
| Compensation failure | refund outage on: `RefundPayment` retrying with growing attempt count; off: completes |
| Heartbeat | `POST /demo/stock-audits`; kill the worker process mid-run; after about the heartbeat timeout the activity retries; the new attempt logs `resuming from batch N` |

Expected but **unverified** details for the heartbeat scenario: the resumed batch number will likely be lower than the last "Audited batch" log line (throttling), and the exact gap before the retry depends on how the process was stopped (chapter 11).

## 19. Testing

- **Workflow tests** (`OrderWorkflowSagaTests`, `OrderWorkflowApprovalTests`): `WorkflowEnvironment.StartTimeSkippingAsync()` starts a test server (downloaded on first use, so this needs network access), a real `TemporalWorker` runs the real workflow with `FakeActivities` that record calls and inject failures. Time skipping makes retry backoffs and the approval timeout instant. Assertions check the **ordered list of activities called**.
- **Activity tests** (`ActivityTests`): `ActivityEnvironment` runs an activity without a server; HTTP is stubbed.
- **Store tests** (`ServiceIdempotencyTests`): plain unit tests of the in-memory services.
- **Integration**: the manual scenarios against Docker.

Not covered: replay testing with `WorkflowReplayer` against a saved history (the SDK supports it; add it when you start changing workflows).

## 20. Temporal vs RabbitMQ

A **broker** moves messages from producers to consumers and tracks delivery: ack means "the consumer processed this message". It knows nothing about a multi-step business process; if a consumer crashes midway through step 3 of 5, the broker redelivers the message and your code must figure out where it was.

**Temporal** stores the progress of the process itself and re-runs your orchestration deterministically; retries and timers are first-class.

| | Broker | Temporal |
| --- | --- | --- |
| Unit | message | workflow execution |
| Guarantees | delivery | progress and recovery of a process |
| Retries | per message, you design them | per activity, declarative |
| Timers / waiting for days | awkward | native |
| Fan-out, streaming, decoupled events | strong | not its job |

Temporal does not replace a broker where you need pub/sub or event streaming; a broker does not give you saga state. They combine well: an event arrives from a broker, a consumer starts a workflow. Neither is universally better.

## 21. Changing workflow code safely

Running workflows replay with *current* code. Adding an activity in the middle of `ProcessOrderAsync` would make old histories disagree with new code (non-determinism error). Options: let old executions finish before deploying, or use the SDK's patching API (`Workflow.Patched("id")` / `DeprecatePatch`) to branch old and new behavior, or worker versioning. Patching is **not demonstrated** in this repo. Test changes against recorded histories with `WorkflowReplayer`. Activities are not replayed, so changing an activity's internals is safe for running workflows, but changing its name or argument shape is not.

## 22. Production considerations

The Docker setup is a development server with in-memory storage. Production needs a real Temporal cluster (or Temporal Cloud) with persistence, TLS, namespaces, and observability; worker autoscaling; payload encryption for sensitive data; persistent business stores; alerting on failed workflows and on `ManualInterventionRequired`; a versioning strategy. This project has no authentication and in-memory state.

## 23. Common mistakes

- I/O, clocks, random or GUID generation inside workflow code.
- Non-idempotent activities.
- Retrying business rejections; or compensating steps that never happened.
- Running every compensation in a single `catch` without regard to what succeeded.
- Treating a signal's successful return as proof the work happened.
- Not setting `HeartbeatTimeout` on long activities (and so never receiving cancellation).
- Assuming heartbeat details are exact: they can lag.
- Changing running workflows' code without versioning.
- Putting large data in workflow inputs and results (payload size limits apply; the docs state 2 MB per payload, 4 MB per gRPC message).

## 24. Running and verifying the project

You need the .NET 10 SDK and Docker. From the repo root in PowerShell:

```powershell
dotnet restore
dotnet build
dotnet test                       # workflow tests download a test server on first run
docker compose config             # validates the compose file
docker compose up -d              # Temporal on :7233, UI on http://localhost:8233
```

Then, in four terminals: `dotnet run --project src/InventoryService`, `PaymentService`, `OrderService`, `OrderWorkflowWorker`. See the README for creating orders and triggering scenarios.

## 25. Next steps

Add a `WorkflowReplayer` test using a history exported from the UI; try `Workflow.Patched`; add Continue-As-New to a long-lived workflow; replace the in-memory services with a database and idempotency keys; read the Temporal docs on workflow versioning and worker deployment.
