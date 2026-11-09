# Size-Level Availability Processor

An ASP.NET Core (.NET 10) service that receives seller stock changes, enriches them with canonical product context, writes them to the Availability API and publishes a normalized `SizeAvailabilityUpdated` event.

Stock changes arrive one at a time (an inventory event) or in bulk (a CSV import processed asynchronously). **Both go through the same processing code**; only the way product context is fetched differs.

This file describes the solution. The assignment is in the PDF, and the original instructions for the provided mocks are in [README.md](README.md), unchanged. Diagrams are in [docs/architecture.md](docs/architecture.md).

## Quick start

Prerequisites: **.NET 10 SDK**, and **Docker** for the provided WireMock mocks.

```powershell
docker compose up -d                               # provided mocks on http://localhost:8080
dotnet run --project src/AvailabilityProcessor     # this service on http://localhost:5000
```

Send the sample event (PowerShell, from the repository root):

```powershell
curl.exe --request POST "http://localhost:5000/_dev/messages" `
  --header "Content-Type: application/json" `
  --data-binary "@wiremock\__files\sample-event.json"
```

Import the 10,000-row CSV and follow the job (use the `statusUrl` returned by the upload):

```powershell
curl.exe -F "file=@wiremock\__files\bulk-stock.csv" http://localhost:5000/v1/availability/imports
curl.exe http://localhost:5000/v1/availability/imports/<jobId>
```

The upload is `multipart/form-data` with the CSV in a field named `file`, which is what `curl -F` sends.

- Inspect the calls the service made: <http://localhost:8080/__admin/requests>.
- Ready-made requests: [AvailabilityProcessor.http](src/AvailabilityProcessor/AvailabilityProcessor.http).
- All settings (dependency URLs, retries, parallelism) are in [appsettings.json](src/AvailabilityProcessor/appsettings.json).
- The service calls the mocks at `http://127.0.0.1:8080`, not `localhost`. Compose publishes the port on 127.0.0.1 only, and on Windows `localhost` resolves to `::1` first, which adds about 2 s to every new connection.
- The service keeps its state in a SQLite file, `src/AvailabilityProcessor/availability.db`, created on the first run. The state survives a restart: the sample event sent again after a restart is skipped as a duplicate. Restarting WireMock clears its request journal, not this state.
  - To start from scratch, stop the service and delete `availability.db` (and `availability.db-wal` and `availability.db-shm`, if present).
  - To keep the state for one run only, start the service with `--Storage:ConnectionString "Data Source=:memory:"`.

Run the tests (Docker is not needed):

```powershell
dotnet test
```

## API

| Route | Purpose | Responses |
|---|---|---|
| `POST /_dev/messages` | Test-only ingress for a `SellerInventoryChanged` event. Calls the same handler a broker consumer would. | `200` applied or skipped, `400` invalid message, `422` permanent failure, `503` transient failure |
| `POST /v1/availability/imports` | CSV upload as `multipart/form-data`, in a field named `file` | `202` with `jobId` and `Location`, `400` no file, unreadable form body or bad header, `413` upload over the size limit, `415` wrong media type, `503` import queue full |
| `GET /v1/availability/imports/{jobId}` | Job status, counts and row-level results | `200`, `404` |
| `GET /_dev/stock/{sellerId}/{productId}/{sizeId}?countryCode=PT` | Latest applied version and quantity of a stock key | `200`, `404` |
| `GET /_dev/outbox` | Events that are waiting in the outbox to be published | `200` |

The status codes of `POST /_dev/messages` mirror what a broker consumer would do with the message: `200` ack, `400` dead-letter (poison message), `422` dead-letter or park, `503` do not ack, so it is redelivered later.

```json
{"status":"Applied","code":"Applied","message":"Availability updated and SizeAvailabilityUpdated published.","retryable":false,"outputEventId":"8d2370cd-f891-e548-ea00-f91e99597d1e","eventPublished":true}
```

## How it works

