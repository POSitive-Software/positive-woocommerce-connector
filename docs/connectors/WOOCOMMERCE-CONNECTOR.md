# WooCommerce Connector — Design

How the POSitive Anywhere WooCommerce connector works and the exact rules it follows. Setting a
store up is in [CONNECTOR-OPERATOR-GUIDE.md](CONNECTOR-OPERATOR-GUIDE.md); building and running
the source is in the [README](../../README.md).

Section numbers are stable: code comments cite them (`§4.6`, `§9.5`, …). Labels such as **S2**,
**A3**, **G11**, **F2** or **R1** in comments and test names are explained in §8.

---

## 0. A separate add-on that uses only the public API

The connector is a **separate add-on**. It talks to POSitive **only through the public V2 API**,
exactly as an outside developer's connector would. It does not run inside the POSitive server, and
it never opens the POSitive database.

Why:
- A WooCommerce fix ships on the connector's own schedule, without updating the API server, and a
  connector bug cannot hurt the API service.
- Every gap the connector hits is fixed in the public API for every API user, not worked around
  privately.
- It doubles as a working reference for anyone building another webstore bridge on the API.

"Supported" means standard WooCommerce. Unusual plugins get the exceptions list and the settings,
or the store's own developer can adapt this source or build on the API.

| Area | Design |
|---|---|
| Host | **`PA.Connectors.Host`**: its own Windows service (`POSitiveAnywhereConnectors`), installed beside the POSitive server, released separately |
| POSitive access | `IPosGateway` implemented by **`PosApiClient`**: HTTP to `/api/v2` with the connector's **own API key**, made at connect time under its own Developer ID, so its links to Woo ids stay in their own space |
| Connector storage | **SQLite** in `%ProgramData%\POSitiveAnywhereV2\connectors\connectors.db` (queue, cursors, item/order maps, exceptions). No POSitive schema change, and no POSitive database upgrade for connector releases |
| Webhooks in | the POSitive server **forwards `/hooks/*` byte for byte** (raw body and the `X-WC-*` headers, so the HMAC still verifies) to the connector host's loopback port. One tunnel, and the server does no connector logic. If the host is down the forward returns 503, Woo records a failure, and the backstop poll catches up |
| Setup / status / exceptions UI | **served by the connector host** (loopback `localhost:5220`) but **reached only through the POSitive server at `/connectors/**`**: a byte-for-byte forward like `/hooks`, behind the portal login, so the pages are same-origin with the server's ops console and work over its tunnel. The ops console has a small **Connectors card** showing the host's health (§5.1) |

## 1. What it does

| Direction | What | Authority |
|---|---|---|
| POSitive → Woo | price and stock for linked items; create missing web items in Woo as simple products | POSitive owns catalog, price, stock |
| Woo → POSitive | paid orders become POSitive transactions (type 'N'), with customer, ship-to, lines, shipping, tax and payment | Woo owns new orders |
| Both | anything that can't be imported or pushed cleanly lands in an **exceptions list**. It is never silently adjusted. | — |

Not covered yet: fulfilment/tracking push-back, Woo refund/cancel → POSitive reversal (shows up as
an exception instead), creating Woo *variable* products from POSitive matrix items (existing
variations are linked by SKU), images, categories, coupons as POSitive promotions, multi-location
stock.

## 2. Architecture

```
Woo store ──webhook──▶ POSitive Anywhere server (:5210 / its tunnel)
                        └─ /hooks/{**rest}   byte-for-byte forward, nothing else
                              │ loopback
                              ▼
PA.Connectors.Host  (Windows service POSitiveAnywhereConnectors, loopback only, default http://localhost:5220)
├─ POST /hooks/woocommerce/{connectorId}   verify HMAC → enqueue order.import → 200 (never inline)
├─ GET  /health                            version, schema, worker state, per-connector queue depth,
│                                          open exceptions, last push / last import
├─ ConnectorWorker (BackgroundService)     drains the queue; schedules recurring jobs
└─ connectors.db (SQLite)                  queue, cursors, item/order maps, exceptions
        │
        ▼  HTTP, X-Api-Key = the connector's OWN key
POSitive /api/v2 (the public API, exactly as any integrator uses it)

PA.Connectors.Core         engine: job queue + retry policy, ConnectorRunner, SQLite stores and
                           migrator, IConnector contract, IPosGateway + PosApiClient,
                           connectors.json, ConnectorPaths, the add-on's own DPAPI secret store
PA.Connectors.WooCommerce  adapter: WooClient (wc/v3, Basic auth over HTTPS, paging, batch ≤100,
                           429/5xx backoff), models, webhook signature, mappers
```

- **No POSitive server code, no POSitive database.** None of the connector projects references the
  POSitive server or its data layer (a test asserts it). The few machine utilities the add-on needs
  live in PA.Connectors.Core: `ConnectorPaths` (the `%ProgramData%\POSitiveAnywhereV2` root, its own
  files) and `DpapiConnectorSecretStore` (LocalMachine DPAPI, its own `connectors\secrets.json`).
- **`IPosGateway` = `PosApiClient`.** The operations the engine needs, by local ids: find
  (`GET /customers?externalId=`), create and PATCH (merge-patch, non-null fields only) a customer,
  add an address (`POST /customers/addresses?externalId=`), create a transaction, post a payment
  (`POST /transactions/payments?externalId=`), list products (`modifiedSince`, `webstoreOnly`,
  paging), resolve a sku (`GET /products?sku=`), read links (`?externalId=`, `?invNo=`) and write
  one (`PUT /products/{invNo}/link`). The tenant is whatever the key resolves to.
  - *Error mapping:* a 4xx problem+json becomes a `PosResult` failure with the endpoint's own
    `errorCode`, `detail` and per-field `errors`. A 422 `no_writable_lines` keeps each line's
    status. A 401/403 is a non-retryable job failure (the key is wrong, so reconnect), as is a
    missing key. A 5xx, 408, 429, timeout or refused connection throws, so the job retries with
    backoff.
