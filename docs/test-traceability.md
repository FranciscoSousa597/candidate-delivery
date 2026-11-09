# Requirement-to-test traceability

This document maps each requirement of the brief to the tests that check it. It also lists:

- the decisions the brief leaves open, and the tests that pin them;
- the requirements and behaviours that no test proves yet.

I wrote the tests after the code, from my own reading of the brief. If that reading is wrong somewhere, the code and its test are wrong together and the test still passes. The tables below make the reading checkable against the brief, one requirement at a time.

The suite has 36 test methods and 56 test cases. `dotnet test` runs them without Docker.

## Summary

| | Count |
|---|---|
| Requirements taken from the brief | 36 |
| Covered by a test | 33 |
| Partly covered | 1 |
| Not covered | 0 |
| Documented, not a matter for a test | 2 |
| Decisions pinned by tests | 13 |
| Gaps closed by a test | 6 |
| Open gaps | 5 |

## How to read the tables

- **Requirement IDs** follow the sections of the brief:
  - `R3.x`: single inventory event;
  - `R4.x`: bulk import;
  - `R5.x`: shared rules.
- **Test IDs** such as `Dev-1` or `Proc-3` are resolved in the [test index](#5-test-index).
- **Coverage** has four levels:
  - *Covered*: a test asserts the behaviour.
  - *Partly*: a test asserts it, but not for every case the requirement names. The missing part is a gap in [section 3](#3-gaps).
  - *Not covered*: no test asserts it.
  - *Documented*: the brief asks for an explanation or a design property. See [SOLUTION.md](../SOLUTION.md).

## 1. What the brief requires

### 1.1 Single inventory event (brief section 3)

| ID | Requirement | Tests | Coverage |
|---|---|---|---|
| R3.1 | `POST /_dev/messages` accepts the `SellerInventoryChanged` contract. | Dev-1 | Covered. The test sends the provided `sample-event.json`. |
| R3.2 | The endpoint calls the same processing path as a real consumer adapter. | None | Documented (G11). |
| R3.3 | The event is validated, and `countryCode` is a two-letter code. | Dev-4, Dev-5, Csv-4 | Covered. Csv-4 checks the same rules on CSV rows. |
| R3.4 | A repeated delivery, or a replay with a new `eventId`, has no second effect. | Dev-2, Proc-2 | Covered. |
| R3.5 | An older version that arrives late does not replace a newer one. | Dev-3, Proc-3 | Covered. |
| R3.6 | Product context is read with `GET /v1/products/{productId}/sizes/{sizeId}?countryCode=`. | Dev-1 | Covered. |
| R3.7 | Unknown, inactive and country-disabled products have a defined behaviour. | Dev-6, Proc-5, Bulk-2 | Covered. The behaviour itself is decision D3. |
| R3.8 | Stock is written with `PUT /v1/availability/{sellerId}/{productId}/{sizeId}?countryCode=`, with `availableQuantity` and `sourceVersion`. | Dev-1 | Covered. |
| R3.9 | A `SizeAvailabilityUpdated` event is posted to `/mock-broker/v1/events`. It identifies the seller, product, size, country, quantity and source version, and it includes the canonical context. | Dev-1, Proc-1, Pub-2 | Covered. |
| R3.10 | The inbound event is not sent to the sink. | Dev-1 | Covered. Exactly one POST reaches the sink, and its `eventType` is `SizeAvailabilityUpdated`. |

### 1.2 Bulk import (brief section 4)

| ID | Requirement | Tests | Coverage |
|---|---|---|---|
| R4.1 | CSV rows go through the same flow as single events. | Bulk-1, Bulk-2 | Covered. Rows produce the same calls and outcome codes as events, and both paths share the version state. |
| R4.2 | The CSV has the columns `sellerId,productId,sizeId,countryCode,availableQuantity,sourceVersion`. | Csv-1, Csv-2, Bulk-1 | Covered. The tests use the provided `bulk-stock.csv`. |
| R4.3 | Malformed requests and missing headers are rejected synchronously. | Bulk-3, Bulk-4, Bulk-9, Bulk-10, Bulk-11, Csv-3 | Covered. Bulk-9 and Bulk-10 send form bodies that cannot be parsed. Bulk-11 sends a body over the request size limit. |
| R4.4 | The upload returns `202 Accepted` and a `jobId`. | Bulk-1, Bulk-2 | Covered. The tests assert `202`, the `Location` header and `statusUrl`. Bulk-1 also asserts that the body has a `jobId`, that `statusUrl` points to that id, and that the status endpoint returns the same id. |
| R4.5 | The upload does not wait for all rows to finish. | Bulk-6 | Covered. The test holds every call to the dependencies. The upload still answers `202`, and the job status shows 10,000 rows, none of them processed, and no write. |
| R4.6 | The status endpoint returns the job state, the total, processed, succeeded and failed row counts, and row-level errors. | Bulk-1, Bulk-2, Bulk-6 | Covered. Bulk-6 reads the status while the job is still `Queued` or `Running`. |
| R4.7 | Partial failures are reported. | Bulk-2 | Covered. The format is decision D5. |
| R4.8 | The service fetches one snapshot per distinct country in the file, and makes no single-item request per row. | Bulk-1, Bulk-2, Bulk-7 | Covered. Bulk-1 and Bulk-2 assert one snapshot request and no per-row lookup for a file with one country. Bulk-7 uploads rows for `PT` and `ES` and asserts one snapshot request per country. The mock has no snapshot for `ES`, so those rows fail with `ProductContextNotFound`. |
| R4.9 | The snapshot order is unspecified, so rows are matched by key. | Bulk-1 | Covered. The provided file and the provided snapshot are in different orders, and all 10,000 rows find their context among 50,000. |
| R4.10 | `sellerId` is part of the stock-version key, but not of the context key. | Proc-4, Bulk-1 | Covered. In the seller case of Proc-4, two sellers use the same context and keep separate versions. |
| R4.11 | Time and space complexity are explained. | None | Documented. |

### 1.3 Shared rules (brief section 5)

| ID | Requirement | Tests | Coverage |
|---|---|---|---|
| R5.1 | Versions are compared only within the same (`sellerId`, `productId`, `sizeId`, `countryCode`). | Proc-4 | Covered. The test has four cases. Each case changes one part of the key: the seller, the product, the size or the country. |
| R5.2 | Version checks apply across both input paths: a bulk import does not override newer stock. | Bulk-2 | Covered. An event sets version 8, then a CSV row with version 7 is skipped. |
| R5.3 | A stale row causes no availability write and no event. | Proc-3, Dev-3, Bulk-2 | Covered. Proc-3 asserts that there is no second write and no second event. Bulk-2 asserts the same for an import: after a stale row and a duplicate row, the key still has one write and one event, both with version 8. |
| R5.4 | The rule also holds when a newer update is processed while the bulk job is running. | Proc-8, Bulk-8 | Covered. Proc-8 proves it at processor level with a controlled race. Bulk-8 proves it through a real import job: it holds the write of a row with version 7, sends an event with version 8 for the same key, and asserts that the event waits. The writes then arrive as version 7 and then version 8, and the stored stock has version 8. |
| R5.5 | The example in the brief: the key is at version 8 with quantity 3, then version 7 with quantity 10 arrives, and the quantity stays 3. | Proc-3, Dev-3, Bulk-2 | Covered at processor, event and import level. |
| R5.6 | A stale row does not reject the upload. | Bulk-2 | Covered. |
| R5.7 | The solution documents whether stale rows are reported as skipped or as failed. | Bulk-2 | Covered. They are reported as `Skipped` (decision D4). |
| R5.8 | The context lookup can return `404`. | Dev-6, Proc-5 | Covered. |
| R5.9 | The context lookup can return `429` with `Retry-After`. | Dev-7, Dev-8 | Covered. |
| R5.10 | The context lookup can return `503`. | Dev-7 | Covered. |
| R5.11 | The Availability API can fail transiently. | Dev-7, Proc-6, Bulk-2 | Covered. |
| R5.12 | The event sink can fail transiently. | Pub-1, Dev-9, Proc-7 | Covered. |
| R5.13 | Retry and idempotency behaviour is described. | Dev-7, Dev-8, Proc-2, Proc-6, Proc-9 | Covered, and described in SOLUTION.md. |
| R5.14 | The case where the write succeeds and publishing fails is described. | Dev-9, Proc-7 | Covered, and described in SOLUTION.md. |
| R5.15 | Concurrency and flow control are described. The brief states that no load test is required. | Proc-8, Bulk-8 | Partly (G8). The per-key lock is tested. The import queue limit, the row parallelism and the per-dependency limiter are only described. |

## 2. Decisions the brief leaves open

The brief leaves these points open, or asks the candidate to decide and document them. The tests below check my choice, not a requirement. If a reviewer prefers another choice, these are the tests that change.

| ID | Decision | What the brief says | Tests |
|---|---|---|---|
| D1 | `POST /_dev/messages` maps the result to an HTTP status: `200` applied or skipped, `400` invalid message, `422` cannot be applied, `503` retry later. | Nothing. The endpoint is a development stand-in for a consumer. | Dev-1 to Dev-7, Dev-9, Bulk-8 |
| D2 | Validation rules: the quantity is 0 or more, the version is 0 or more, `eventType` must be `SellerInventoryChanged`, a lower-case country code is accepted and upper-cased. Every error is reported, not only the first. | The event must be validated, and the country code has two letters. | Dev-4, Csv-1, Csv-4 |
| D3 | Unknown, inactive and country-disabled products are permanent failures: no write, no event, not retryable. | "Decide and document". | Dev-6, Proc-5, Bulk-2, Bulk-7 |
| D4 | Duplicate and stale updates are `Skipped`, not failed. In a job they count in `skippedRows`, and the job still ends as `Completed`. | "Document whether stale rows are reported as skipped or failed". | Dev-2, Dev-3, Proc-2, Proc-3, Bulk-1, Bulk-2 |
| D5 | Partial failures: the job ends as `CompletedWithErrors`. `rowResults` gives the line, outcome, code and retryable flag of each row that was not applied. The list is capped at 1,000 entries and `rowResultsTruncated` signals the cap. The counts are always exact. | "Define and document how partial failures are reported". | Bulk-1, Bulk-2, Bulk-7 |
| D6 | The version is recorded after a successful write and before publishing. A failed publish leaves the update applied, and the event waits in an in-memory outbox for a later relay. | The case must be described. | Dev-9, Proc-6, Proc-7 |
| D7 | The output `eventId` is deterministic per stock key and version. `causationId` carries the inbound `eventId`. | Nothing. | Dev-1, Dev-9, Proc-9 |
| D8 | Retry policy: only transient failures are retried, `Retry-After` is honoured, and the number of attempts is bounded. | Retry behaviour must be described. | Dev-7, Dev-8, Pub-1 |
| D9 | Uploads are accepted as `multipart/form-data` (field `file`) or as a `text/csv` body. A missing file, a form body that cannot be parsed or a bad header gets `400`. A body over the request size limit gets `413`. Another media type gets `415`. | Malformed requests are rejected synchronously. The format and the status codes are not given. | Bulk-3, Bulk-4, Bulk-9, Bulk-10, Bulk-11 |
| D10 | CSV parsing: columns are matched by header name in any order, CRLF and blank lines are accepted, quoted fields follow RFC 4180, and a repeated column name rejects the file. | The column names only. | Csv-1, Csv-3, Csv-5 |
| D11 | An invalid row fails on its own and keeps its line number. It does not reject the file. | The status must include row-level errors. | Csv-4, Bulk-2 |
| D12 | An unknown job id gets `404`. | Nothing. | Bulk-5 |
| D13 | Two helper routes exist for inspection: `GET /_dev/stock/...` and `GET /_dev/outbox`. | Nothing. | Used by Dev-3, Dev-7, Dev-9, Bulk-2 and Bulk-8 |

## 3. Gaps

### 3.1 Open gaps

No test proves the points below. Each row names the test that would close the gap.

| ID | What is not proven | Relates to | Test that would close it | Size |
|---|---|---|---|---|
| G7 | Snapshot failures. When the snapshot request keeps failing, the rows of that country fail as `ProductContextUnavailable` and are retryable. | R5.9 and R5.10, applied to the snapshot. The mock has no fault input for the snapshot. | Make the fake snapshot route answer `503`. Assert the row results and the `CompletedWithErrors` status. | Small |
| G8 | Flow control: a full import queue answers `503`, at most 8 stock keys are processed in parallel, and each dependency has a concurrency limit. | R5.15. The brief asks for a description only. | Fill the queue with held jobs and assert that the next upload gets `503`. Add a counter of concurrent calls to the fake. | Medium |
| G9 | The job state `Failed`, on shutdown or on an unexpected error. | D5 | Stop the worker during a job and assert the status. | Medium |
| G10 | The timer loop of `OutboxRelayService`. The tests call `PendingEventOutbox.FlushAsync` directly. | D6 | Run the application with a short `Outbox:RelayInterval` and wait for the event to reach the sink. | Small |
| G11 | A real consumer uses the same processing path. | R3.2 | Not testable as behaviour. `DevEndpoints` only calls `SellerInventoryChangedHandler` and maps its result to a status code. A second adapter that calls the same handler would demonstrate it. | Not applicable |

### 3.2 Closed gaps

The first version of this document listed six more gaps, each tied to a requirement of the brief. Each one now has a test. I checked these tests by breaking the code: see [section 4.4](#44-tests-checked-by-breaking-the-code).

| ID | What was not proven | Relates to | Test that proves it |
|---|---|---|---|
| G1 | The `202` body contains a `jobId`. | R4.4 | Bulk-1, with new assertions |
| G2 | The upload returns before the rows are processed. | R4.5 | Bulk-6, a new test |
| G3 | One snapshot request per country when a file has several countries. | R4.8 | Bulk-7, a new test |
| G4 | Versions are independent for a different product, size or country. | R5.1 | Proc-4, now with one case for each part of the key |
| G5 | A stale row inside an import sends no PUT and no POST. | R5.3 | Bulk-2, with new assertions |
| G6 | A newer event that arrives while an import job is running, end to end. | R5.4 | Bulk-8, a new test |

## 4. How close the tests are to the real mocks

### 4.1 What runs for real

- **Endpoint tests** (`Dev`, `Bulk` and `Pub`) start the real application with `WebApplicationFactory`. Routing, validation, the background worker and the resilience pipeline are the production code.
- Only the HTTP calls to the dependencies are replaced. `FakeDependencies` is an in-memory copy of the 12 WireMock mappings that the service calls.
- The tests read the provided fixtures from `wiremock/__files`: `sample-event.json`, `bulk-stock.csv` and `product-contexts-PT.json`.
- Bulk-6 and Bulk-8 hold a dependency call on purpose, through the `BeforeRespond` hook of the fake. This lets a test observe the service while that call is in flight.
- Bulk-11 is the only test that runs on Kestrel instead of the in-memory test server. Kestrel is what enforces the request size limit, so the test starts it on a free port with the limit lowered to 1 KB.
- **Processor tests** (`Proc`) use hand-written fakes for the gateway, the publisher and the context source. They test the rules, not HTTP.
- **CSV tests** (`Csv`) call the parser directly.

### 4.2 Fault inputs from the mock reference in the README

| Input | Mock behaviour | Tests |
|---|---|---|
| `productId` `style-90001` | Lookup returns `429` with `Retry-After` | Dev-7 |
| `productId` `style-90002` | Lookup returns `503` | Dev-7 |
| `sellerId` `seller-429` | Availability write returns `429` | Dev-7 |
| `sellerId` `seller-503` | Availability write returns `503` | Dev-7, Bulk-2 (line 8) |
| Published `eventId` `fault-429` or `fault-503` | Event sink returns `429` or `503` | Pub-1 |
| `productId` `style-9999` | Context with `productStatus` `INACTIVE` | Dev-6, Bulk-2 (line 5) |
| `style-9998`, `size-38`, `PT` | Context with `countryEnabled` `false` | Dev-6, Bulk-2 (line 6) |
| Unknown product | Lookup returns `404`; the product is not in the snapshot | Dev-6, Bulk-2 (line 7) |

### 4.3 Differences between the fake and WireMock

I compared the fake with each mapping file. The routes, the fault inputs and the status codes match. These points differ:

- **`Retry-After`:** WireMock sends 5 seconds. The fake sends 0 by default to keep the tests fast. Dev-8 sets it to 1 second and measures the wait.
- **Number of retries:** the tests configure 2 retries, so 3 attempts. The default configuration has 3 retries, so 4 attempts.
- **Error bodies:** the fake leaves out the `message` field. The service reads error bodies only as a diagnostic snippet.

### 4.4 Tests checked by breaking the code

A test that cannot fail proves nothing. For each row below, I changed the code so that it breaks one rule, ran the tests, and restored the code. After the last change, the source was identical to the original again and all 52 cases passed.

| Change to the code | Rule it breaks | Tests that failed |
|---|---|---|
| The `202` body has no `jobId`. | R4.4 | Bulk-1 |
| The upload waits for the job to end before it answers. | R4.5 | Bulk-6, with a timeout |
| Only the first country of a file gets a snapshot request. | R4.8 | Bulk-7 |
| The snapshot is requested once per row. | R4.8 | Bulk-7 |
| The version store ignores the seller. | R5.1 | Proc-4, seller case |
| The version store ignores the product. | R5.1 | Proc-4, product case; Bulk-1; Bulk-6 |
| The version store ignores the size. | R5.1 | Proc-4, size case |
| The version store ignores the country. | R5.1 | Proc-4, country case |
| A skipped update is still written and published. | R5.3 | Proc-2, Proc-3, Proc-7, Dev-2, Dev-3, Bulk-1, Bulk-2 |
| The per-key lock is removed. | R5.4 | Proc-8, Bulk-8 |

Notes on these runs:

- The suite had 52 cases at the time. Bulk-9, Bulk-10 and Bulk-11 were added later, together with the fix they cover, so they are not part of these runs. I wrote them before the fix and ran them against the code without it: all four cases failed, because the service answered `500`.
- Each run used all 52 cases, with two exceptions:
  - **The upload waits for the job:** I ran Bulk-6 alone, and then the 50 cases other than Bulk-6 and Bulk-8. All 50 passed, so no earlier test noticed a synchronous upload. I did not run Bulk-8 with this change.
  - **The snapshot is requested once per row:** I ran Bulk-7 alone. With this change, the tests that import the provided file would request the snapshot 10,000 times.
- **Size or country ignored:** the new case of Proc-4 is the only one that fails.
- **Skipped update still written:** Bulk-2 fails on a new assertion, the count of PUT calls for the key. Its earlier assertions pass with this change.

### 4.5 Limits of this evidence

- The automated tests never call the WireMock container. I ran the flows against the container by hand:
  - the sample event and its replay;
  - the 10,000-row file, which produced 1 snapshot request, 10,000 PUT and 10,000 POST calls;
  - a second import of the same file, which skipped all 10,000 rows;
  - each fault input in the table of section 4.2;
  - a mixed file with partial failures.
- The changes in section 4.4 target the six closed gaps. I did not check in this way the tests that failed under none of them, for example the tests for validation, retries and CSV parsing.
- Proc-8 and Bulk-8 decide that the newer update is waiting when it has not finished after a short pause: 100 ms in Proc-8 and 200 ms in Bulk-8. On a very slow machine, they could pass although the lock is missing. The pause cannot make them fail when the lock works.
- The code and the tests have the same author and the same reading of the brief. A misreading would be in both.

## 5. Test index

All tests are in `tests/AvailabilityProcessor.Tests`. "Cases" is the number of test cases the method runs.

### `DevMessagesEndpointTests` (Dev): 9 methods, 14 cases

| ID | Test | Cases | Requirements | Decisions |
|---|---|---|---|---|
| Dev-1 | `Sample_event_is_enriched_written_and_published` | 1 | R3.1, R3.6, R3.8, R3.9, R3.10 | D1, D7 |
| Dev-2 | `Replay_with_a_new_eventId_is_acknowledged_without_downstream_calls` | 1 | R3.4 | D1, D4 |
| Dev-3 | `Older_event_arriving_late_is_skipped_and_stock_stays_at_the_newer_version` | 1 | R3.5, R5.3, R5.5 | D1, D4, D13 |
| Dev-4 | `Invalid_event_is_rejected_with_every_validation_error_and_no_downstream_call` | 1 | R3.3 | D1, D2 |
| Dev-5 | `Malformed_json_is_rejected` | 1 | R3.3 | D1 |
| Dev-6 | `Unsellable_products_return_422_and_are_not_written` | 3 | R3.7, R5.8 | D1, D3 |
| Dev-7 | `Persistent_dependency_faults_are_retried_then_reported_as_retryable` | 4 | R5.9, R5.10, R5.11, R5.13 | D1, D8, D13 |
| Dev-8 | `Retry_After_is_honoured_and_a_recovered_dependency_succeeds` | 1 | R5.9, R5.13 | D8 |
| Dev-9 | `Publish_failure_after_the_write_is_acknowledged_and_the_event_is_relayed_later` | 1 | R5.12, R5.14 | D1, D6, D7, D13 |

### `StockUpdateProcessorTests` (Proc): 9 methods, 14 cases

| ID | Test | Cases | Requirements | Decisions |
|---|---|---|---|---|
| Proc-1 | `New_version_is_written_recorded_and_published_with_canonical_context` | 1 | R3.9 | None |
| Proc-2 | `Replay_of_the_same_version_with_a_new_eventId_is_a_duplicate_without_side_effects` | 1 | R3.4, R5.13 | D4 |
| Proc-3 | `Older_version_is_stale_and_leaves_the_newer_stock_untouched` | 1 | R3.5, R5.3, R5.5 | D4 |
| Proc-4 | `Versions_are_only_compared_within_the_same_stock_key` | 4 | R4.10, R5.1 | None |
| Proc-5 | `Unsellable_products_fail_permanently_without_side_effects` | 3 | R3.7, R5.8 | D3 |
| Proc-6 | `Transient_write_failure_is_retryable_and_does_not_record_the_version` | 1 | R5.11, R5.13 | D6 |
| Proc-7 | `Publish_failure_after_a_successful_write_keeps_the_version_and_parks_the_event_in_the_outbox` | 1 | R5.12, R5.14 | D6 |
| Proc-8 | `Event_arriving_while_a_bulk_row_for_the_same_key_is_in_flight_is_never_overwritten_by_the_older_row` | 1 | R5.4, R5.15 | None |
| Proc-9 | `Output_eventId_is_deterministic_per_stock_key_and_version` | 1 | R5.13 | D7 |

### `BulkImportEndpointTests` (Bulk): 11 methods, 14 cases

| ID | Test | Cases | Requirements | Decisions |
|---|---|---|---|---|
| Bulk-1 | `Provided_fixture_is_imported_asynchronously_with_one_snapshot_request_and_reimporting_it_is_a_no_op` | 1 | R4.1, R4.2, R4.4, R4.6, R4.8, R4.9, R4.10 | D4, D5 |
| Bulk-2 | `Mixed_upload_reports_partial_failures_per_row_and_never_overwrites_newer_stock` | 1 | R3.7, R4.1, R4.4, R4.6, R4.7, R4.8, R5.2, R5.3, R5.5, R5.6, R5.7, R5.11 | D3, D4, D5, D11, D13 |
| Bulk-3 | `Malformed_uploads_are_rejected_synchronously` | 3 | R4.3 | D9 |
| Bulk-4 | `Multipart_request_without_a_file_is_rejected` | 1 | R4.3 | D9 |
| Bulk-5 | `Unknown_job_returns_404` | 1 | None | D12 |
| Bulk-6 | `Upload_is_accepted_before_any_row_is_processed` | 1 | R4.5, R4.6 | None |
| Bulk-7 | `Upload_with_several_countries_requests_one_snapshot_per_country` | 1 | R4.8 | D3, D5 |
| Bulk-8 | `Newer_event_arriving_while_an_import_is_writing_the_same_key_is_never_overwritten_by_the_older_row` | 1 | R5.4, R5.15 | D1, D13 |
| Bulk-9 | `Form_body_that_cannot_be_parsed_is_rejected_with_400` | 2 | R4.3 | D9 |
| Bulk-10 | `Csv_sent_as_a_url_encoded_form_is_rejected_with_400` | 1 | R4.3 | D9 |
| Bulk-11 | `Upload_larger_than_the_request_size_limit_is_rejected_with_413` | 1 | R4.3 | D9 |

### `StockCsvTests` (Csv): 5 methods, 11 cases

| ID | Test | Cases | Requirements | Decisions |
|---|---|---|---|---|
| Csv-1 | `Parses_crlf_files_and_maps_columns_by_header_name` | 1 | R4.2 | D2, D10 |
| Csv-2 | `Provided_fixture_parses_into_10000_valid_rows` | 1 | R4.2 | None |
| Csv-3 | `Unusable_header_rejects_the_whole_upload` | 3 | R4.3 | D10 |
| Csv-4 | `Invalid_row_becomes_a_row_level_error` | 5 | R3.3 | D2, D11 |
| Csv-5 | `Quoted_fields_may_contain_commas_quotes_and_line_breaks` | 1 | None | D10 |

### `EventPublisherTests` (Pub): 2 methods, 3 cases

| ID | Test | Cases | Requirements | Decisions |
|---|---|---|---|---|
| Pub-1 | `Sink_fault_eventIds_are_retried_then_surface_as_transient_failures` | 2 | R5.12 | D8 |
| Pub-2 | `Normal_event_is_accepted_by_the_sink` | 1 | R3.9 | None |