```text
POST /_dev/messages ─► SellerInventoryChangedHandler ─────────────┐
 (a broker consumer would call the same handler)                  │
                                                                  ▼
POST /v1/availability/imports ─► 202 + jobId          StockUpdateProcessor (shared)
        │                                               1. version check, under a per-key lock
        ▼                                               2. product context ◄─ single lookup (events)
  ImportQueue ─► ImportWorker ─► ImportJobRunner ─────►                    ◄─ country snapshot (bulk)
                 validate rows, 1 snapshot per          3. PUT availability
                 country, rows in parallel              4. record version + event (one transaction)
                                                        5. publish SizeAvailabilityUpdated
                                                           (the relay retries it from the outbox)
```

| Folder (`src/AvailabilityProcessor`) | Contents |
|---|---|
| `Domain` | `StockKey`, `StockUpdate`, validation rules shared by both inputs, `ProcessingResult` |
| `Processing` | `StockUpdateProcessor` (the shared flow), the interfaces of the version store and the outbox, the per-key lock and the outbox relay |
| `Persistence` | `SqliteStockStore`: the applied versions and the outbox, in one SQLite database |
| `Messaging` | `SellerInventoryChangedHandler`, the entry point for inbound messages |
| `Imports` | The CSV format (parsed with the CsvHelper library), import job state, queue, worker and runner |
| `Dependencies` | HTTP clients for the three dependencies and their resilience pipeline |
| `Api` | Minimal API endpoints |

## Design decisions

### Versions, duplicates and stale updates

- The stock key is `(sellerId, productId, sizeId, countryCode)`. A store keeps the latest **successfully applied** `sourceVersion` (and quantity) per key, in a SQLite database, so it survives a restart. Events and CSV rows share it.
- Lower version than the applied one: skipped as `StaleVersion`. Same version: skipped as `DuplicateVersion`, which covers redeliveries and replays even when they carry a new `eventId`. Higher version: applied.
- `eventId` is not used for de-duplication, because a replay may change it. It is kept as `causationId` on the output event for tracing.
- The version check, the availability write and the version update run under a **per-key lock** (one semaphore per stock key, kept in memory for the life of the process). An event and a bulk row for the same key cannot interleave, even while a job is running. Without the lock, v7 could reach the gateway after v8; a test reproduces that race.
- Only a successful write advances the version. A failed update leaves it unchanged, so a redelivery can still apply it.
- The same version with a different quantity is treated as a duplicate (the applied value wins) and logged as a warning.

### Unknown, inactive and disabled products

| Product Context answer | Outcome |
|---|---|
| `404` | `ProductContextNotFound` |
| `productStatus` other than `ACTIVE` | `ProductInactive` |
| `countryEnabled: false` | `CountryNotEnabled` |

All three are permanent failures. There is no availability write and no event, and the version is not recorded, so the update can still be applied if replayed after the product becomes sellable. The service cannot publish a normalized event without canonical context, and it should not expose availability for something that cannot be sold in that country. In production these messages go to a dead-letter or parking topic for replay. The trade-off is that the seller's quantity for an inactive product is not kept meanwhile.

### Retries

Each dependency has its own pipeline (`Microsoft.Extensions.Http.Resilience`, which is built on Polly). Strategies are listed from outer to inner:

| Strategy | Default |
|---|---|
| Concurrency limiter | 16 calls in flight per dependency, 1,000 queued |
| Total timeout | 60 s per logical call, retries included |
| Retry | 3 retries for 408, 429, 5xx, network errors and timeouts. Exponential backoff from 200 ms with jitter, capped at 5 s. **`Retry-After` is honoured.** |
| Circuit breaker | Opens when at least half of 20 or more calls in 30 s failed. Calls are then rejected for 5 s without being sent, and one trial call decides whether the circuit closes again. |
| Attempt timeout | 10 s |

`404` and other 4xx answers are final. When retries are exhausted, or the circuit is open, an event gets `503` (redeliver later) and a CSV row is reported as failed with `retryable: true`.

Retrying is safe on both write paths:

- `PUT` is idempotent and carries `sourceVersion`.
- The `POST` to the sink is also retried, because the output `eventId` is deterministic (a hash of the stock key and version). Consumers can drop duplicates by `eventId`.

Trade-offs:

- Honouring the mock's `Retry-After: 5` with 3 retries holds one message for about 15 s. With a real broker, a long `Retry-After` should send the message to a delayed retry topic instead of blocking the partition.
- The circuit breaker works per dependency, not per stock key. The fault inputs of the mocks count as failures of the whole dependency: about five failing events in a row open its circuit, and a healthy request sent in the next 5 s also gets `503`.