- **One API tenant per connector.** The connector calls the API with its own key, made for its own
  Developer ID (for example `WOO-shop1`) and stored in the add-on's secret store as
  `connector-<id>-pos-api-key`. The connector never makes keys itself: a signed-in POSitive user
  creates it from the connect wizard (§5).
- **Durable queue, single consumer for orders.** Jobs live in connectors.db, so a reboot mid-sync
  resumes. Order imports run one at a time per connector.
  - A claim leases one due job (`running` until `LeaseUntil`; an expired lease is reclaimed) and
    counts an attempt. The host is the only process that owns the file. Every write runs under an
    in-process write gate inside a SQLite `BEGIN IMMEDIATE` transaction (WAL, `busy_timeout`).
    Failures back off exponentially (30 s doubling, capped at 1 h, 8 attempts). After that the job
    is `failed`, and a `job_failed` exception is raised in the same transaction. A non-retryable
    failure (`ConnectorJobException`, `Retryable=false`) goes straight there. A failed job whose
    key already has a newer pending job is closed as superseded, and the newer one inherits its
    attempt count.
  - *Worker:* `ConnectorWorker` re-reads connectors.json when it changes. With no file or no
    enabled connector it calls nothing and logs that once. Each enabled connector gets its own
    `PosApiClient` (its own key, at the host's `Connectors:PosApiBaseUrl` — loopback only, §5.1).
- **Webhooks in.** The POSitive server maps `/hooks/{**rest}` to a plain forward to its
  `Positive:Connectors:HostUrl` (default `http://localhost:5220`): method, path and query, the raw
  body, `Content-Type`, every `X-WC-*` header and `User-Agent` (not cookies or `Authorization`),
  and returns the host's answer unchanged. Bodies over 1 MB get 413; a host that is down gets a
  fast 503 `connector_host_unavailable`. The host's receiver checks the connector exists, answers
  Woo's unsigned `webhook_id=N` ping with 200, verifies `X-WC-Webhook-Signature` over the raw bytes
  with `connector-<id>-woo-webhook-secret`, ignores non-`order.*` topics (200), and enqueues
  `order.import` keyed by the order id. Duplicate deliveries coalesce. A disabled connector's
  deliveries are still queued.
- **Host.** `PA.Connectors.Host` (net8.0 Web SDK) runs as the Windows service
  `POSitiveAnywhereConnectors` (`UseWindowsService`). Its content root is the exe folder, and it
  reads its own `connectorhost.json`, never `appsettings.json`, so it can share a folder with the
  POSitive server. `Urls` defaults to `http://localhost:5220`, and the host refuses to start on any
  non-loopback URL. Serilog writes `pa-connectors-*.log` to `%ProgramData%\POSitiveAnywhereV2\logs`;
  a startup failure before logging exists goes to `pa-connectors-startup.log`. The host migrates
  connectors.db on startup and refuses a file from a newer build.

### 2.1 Connector storage — `connectors.db` (SQLite)

`%ProgramData%\POSitiveAnywhereV2\connectors\connectors.db` (`Connectors:DatabasePath` overrides
it; tests use a temp file). The schema version is `PRAGMA user_version`, and a small migrator in
`ConnectorDatabase` applies each version in one transaction. Times are ISO-8601 UTC text.

| Table | Purpose |
|---|---|
| `ConnectorJob` | queue: id, connectorId, kind (`order.import`, `order.poll`, `catalog.push`, `catalog.reconcile`, `webhook.health`, the review kinds), resourceKey, payload, status, attempts, nextAttemptAt, leaseUntil, lastError, timestamps, `FailedPermanently` (1 = dead-lettered because retrying cannot help, 0 = ran out of attempts on transient faults). Partial unique index on (connectorId, kind, resourceKey) `WHERE Status = 'pending'`, so duplicate deliveries coalesce. |
| `ConnectorCursor` | watermarks: the POSitive change feed's `modifiedSince`, Woo `modified_after` for the order backstop poll, last-scheduled times of recurring jobs |
| `ConnectorItem` | Woo id ⇄ invNo, plus the Woo parent id (variations need it for the URL), and a hash of the last price/stock pushed so unchanged items are skipped. The link is also written through the API (`PUT /products/{invNo}/link`), so `/api/v2` reads show the Woo id as `externalId`. |
| `ConnectorOrder` | Woo order id → POSitive invoice, import status, **payment posted flag** (payment idempotency, §4.5), `ImportedTotal` / `ImportedTax` (Woo's total incl. tax and its tax as imported, §4.6) |
| `ConnectorStockHold` | Woo orders that hold Woo stock but are **not in POSitive**, per (order, invNo): quantity, Woo status, count of lines no item could be found for, last checked. Every push subtracts the held quantity (§3.1, §4.6). |
| `ConnectorException` | id, connectorId, kind, Woo ref, POSitive ref, reason, snapshot JSON, status (open/retried/resolved/ignored), who/when resolved, `Resolution`. One OPEN row per (connectorId, kind, ref): a repeat bumps `Occurrences`. |
| `ConnectorIgnore` | match-review "ignore" marks, per (connectorId, ref) — a Woo id or `inv:<invNo>` — with the kind it was ignored from, note, who and when. While a mark exists the review exceptions (`catalog_unmatched_woo`, `catalog_unmatched_pos`, `catalog_ambiguous`) are not raised for that ref. |

Settings are **not** in a database: `%ProgramData%\POSitiveAnywhereV2\connectors.json` holds them
(`Connectors:ConfigPath` overrides it). The Woo consumer key/secret, the webhook secret and the
connector's POSitive API key live in the add-on's DPAPI store (`ConnectorSecretNames.*`).

connectors.json (`ConnectorSettings`):

```json
{ "connectors": [ {
    "id": "shop1", "platform": "woocommerce", "enabled": true, "storeUrl": "https://shop.example.com",
    "priceLevel": 1, "stockBuffer": 0, "publishNewAs": "draft", "overwriteTitleDescription": false,
    "importOnHold": false, "pushIntervalSeconds": 60, "orderPollMinutes": 5, "developerId": "WOO-shop1",
    "payOnCompletionMethods": ["cod"], "paymentTenders": { "cheque": "check" } } ] }
```

There is no per-connector POSitive API address: the connector sends its key there, so it is the
host's own setting (`connectorhost.json` `Connectors:PosApiBaseUrl`, default
`http://localhost:5210`, loopback only — §5.1). A `posApiBaseUrl` left in an older connectors.json
is ignored and dropped on the next save.

Secrets are `connector-<id>-woo-consumer-key`, `-woo-consumer-secret`, `-woo-webhook-secret` and
`-pos-api-key`. `developerId` is informational: it records which Developer ID the key was made
under. The key decides the tenant. **Validation:** two connectors (enabled or not) may not record
the same `developerId` (trimmed, any case) or resolve to the same API-key secret name, because they
would link over each other's customers, orders and products.

## 3. POSitive → Woo (catalog, price, stock)

**Which items:** POSitive items flagged as web products (the API's `webProduct` / `webstoreOnly`).

**First connect: matching.** The connector pulls every Woo product and variation, then matches
them where **Woo SKU = POSitive SKU or barcode**.
- Ambiguous or unmatched items on either side go to a review list.
- Woo-only items stay unmanaged.
- POSitive web items with no Woo match are created in Woo as **simple products, status draft**
  (setting: draft/publish), so the owner reviews them before they go live.
- Matches are stored in `ConnectorItem` and, through the public link endpoint, in POSitive.

**Ongoing push:**
- **Incremental:** every 60 s, read `products?modifiedSince=<cursor>`, map the results to Woo
  updates, skip any whose hash hasn't changed, then send `products/batch` / `variations/batch` in
  chunks of up to 100, checking each item's error.
- **Nightly full reconcile:** compare everything and fix drift.
- **Fields pushed:** `regular_price` from a chosen POSitive price level (default level 1);
  `sale_price` from the matching sale price when active; `stock_quantity` with
  `manage_stock=true`, as an **absolute** value (below), never below 0.
- **Title and description** are sent only when the connector creates a product (setting: also
  overwrite on every push, default off): Woo descriptions are usually richer than POSitive's.

**Stock value: the API's `availableLive`.** An order created through the API is not counted in
`available` until the POSitive desktop next recounts the item, so pushing `available` would undo
Woo's own decrement and let the store oversell. `availableLive` is the same quantity counted live
(in stock − not-sellable − held on open order lines). The push value is `availableLive` less the
stock buffer less what Woo orders not yet in POSitive hold (§3.1). After an order import, the
connector **re-pushes the stock of every item on that order straight away**.

### 3.1 As built

Code: `PA.Connectors.WooCommerce/Catalog/` (`WooCatalogSync` = job handler and entry points,
`WooCatalogRun` = one job's work, `WooCatalogSnapshot`), engine-side helpers in
`PA.Connectors.Core/Catalog/`. Tests: `PA.Connectors.Tests/Catalog/` (in-memory Woo store under the
real `WooClient`, fake gateway, real SQLite item store).

- **Entry points.** `WooConnector.Catalog` exposes `RunMatchAsync(createMissing)` (returns a
  `CatalogRunSummary`), `CreateInWooAsync(invNos)`, `ReconcileAsync()`, `PushItemsAsync(invNos)`
  and `PushChangesAsync()`. The pages enqueue jobs for them rather than calling them inline.
- **Matching.** Reads every Woo product (status "any", not trash) and the variations of every
  variable product, and every POSitive web product (`webstoreOnly=true`, pages of 200). Only
  **simple products and variations** are linked; variable parents, grouped and external products
  are never linked, but their SKUs still count. Order: (a) a POSitive item already carrying this
  tenant's link to a Woo id is taken over as is (map lost, reinstall); an item linked to something
  that is not a product of the store is `catalog_link_conflict`; (b) Woo SKU = POSitive `sku`,
  trimmed and case-insensitive, else `ResolveSkuAsync` (alternate barcodes + the item's barcode)
  restricted to web items; (c) a POSitive item claimed by two Woo items, two POSitive items on one
  Woo SKU, or a Woo SKU shared by several Woo items is `catalog_ambiguous` and nothing on it is
  linked or created. Each match is `PUT /products/{invNo}/link` and then the `ConnectorItem` row; a
  refused link (409/404) is `catalog_link_conflict` and nothing is mapped.
- **Create.** Unmatched web items → `products/batch` creates (≤100 per request) as
  `type: simple`, `status` = `publishNewAs`, `name` = description (else sku), `description` = the
  web text else the long description, `sku`, prices, `manage_stock: true`, stock. Results are
  matched back by SKU; a per-item error is `catalog_create_failed` (ref `inv:<invNo>`). **A web
  item with no SKU is never created** (nothing would tie the Woo product back to it): it is
  `catalog_unmatched_pos` for review. **Matrix items** are never created (§9.5). Outside a full run
  the SKUs are first looked up in Woo (`?sku=`) and linked instead of created, which also makes a
  create retried after a 5xx safe.
- **Values** (`CatalogMapper`): `regular_price` = the `priceLevel` price; `sale_price` = that
  level's sale price when it is **above 0 and below the regular price after rounding**, else `""`
  (removes a sale); `stock_quantity` = ⌊`availableLive` − `stockBuffer` − **held**⌋, never below
  0, where **held** = the item's quantity on Woo orders that hold Woo stock but are not in POSitive
  (`ConnectorStockHold`, §4.6). An item without a price at the level, or without `availableLive`
  (an older server), is refused (`push_failed`), never published from `available`. Money is
  `decimal` rounded half away from zero to the store's `woocommerce_price_num_decimals` (default
  2), formatted with the invariant culture.
- **Push (`catalog.push`, no payload).** `modifiedSince` = cursor − **5 minutes overlap**,
  `webstoreOnly=true`, paged until a **short page**. The cursor is the job's **start time**,
  written only after the whole job succeeded; a transient fault leaves it, and the job retries.
  Re-read items are skipped by the **hash** (SHA-256 of exactly the values pushed). Updates go to
  `products/batch` (simple) or `products/{parent}/variations/batch`, ≤100 per request; a per-item
  error is `push_failed` (ref = Woo id). With **no cursor yet** the push runs the full match
  instead, then sets the cursor.
- **Push with a `CatalogPushPayload`.** Exactly those invNos, pushed **regardless of cursor and
  hash** — Woo moved its own stock for the order, so an unchanged hash does not mean Woo agrees.
- **Reconcile (`catalog.reconcile`).** Full snapshot of both sides; each linked item is compared
  with the **live Woo values** and pushed only when they differ; new SKU matches are linked and new
  web items created, silently (no review exceptions). This also catches items the change feed
  misses (G11).
- **Deleted Woo products.** Detected by a batch item error `…_invalid_id` or by absence from the
  reconcile snapshot, then confirmed with a GET. If exactly one other unlinked Woo item has the SKU
  (deleted and re-made), the link and map move there. Otherwise the item is unlinked in POSitive,
  its map row is kept with type `missing` (**never re-created behind the owner's back**) and
  `catalog_woo_missing` is raised; a reconcile re-links it if the product comes back.
- **Recurring jobs.** `catalog.push` every `pushIntervalSeconds`. `catalog.reconcile` is enqueued
  hourly with resource key `nightly`; the handler runs it once per local day at the first check at
  or after **02:00 host time**, and not before the first push has matched.
- **Exceptions** (kinds in `CatalogExceptionKinds`): `catalog_unmatched_woo`,
  `catalog_unmatched_pos`, `catalog_ambiguous`, `catalog_link_conflict`, `catalog_create_failed`,
  `catalog_woo_missing`, `push_failed`. A later run that links, creates or pushes the item closes
  its open rows. Woo errors that retrying cannot fix (401 keys, not a Woo store, unreadable JSON)
  fail the job permanently (`job_failed`).
- Woo reports `manage_stock: "parent"` on a variation whose parent manages its stock;
  `WooManageStockConverter` reads it as false.

## 4. Woo → POSitive (orders)

### 4.1 Getting the events
- **Webhooks:** `order.created` and `order.updated` to
  `https://<public address>/hooks/woocommerce/{connectorId}`. The connector **creates the webhooks
  itself** with a generated secret, so the owner never configures them.
- **Receiver:** reads the raw body (cap 1 MB), verifies `X-WC-Webhook-Signature` = base64
  HMAC-SHA256(body, secret) with a constant-time compare *before* parsing, answers Woo's unsigned
  creation ping with 200, enqueues `order.import(orderId)` and returns 200.
- **Backstop poll:** Woo does **not retry** failed deliveries, and it **disables** a webhook after
  5 consecutive failures. So every 5 min the connector polls `orders?modified_after=<cursor>`, and
  the health job re-activates disabled webhooks and raises an exception. Without a public address,
  polling alone still works, just slower.
- Woo payloads are built at delivery time and can be duplicated or coalesced, so the job
  **re-fetches the order** and ignores the payload.
- **Observed WooCommerce behaviour** (recorded responses under `PA.Connectors.Tests/Fixtures/`):
  - the creation ping is a form post with no signature and no `X-WC-*` headers;
  - `order.updated` waits in Woo's Action Scheduler until the *next request to the site*; the
    connector's poll is itself a request, so it also flushes waiting deliveries;
  - a partial refund leaves the status at `processing`, so refunds are detected from `refunds[]`;
  - SKU lookup (`?sku=`) also returns variations and matches **ignoring case**;
  - an order paid through REST lowers Woo stock at creation.

### 4.2 When an order is imported
- Import when status is `processing` or `completed` (i.e. paid). Setting: also import `on-hold`
  (bank transfer/cheque), default off.
- Ignore `pending`, `failed` and `checkout-draft`.
- An order that is `cancelled` or `refunded`, or that gains a refund *after* import, raises an
  exception for the owner to handle in POSitive. There is no automatic reversal.

### 4.3 Customer
- **Registered Woo customer:** `localCustomerId = woo-c<customer_id>`.
- **Guest:** `woo-g<lowercased billing email>`, so repeat guests reuse one POSitive customer.
- **New customer:** create it, then add the billing and shipping addresses.
- **Existing customer:** PATCH changed contact fields; if the shipping address differs, add a new
  ship-to.

### 4.4 The transaction
- **Header:** `localTransactionId = woo-<order id>`, so a replay returns 409
  `transaction_exists`, which counts as already imported. Type `N`, `createdAt` from
  `date_created_gmt`, `purchaseOrderNumber` = the Woo order number; a note with the payment method,
  coupon codes and customer note.
- **Lines:** `productId` from the item map, with sku as fallback; quantity, unit price, line total
  and tax as Woo computed them. **Every line must resolve before anything is written**: the
  connector holds the whole order as an "unmatched product" exception; the owner links the item,
  then retries.
- **Shipping** becomes a line with `sku: "FREIGHT"` (the store's Freight item, §7.1); without one,
  a non-inventory line. **Fees** are non-inventory lines.
- **Ship-to** goes inline as `shippingAddress`.
- **Reconciliation:** Σ line totals = Woo `total − total_tax`, and header tax = `total_tax`, within
  ±0.01. Otherwise the order goes to an exception and nothing is written.

### 4.5 Payment
- If the order is paid, post one payment for the total. The tender follows the `paymentTenders`
  setting (§4.6); unmapped methods post as **R** (charge card).
- `ConnectorOrder.PaymentPosted` is flipped once, and the payment carries `localPaymentId`, so a
  retry never double-tenders (the API answers a replay with 409 `payment_exists`).

### 4.6 As built

Code: `PA.Connectors.WooCommerce/Orders/` (`WooOrderImport` = order.import and routing,
`WooOrderMapper` = pure mapping, `WooOrderPoll`, `WooWebhookHealth`, `WooStockHolds`),
`PA.Connectors.Core/Orders/` (`OrderReconciliation`, `OrderExceptionKinds`), the receiver in
`PA.Connectors.Host/WebhookEndpoints.cs`. Tests: `PA.Connectors.Tests/Orders/` (the real import,
WooClient, PosApiClient and SQLite stores against fake Woo and fake `/api/v2` HTTP handlers, driven
by the recorded fixtures).

- **Receiver:** only `order.created` / `order.updated` queue an `order.import` (key = the Woo order
  id). Any other topic, or none, is 200 `ignored`, so Woo never counts it as a failure. Order of
  checks: size (413) → connector exists → unsigned ping (200) → HMAC over the raw bytes (401) →
  topic → parse the id (400). Nothing runs inline.
- **order.import** re-fetches the order and ignores the payload.
  - *Pending row first:* a row still `pending` means a create was attempted and its outcome never
    recorded. The connector first asks POSitive for the transaction (Recovery, below); if it
    exists the order is recorded as imported.
  - *Status gate:* `processing` / `completed`, plus `on-hold` when `importOnHold`. Anything else
    writes nothing to POSitive; only its stock hold is recorded or cleared.
  - *Stock holds (`ConnectorStockHold`):* Woo reduces stock for `on-hold`, `processing` and
    `completed` orders. Whenever an import or poll sees such an order that is **not imported**, its
    lines are recorded per invNo; every push subtracts them (§3.1). The hold is cleared when the
    order is imported (POSitive's `availableLive` then counts it), cancelled, refunded, trashed,
    deleted, or back in pending/failed. A line no item can be found for cannot be subtracted: it is
    counted and logged. A change to an order's held quantities queues a `catalog.push` of its items.
  - *Already imported:* post the payment if it is now paid and not yet posted, then check for
    changes: `cancelled`/`refunded`/`trash` status, refund ids in `refunds[]`, or an **edited
    total**. Each new change raises **`order_changed_after_import`** once (the change signature is
    kept in a cursor, so redeliveries stay quiet) and queues a stock push of the order's items.
  - *Edited after import:* the row keeps `ImportedTotal` / `ImportedTax`. If Woo's total or tax
    later differs, the change is reported and **the payment is not posted automatically**.
  - *Before anything is written:* the customer key; every line resolved (item map first:
    `variation_id` if non-zero, else `product_id`; then the line's sku through
    `GET /products?sku=`, which must match **exactly one** item); the totals reconciled. A failure
    raises **`order_unmatched_product`** (every bad line listed), **`order_totals_mismatch`**, or
    **`order_rejected`** (e.g. a guest without an email). A later import that succeeds closes them.
- **Customer:** `woo-c<customer_id>` / `woo-g<trimmed lowercased billing email>` (a guest key over
  100 characters becomes `woo-gh` + 40 hex of SHA-256). Found by `GET /customers?externalId=`. If
  absent it is created from billing (a company makes it a business customer); a 409
  `customer_exists` re-finds it. If present it is PATCHed with only the non-blank fields that
  differ. The billing address is posted every time; POSitive keeps the first one.
- **Ship-to:** sent **inline** as `shippingAddress` without a `localAddressId`, so the API reuses an
  identical address the customer already has and adds one only when it differs. If every shipping
  line is `local_pickup*`, it sends `deliveryMethod: "C"` and no ship-to.
- **Lines:** product lines take `quantity`, `price = round(total / qty, 4)`, `lineTotal = total`,
  `taxAmount = total_tax` and no description. **Shipping:** one line per non-zero shipping line,
  on the `FREIGHT` item when `GET /products?sku=FREIGHT` finds exactly one, else a non-inventory
  line "Shipping - <method>". **Fees:** non-inventory "Fee - <name>", negative allowed. Descriptions
  are cut to 40. Coupons add no line (Woo's line totals are already after the discount).
- **Rounding.** Orders are read with **`dp=6`**, because Woo keeps line, shipping, fee and tax
  amounts unrounded. Reconciliation runs on those figures; only then is the transaction rounded to
  cents, with the cents rounding gained or lost moved onto the lines whose rounding moved them
  most, so **Σ line totals = header total and Σ line tax = header tax exactly**.
- **Write steps:** the row is saved `pending` before the create. 201 → `imported`. 409
  `transaction_exists` → looked up (Recovery) and `imported`. 404 `customer_pending` → retry. 503
  `database_upgrade_required` → **`pos_upgrade_required`** and retry. Any other refusal →
  `order_rejected`, nothing written. Lines POSitive skipped on a create that succeeded raise
  **`order_lines_skipped`**.
- **Recovery** (`IPosGateway.FindTransactionAsync`): `GET /transactions/pending?externalId=woo-<id>`
  answers the pending transaction this key linked; a 404 `transaction_invoiced` names the invoices,
  read with `GET /invoices/{id}`.
- **Payment**, if paid (and, for a pay-on-completion method, only once `completed`), not
  cancelled, refunded, trashed or edited after import, and total ≠ 0: amount = the imported total,
  `localPaymentId woo-<id>-pay`. 409 `payment_exists` = already posted. `PaymentPosted` is flipped
  **after** the post: a crash in between replays into 409, never a second tender.
- **Pay on completion (`payOnCompletionMethods`, default `["cod"]`).** For these Woo
  `payment_method` ids the order is imported at `processing`, but its payment is posted only once
  it is `completed`. Set `[]` to treat every method alike.
- **Payment tender (`paymentTenders`, default `{}`).** Woo `payment_method` id → `card` (**R**,
  charge card), `check` (**C**) or `onAccount` (**A**, which also records a closed deposit on the
  customer's account). A method not in the map posts as **R**. The Settings step lists one row per
  enabled Woo gateway. There is no cash choice: the API's payment route accepts only C, R and A.
- **Outages.** `order.import` retries for about 24 hours (30 s doubling, capped at 1 h, 31
  attempts); other kinds keep the default (8 attempts ≈ 2 h). An import that still runs out is
  dead-lettered (`job_failed`) and **every order.poll re-queues it** while its order is not fully
  imported. Permanent failures (a bad key) are not re-queued.
- **order.poll** (every `orderPollMinutes`): `orders?modified_after=<cursor − 5 min>&dates_are_gmt=true`.
  The cursor only moves to a `date_modified_gmt` Woo reported, after the whole listing was read.
  With no cursor it starts 1 hour back (the connect wizard seeds it). Held orders the listing has
  not shown for an hour are re-read by id. The first poll after each start also queues a
  `webhook.health`.
- **webhook.health** (hourly): needs **`webhookBaseUrl`** (delivery URL =
  `{webhookBaseUrl}/hooks/woocommerce/{id}`). When it is empty, orders arrive by the poll.
  Otherwise, for each of `order.created` / `order.updated` on that URL (webhooks on other URLs are
  never touched): missing → created with the stored secret (32 random bytes made and saved first);
  paused or disabled → re-activated, with a `webhook_unhealthy` exception; a different secret →
  replaced.

## 5. Ops console (setup, status, exceptions)

- **Connectors card** on the server's ops console: per store — enabled, last push, last import,
  queue depth, open exceptions, items to review.
- **Connect a store:** a five-step wizard (§5.1.2): store address and Woo keys with **Test
  connection**; the settings; the **POSitive API key**; orders and webhooks; Enable. Then run the
  match.
  - **The connector's POSitive API key.** Keys are only ever created by a signed-in person on the
    POSitive server: the wizard's **Create key for this connector** calls the server's key route
    with the user's own session (it needs the *Able To Create API Keys* permission). The key goes
    straight to the connector's secret store and is never shown. Or a key made on the ops console
    is pasted, and checked with `GET /api/whoami`.
  - **Developer ID:** one per connected store (`WOO-<store>`), so each store's links stay separate.
    If `WOO-<store>` already belongs to a key, creating stops with *"This key will share links and
    data with: <names>"* and only a second, explicit click creates it.
  - **Replacing a lost or leaked key:** create the new key with the **same** Developer ID and
    confirm the share, then revoke the old key; the links are kept because they belong to the
    Developer ID, not the key.
- **Match review:** unmatched/ambiguous items with a POSitive item picker; "create in Woo" /
  "ignore".
- **Exceptions page:** list, detail (snapshot), **Retry**, **Ignore**, and for unmatched products
  **Link item → Retry**.
- Permissions: *Operations Settings* for changes, *Operations Dashboard* to view.

### 5.1 As built

Code: `PA.Connectors.Host/Ui/` (`ConnectorUiGate`, `ConnectorUiEndpoints`, `ConnectorUiModels`,
`ConnectorUiServices`, `ConnectorUiAssets`, and the embedded pages `Ui/Pages/index.html`,
`app.js`, `app.css`); the review jobs in `PA.Connectors.WooCommerce/Catalog/WooCatalogReviewJobs.cs`;
`PA.Connectors.Core/Jobs/ReviewJobs.cs` and `Stores/SqliteConnectorUiStore.cs`; `WooStoreInfo`.
Tests: `PA.Connectors.Tests/Host/` (`ConnectorUiApiTests`, `CatalogReviewJobTests`,
`ConnectorPageSourceTests`, and `ConnectorPageScriptTests` — the wizard's logic run headless in
Node's `vm`, skipped without `node` on PATH).

**How a user reaches it.** `https://<server or tunnel>/connectors/` → the POSitive server →
`http://localhost:5220/connectors/…` on the host. Nobody uses port 5220 directly.

| Layer | Rule |
|---|---|
| POSitive server portal gate | `/connectors` needs a portal session (a browser is sent to sign in, a fetch gets 401). Viewing needs *Operations Dashboard*; anything else also *Operations Settings*. |
| POSitive server forward | Method, path + query, body (≤ 1 MB) and Content-Type as-is. A path holding an encoded `/`, `\` or `%` is refused, never forwarded. Only a few safe request headers are copied — never cookies, `Authorization` or `X-Forwarded-*`. The server sets **`X-PA-Portal-User`**, **`X-PA-Portal-Can-Write`** and **`X-PA-Forward-Token`** itself, after removing any copy the client sent. A cross-site non-GET is refused. |
| Host gate (`ConnectorUiGate`) | The peer must be loopback, and the `Host` header must be `localhost` / `127.0.0.1` / `[::1]` (DNS rebinding). **The portal headers count only next to a matching `X-PA-Forward-Token`** (constant-time compare): any other local process can reach :5220, so without the token the request is anonymous. `/connectors/api/**` needs a portal user (401 `portal_user_required`) and, for any write, `X-PA-Portal-Can-Write: 1` (403 `settings_permission_required`). Responses are `no-store` and `nosniff`; the pages carry a CSP with no inline script. |

**Host API (`/connectors/api`, JSON; errors are problem+json with an `errorCode`).** Every write
is logged as `AUDIT connector <action> <id> by <user>: …`, never with a key or secret.

| Route | What |
|---|---|
| `GET /me` | the caller, and whether they may write |
| `GET /connectors`, `GET /connectors/{id}` | settings, which secrets are stored (booleans only — **values are never returned**), status, and `setup` (the wizard rail, §5.1.2) |
| `POST /connectors` | create: id, storeUrl, Woo keys, settings, `orderStart` (`now` or `since` a date). Saved **disabled**, with `developerId = WOO-<id>` |
| `PUT /connectors/{id}` | edit (null = keep; both keys blank = keep the stored ones). Moving `storeUrl` to another origin **clears the stored Woo keys** unless both are re-entered |
| `POST /connectors/{id}/enable` · `/disable` · `DELETE /connectors/{id}` | enable refuses (409 `connector_not_ready`) until the connector is runnable; remove clears its settings and secrets but keeps its connectors.db data |
| `POST /test-connection` | product count, currency, price decimals, taxes, prices-include-tax, HPOS. Failures are fixed sentences plus at most Woo's status and error code. 10 per user per 5 minutes |
| `POST /connectors/{id}/pos-key` · `…/pos-key/verify` | checks a key with `GET /api/whoami` at the host's own `Connectors:PosApiBaseUrl`, then stores it; never echoed. Rate limited |
| `POST /connectors/{id}/order-start` | moves the order poll cursor |
| `GET /connectors/{id}/payment-gateways` | the store's enabled Woo gateways and the tender each posts as |
| `GET /connectors/{id}/status` | the view, exception counts, the last 25 jobs |
| `GET /connectors/{id}/review` and `POST …/review/match` · `create` · `link` · `ignore` · `unignore` · `ignore-many` · `unignore-many` | match review. Catalog work is **enqueued** (202 with the job id), never run inline. A link the item map shows would clash is 409 `link_conflict`; `replace: true` (the owner confirmed) unlinks first |
| `GET …/pos-products?q=` | the item picker, through the connector's own key; one item that cannot be read is reported on its own, not as a failed search |
| `GET …/jobs/{jobId}` | one job |
| `GET …/exceptions` · `GET …/exceptions/{id}` · `POST …/retry` · `POST …/ignore` | exceptions; retry re-queues what the exception reports |

#### 5.1.1 Review labels F1–F8

| # | Rule |
|---|---|
| F1 | A **Next steps** panel after enabling: run the match, review the matches, keep an eye on Status / Exceptions. |
| F2 | **Review items are not exceptions.** `catalog_unmatched_woo`, `catalog_unmatched_pos`, `catalog_ambiguous` and `catalog_link_conflict` are listed only on Match review, grouped (variations under their parent product), with Link / Create / Ignore. Status shows **Exceptions: N** and **Needs review: N** separately. |
| F3 | The item picker reports an item it could not read as its own row; the rest of the search still works. |
| F4 | A link clash is shown inline with *Unlink #N and link* (asks first) and *Choose a different item*. |
| F5 | No browser `confirm()` / `alert()`: a modal dialog built with DOM calls (CSP-safe). |
| F6 | `ConnectorException.Resolution`: what fixed it, shown first on a closed exception. |
| F7 | `webProduct` null = unknown: no warning shown. |
| F8 | One poller per page, paused while the tab is hidden, slower after 10 minutes without input. |

#### 5.1.2 Review labels R1–R3

| # | Rule |
|---|---|
| R1 | **Wizard rail:** five steps (**1 Store connection** · **2 Settings** · **3 POSitive API key** · **4 Orders & webhooks** · **5 Enable**) listed on the left (a stepper on top on a phone), done ones ticked, needing attention marked. **Next** saves the step and is disabled, with the reason beside it, until the step is complete (step 1 needs a passing Test connection for exactly what is typed). The same rail is an existing connector's Settings page. |
| R2 | **Key step done state:** "✓ A key is stored for this connector", with **Check it** and **Replace key…**. |
| R3 | **Match review names and groups every row**, reading names from Woo for rows that lack them (one batched lookup per page load, cached). |

**Security rules.** Tests: `ConnectorSecurityTests` (labels M2, M3), the `H1_`…`L4_` tests in
`ConnectorUiApiTests` and `PosApiClientTests`.

| Setting / rule | What |
|---|---|
| `Connectors:PosApiBaseUrl` | Where the connector calls POSitive **and sends its API key**. Must be http(s) on loopback, no user info or query, or **the host refuses to start**. Not editable from the pages. The POSitive HttpClient never follows redirects and uses no proxy. |
| `Connectors:AllowPrivateStoreHosts` | **Development only, default `false`.** False: a `storeUrl` whose host is (or resolves to) loopback, link-local, private, CGNAT, unspecified or multicast is refused on create, edit and test connection. DNS is resolved then **and again on every new connection**, so a name that re-points inside afterwards is still refused. `true` lifts the address rule for a test store. A `storeUrl` with user info is refused either way. The Woo HttpClient never follows redirects and uses no proxy. |
| `Connectors:ForwardTokenPath` | The **forward token** the POSitive server puts in `X-PA-Forward-Token`. Default `%ProgramData%\POSitiveAnywhereV2\connectors\forward.token`, written by whichever service needs it first as 32 random bytes with a protected ACL (SYSTEM + Administrators, plus the creating user when that is not SYSTEM). A file with another owner or reader, or malformed, is replaced. If it cannot be read, the pages' API answers 401 — fail closed. Server and host must use the same file. |
| Error text | Viewers never see paths or exception text; details go to the log. `WooClient` keeps only Woo's error `code` and a short `message`. |
| Webhooks | An unknown connector id answers **401 `invalid_signature`**, the same as a bad signature, so ids cannot be probed. |

## 6. Testing

- **Unit tests** (`PA.Connectors.Tests`): mappers against recorded Woo JSON fixtures (simple,
  variable, guest, coupon, shipping, fee, refund); HMAC verification incl. ping and bad signature;
  totals reconciliation; job coalescing and backoff; the SQLite stores; the host's endpoints on a
  TestServer; the security rules; the wizard's script logic.
- **End-to-end:** POSitive Software also runs the connector against a Docker WooCommerce store and
  a test POSitive server before each release (match, push, orders through Woo REST, payments,
  exceptions, webhook and poll-only paths). See the operator guide's developer section for
  setting up a similar test store.

## 7. API notes

The public API changes the connector needed were made in the API itself, for every API user:

| # | Gap | Resolution |
|---|---|---|
| G1 | link an existing product to an external id | `PUT` / `DELETE /api/v2/products/{invNo}/link` |
| G2 | payments had no idempotency guard | `localPaymentId`, replay → 409 `payment_exists` |
| G3 | freight / inline ship-to | inline `shippingAddress` on create; freight stays a line (§7.1) |
| G5 | customer PUT is a full replace | the connector uses PATCH |
| G7 | no customer lookup by email | the connector's own guest key (§4.3) |
| G9 | no transaction cancel/void | changes after import are exceptions |
| G10 | API orders left `available` stale until the desktop recounts | `availableLive` on the product list and stock endpoint |
| G11 | some items never appear in `modifiedSince`, and `totalItems` ignores it | the connector re-pushes ordered items itself, pages until a short page, and reconciles nightly |
| G13 | no read of a transaction by the caller's `localTransactionId` | `GET /transactions/pending?externalId=`; the 409 carries `transactionId` |

### 7.1 Freight

There is no header freight field. Shipping goes on the order as a **line** on the store's Freight
item (`sku: "FREIGHT"`), which is how the POSitive desktop itself records freight; a store without
a Freight item gets a non-inventory line instead, which still prints and totals.

## 8. Labels used in code and tests

- **S0–S6** — the build stages: S0 spikes (WooCommerce shapes, webhooks, stock after an API
  order), S1/S1b the engine and the separate host, S2 catalog push, S3 order import, S4 payment
  idempotency, S5 the pages, S6 end-to-end tests and the installer.
- **A1–A6** — the public API changes the connector needed (§7).
- **G1–G13** — API gaps (§7).
- **F1–F8, R1–R3** — review rules of the pages (§5.1.1, §5.1.2).
- **H1…L4** (incl. M1–M3) — security-review findings, by severity (high, medium, low).

## 9. Decisions

1. Paid Woo orders are imported with the payment posted.
2. Only `processing` and `completed` orders are imported; `on-hold` is a setting, off by default.
3. POSitive web items with no Woo match are created in Woo as drafts.
4. Title and description are sent only when the connector creates the product (a setting
   overwrites them on every push, off by default).
5. Variable products: existing Woo variations are linked by SKU; creating variable products from
   POSitive matrix items comes later.
6. Payment idempotency is fixed in the public API (`localPaymentId`, 409).
7. Woo orders that hold Woo stock but are not imported are held back from every push; a line that
   cannot be resolved cannot be held back (logged).
8. Cash on delivery: `payOnCompletionMethods` (default `["cod"]`) — imported at processing,
   payment posted only at completed.
9. A total or tax edited in Woo after import is reported, and the payment is not posted
   automatically.
10. order.import retries for about 24 hours, and the poll re-queues any that still run out.
11. POSitive web items without a SKU are not created in Woo; they go to review.
12. Two connectors may not share a Developer ID or an API-key secret.
13. Each Woo payment method can post as card (R, the default), check (C) or on account (A). Cash
    waits for a cash payment type in the public API.