### Gateway write succeeded, but publishing failed

1. The version is recorded right after the successful write. Otherwise an older update could pass the version check later and overwrite the stock.
2. The output event is stored in the outbox in the same database transaction as the version (a transactional outbox). Either both are stored or neither is, so an applied version always has its event, even if the service crashes at that moment.
3. The service then publishes the event straight away, and removes it from the outbox once the sink has accepted it. Publishing is not cancelled when the caller goes away, because the stock has already changed.
4. If publishing still fails after its retries, the event stays in the outbox. `OutboxRelayService` publishes the waiting events every 10 s, with the **same** `eventId`. The result is `Applied` with `eventPublished: false`, and the message is acknowledged. A redelivery is a duplicate and does not write again.
5. Before it publishes an event, the relay takes the lock of that event's stock key, as the processor does. An event is therefore never published by both at the same moment.

Delivery is at-least-once. If the service crashes after the sink accepted an event and before the event left the outbox, the relay publishes it again after the restart, with the same `eventId`. Consumers de-duplicate by `eventId` and keep the highest `sourceVersion` per key, since a waiting v7 may arrive after v8.

### Bulk imports and partial failures

- **Rejected synchronously:**
  - a request that is not `multipart/form-data` (`415`);
  - an upload over the request size limit of about 30 MB (`413`);
  - a form body that cannot be parsed, for example multipart content without a boundary (`400`);
  - no file (`400`);
  - an empty file (`400`);
  - a header without the six required columns (`400`). Columns can be in any order, names are case-insensitive and extra columns are ignored.
  - a header with a duplicated column (`400`).
- **Parsing:** the CsvHelper library reads the file as RFC 4180 (quoted fields, escaped quotes, CRLF or LF, empty lines skipped). The header rules above and the row rules are this service's own.
- **Otherwise accepted with `202` straight away.** Rows are processed asynchronously:
  1. Each row is validated with the same rules as events.
  2. Its context comes from the snapshot.
  3. It goes through the same `StockUpdateProcessor`.
- **Counts:** `processedRows = succeededRows + skippedRows + failedRows`.
  - **Succeeded:** written. The event was published, or it is waiting in the outbox for the relay.
  - **Skipped:** stale and duplicate rows. These are not errors: the stock already holds an equal or newer version, and a stale row never causes a write or an event.
  - **Failed:** invalid row, unknown, inactive or disabled product, or a dependency failure after retries. `retryable` says whether submitting the row again can help.
- **`rowResults`** lists every row that was not applied. Each entry has the line number in the file (the header is line 1), the outcome, a code, a message and `retryable`. The list is capped at 1,000 entries (`rowResultsTruncated` says when the cap was hit), but the counts are always exact.
- **Job `status`:**
  - `Completed`: no failed rows.
  - `CompletedWithErrors`: partial failure.
  - `Failed`: the job itself was interrupted, for example by a shutdown.
- **Recovery:** upload the same file again, or only the failed rows. Rows that were already applied are skipped as duplicates, so this is safe. A test re-imports the 10,000-row fixture and checks that nothing is written twice.
- **Snapshot failures:** if a country's snapshot cannot be fetched, that country's rows fail as retryable (`ProductContextUnavailable`). A `404` snapshot means an unknown country, so its rows fail with `ProductContextNotFound`.

Abridged status of a mixed file (from the tests):

```json
{
  "jobId": "4b3b8f5e-8d5f-4b8e-9a43-3f2f1c9d2a10",
  "status": "CompletedWithErrors",
  "totalRows": 8, "processedRows": 8, "succeededRows": 1, "skippedRows": 2, "failedRows": 5,
  "rowResults": [
    { "line": 2, "outcome": "Skipped", "code": "StaleVersion", "message": "Version 7 of boutique-42/style-123/size-38/PT is older than the applied version 8.", "retryable": false },
    { "line": 5, "outcome": "Failed", "code": "ProductInactive", "message": "Product style-9999 is INACTIVE.", "retryable": false },
    { "line": 8, "outcome": "Failed", "code": "AvailabilityWriteFailed", "message": "availability-api: HTTP 503 Service Unavailable ...", "retryable": true }
  ],
  "rowResultsTruncated": false
}
```

### Concurrency and flow control

- **Imports:** one job runs at a time. Inside a job, up to 8 stock keys are processed in parallel (`BulkImport:MaxDegreeOfParallelism`). Rows of the same key are processed in file order.
- **Per-dependency limiter:** shared by events, imports and the outbox relay, so a burst never puts more than 16 concurrent calls on any dependency; extra calls wait in a bounded queue. A call keeps its permit while it waits for `Retry-After`, so 429s slow the callers down instead of piling up more requests.
- **Circuit breaker:** when a dependency keeps failing, the service stops calling it for 5 s at a time instead of retrying every update against it. A large import against a dependency that is down therefore ends quickly, with its rows reported as retryable. A test imports 200 rows while the Availability API answers `503`: the API receives fewer than 100 calls instead of 600.
- **Bounded import queue:** at most 10 jobs wait. When it is full, uploads get `503` instead of unbounded work being accepted.
- **In production:** consumer concurrency is bounded by partitions × instances, partitions are paused during 429 storms, and a rate limiter is tuned to each dependency's quota. A durable import job would pause while a circuit is open and resume afterwards, instead of failing its rows.

## Crashes, restarts and atomicity

The applied versions and the outbox are stored in SQLite, so they survive a crash or a restart. The import jobs and the key locks are in memory. This section covers three things: what a crash loses, which steps are atomic, and what production would add.

### What a crash or restart keeps and loses

| State | Stored in | After a crash or restart | Effect |
|---|---|---|---|
| Applied versions | SQLite | Kept | An older message that arrives after the restart is still skipped. |
| Outbox (events waiting to be published) | SQLite | Kept | The relay publishes the waiting events after the restart. |
| Running import job | Memory | Stops part-way | Some rows are applied and some are not. The job disappears, so its status endpoint returns `404`. Uploading the file again is safe: the rows that were applied are skipped. |
| Queued import jobs | Memory | Lost, with their file content | The files must be uploaded again. |
| Event being processed | Not stored | The HTTP request fails | Sending it again is safe. If the `PUT` had already happened, it is repeated with the same version. |

A graceful shutdown marks the running job `Failed`, but that status is also lost when the process exits.

SQLite runs with a write-ahead log that is synced at checkpoints (`synchronous = NORMAL`). A committed transaction survives a crash of the service. A power failure or a crash of the operating system can lose the most recent commits; the database stays consistent, and a message that is delivered again repeats the work.

### Atomicity

The applied version and its output event are stored in one database transaction. The Availability API is a different system, reached over HTTP, so no transaction can cover it together with the database. For that step, the service relies on two things:

- the order of the steps;
- idempotency (repeating a step has no extra effect).

As a result, a failure at any point either leaves nothing changed, or the state converges once the message is delivered again or the relay runs.

| Where processing stops | State left behind | How it converges |
|---|---|---|
| Before the `PUT`, or the `PUT` fails | Nothing changed; the version is not recorded | The caller gets `503` and retries; the change is applied later |
| The `PUT` reached the gateway but the response was lost (timeout) | Stock may have changed; the version is not recorded | The retried message repeats the `PUT` with the same version, which is idempotent |
| A crash after the `PUT`, before the version is recorded | Stock changed; no version and no event stored | The message was not acknowledged, so it is delivered again. The `PUT` is repeated with the same version, and the version and the event are then stored. A CSV row needs its file uploaded again. |
| A crash after the version is recorded, before publishing | Stock changed; version and event stored | The relay publishes the event after the restart |
| Publishing fails after retries | Stock changed; version and event stored | The relay publishes the event later, with the same `eventId` |

Inside the service:

- **One stock key at a time.** The key lock makes "check version, write, record version" indivisible for a key. Two updates for the same key cannot interleave within one process.
- **Recorded versions only move forward.** The SQL statement that stores a version only replaces a lower one (`WHERE excluded.source_version > applied_stock.source_version`).
- **Publishing is not cancelled when the caller disconnects.** Once the stock is written, the event stays in the outbox until it is published.
- **An upload is accepted or rejected as a whole.** If the queue is full, the job is removed before `503` is returned, so no orphan job is left behind.
- **Imports are atomic per row, not per file, by design.** The brief asks for partial-failure reporting, and one bad or stale row must not block the others. Running a file again is safe: rows that were already applied are skipped.
- **The job status is always consistent.** Job counters are updated atomically (`Interlocked`), and the status and row results are updated under a lock, so `GET` never shows a half-written state.

### What production would add

1. **A server database instead of the SQLite file.** SQLite is one file used by one instance. A database shared by all instances keeps the same conditional update and the same outbox table, and the version check then works across instances.
2. **A relay for several instances.** Outbox rows are leased, or read through change data capture, so that two instances do not publish the same event. An event that the sink keeps rejecting moves to a dead-letter table after a number of attempts, instead of being retried for ever.
3. **Commit after processing.** The Kafka consumer commits the offset only after the handler returns, so a message interrupted by a crash is delivered again. Combined with the idempotent `PUT` and the outbox, every change is eventually applied, and repeated work has no extra effect.
4. **Durable, resumable imports.** The uploaded file goes to blob storage and the job to a table. On start-up, the worker resumes jobs that were `Queued` or `Running`; the version check skips rows that were already applied.
5. **A version check in the Availability API.** If the API rejects writes older than the version it holds, lost state in this service can never cause an overwrite.
6. **Graceful shutdown.** On shutdown, the service stops accepting uploads, then finishes the current job or records how far it got before the process exits.

## Bulk context matching: time and space complexity

Let *n* be the CSV rows, *m* the snapshot objects of the countries in the file, *k* ≤ *n* the distinct `(productId, sizeId, countryCode)` keys the rows need, and *c* the number of countries.

1. Collect the needed keys in a hash set: **O(n)** time, **O(k)** space.
2. Fetch **one snapshot per country** (*c* requests instead of *n*). Stream it with `JsonSerializer.DeserializeAsyncEnumerable` and keep only the objects whose key is in the set: **O(m)** time, **O(k)** space. The 34.6 MB, 50,000-object snapshot is never held in memory; only the 10,000 contexts the file needs are.
3. Each row finds its context with a dictionary lookup: O(1) on average, **O(n)** in total.

**Total: O(n + m) time and O(n) memory** (the rows dominate). For comparison, scanning the snapshot for every row is O(n·m), about 5×10⁸ comparisons here, and looking up every row over HTTP costs *n* round trips.

As both collections grow, time grows linearly in each, and memory follows the upload, not the snapshot. Next steps at larger scale:

- For much bigger uploads, stream the CSV from blob storage in chunks instead of holding it in memory.
- For snapshots too large to download for every job, cache them (ETag or version), or keep a local product-context store fed by product-change events.
- If both sides become huge and unsorted, an external sort-merge join (O(n log n + m log m) with bounded memory) is the fallback.

## Production broker: Kafka

- **Ordering per key:** the partition key is the stock key, so every update of a key is consumed in order by a single consumer. This replaces the in-process lock across instances, and consumer groups scale out up to the number of partitions.
- **Durable, replayable log:** stock changes can be reprocessed after a bug fix, or state can be rebuilt.
- **Output topic:** keyed the same way and log-compacted, so new consumers can bootstrap the latest availability per key.
- **Connecting it to the code:**
  - A consumer (Confluent.Kafka or KafkaFlow) deserializes `SellerInventoryChanged` and calls `SellerInventoryChangedHandler.HandleAsync`, the method `POST /_dev/messages` calls today.
  - It maps the result: applied or skipped commits the offset; invalid or permanent failures go to a dead-letter topic and then commit; transient failures go to a delayed retry topic, so they do not block the partition.
  - `IEventPublisher` gets a Kafka producer implementation (`acks=all`, `enable.idempotence=true`, key = stock key), fed by the outbox relay.
- **Alternatives:** Azure Service Bus sessions (`SessionId` = stock key) or SQS FIFO (`MessageGroupId` = stock key) also give per-key ordering, if the platform already uses them. RabbitMQ needs extra work (a consistent-hash exchange or a single active consumer) to keep per-key order.

## Assumptions

- `sourceVersion` is a non-negative integer, monotonic per stock key, and comes from one sequence shared by events and CSV rows.
- `countryCode` must be two letters and is normalized to upper case, so `pt` and `PT` are the same stock stream. Only the format is checked, not the ISO list.
- `availableQuantity` is an integer ≥ 0.
- Inbound messages must have `eventType` `SellerInventoryChanged` and `schemaVersion` 1. `occurredAt` is required but not used for ordering; `sourceVersion` is.
- Output event fields:
  - `occurredAt` is the processing time.
  - `eventId` is deterministic.
  - `causationId` is the inbound `eventId`, or `import:{jobId}:line:{n}` for CSV rows.
  - `productContext` adds `category` and `color` to the fields shown in the brief.
- Single instance. The applied versions and the outbox are in a local SQLite file; the import jobs and the key locks are in memory. See [Crashes, restarts and atomicity](#crashes-restarts-and-atomicity).
- Uploads are read into memory and limited by Kestrel's default request size of about 30 MB; a larger upload gets `413`. The fixture is 375 KB.

## Tests

`dotnet test` runs 65 tests in a few seconds, without Docker.

[docs/test-traceability.md](docs/test-traceability.md) maps each requirement of the brief to the tests that check it. It also lists what no test proves yet, and the tests that I checked by breaking the code.

- **`StockUpdateProcessorTests`:**
  - a new version is applied;
  - a replay with a new `eventId` is a duplicate;
  - the stale case from the brief (v8 then v7);
  - versions are independent for each part of the key: seller, product, size and country;
  - unknown, inactive and disabled products;
  - a transient write failure;
  - a publish failure, after which the event waits in the outbox and the relay publishes it;
  - an event and a bulk row racing on the same key. This test fails if the per-key lock is removed.
- **`SqliteStockStoreTests`:** the SQL of the store, against a SQLite database in memory. A version and its event are stored together, the event leaves the outbox once published, and a version that is not higher never replaces the stored one.
- **`StockCsvTests`:** CRLF line endings, any column order, quoted fields, header errors, row errors (a row with broken quoting among them), and the provided fixture.
- **`DevMessagesEndpointTests`, `BulkImportEndpointTests` and `EventPublisherTests`** run the real application (`WebApplicationFactory`) with the real resilience pipeline. Only the HTTP dependencies are replaced, by an in-memory copy of the WireMock mappings ([FakeDependencies.cs](tests/AvailabilityProcessor.Tests/Support/FakeDependencies.cs)), and they use the real fixtures. Each application gets its own SQLite database, in memory. They cover:
  - the sample event;
  - every fault input from the mock reference table;
  - `Retry-After`;
  - the 10,000-row fixture, which needs one snapshot request and no per-row lookups, and is a no-op when imported again;
  - a mixed file with partial failures, where the stale row and the duplicate row send nothing downstream;
  - an upload that gets `202` while every dependency call is held, so before any row is processed;
  - a file with two countries, which needs one snapshot request per country;
  - an event that arrives while an import job is writing the same key. The event waits, and the older row never overwrites it;
  - uploads that cannot be read: a request that is not multipart gets `415`, a form body that cannot be parsed gets `400`, and a body over the size limit gets `413`. Kestrel enforces that limit, so this one test starts the application on Kestrel, with the limit lowered to 1 KB;
  - the circuit breaker: it opens after repeated failures, the next call is rejected without reaching the dependency, and it closes again once the dependency recovers;
  - an import of 200 rows against a failing Availability API, which stops calling it once the circuit opens;
  - a restart. Two instances of the application use the same database file, one after the other: the second one still skips an older event, and it publishes an event that the first one left in the outbox.
- **Event sink faults:** they are keyed on the published `eventId` (`fault-429` and `fault-503`). The service derives event IDs from the key and version, so the full flow never produces them; the publisher adapter is tested with those IDs directly.

## What I would do next

- Move the state from the SQLite file to a database shared by all instances, and store the import jobs and their row results there too.
- Have the Availability API enforce `sourceVersion` (reject older writes) as the last line of defence.
- Add Kafka consumer and producer adapters, with dead-letter and retry topics.
- Add OpenTelemetry metrics and tracing (using `causationId`), and dependency health checks.
- For uploads: stream them to blob storage, paginate row results, add job retention and authentication, and switch off the `/_dev` routes.
