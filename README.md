# POSitive Anywhere WooCommerce connector

The WooCommerce add-on for **POSitive Anywhere V2**. It keeps a WooCommerce store and a POSitive
store in step:

- **POSitive → WooCommerce:** price and stock of linked items, and new POSitive web items created in
  WooCommerce as drafts.
- **WooCommerce → POSitive:** paid orders become POSitive transactions, with the customer, ship-to,
  lines, shipping, tax and payment.
- Anything that cannot be imported or pushed cleanly goes to an **exceptions list** for a person to
  handle. Nothing is adjusted silently.

The connector is included with POSitive Anywhere at no extra charge, and installed with its own
installer. This repository is its source, published under the [MIT licence](LICENSE) so that you
can read how it works, build it yourself, or adapt it to an unusual store.

## How it works

```
WooCommerce ──webhook──▶ POSitive Anywhere server ──/hooks/* forward (loopback)──▶ connector host
                                    ▲                                                   │
                                    └──────────── /api/v2, the connector's own API key ─┘
```

- **A separate Windows service.** `PA.Connectors.Host` runs as the service
  `POSitiveAnywhereConnectors` on the POSitive Anywhere server machine, listening on loopback only
  (default `http://localhost:5220`). It is installed, upgraded and released separately from the
  POSitive server.
- **Only the public API.** The connector talks to POSitive exclusively through the public
  `/api/v2`, with **its own API key** (its own Developer ID, so its links to Woo ids stay in their
  own space), exactly as any outside integration would. It never opens the POSitive database.
- **Its own storage.** Queue, cursors, item and order maps and exceptions live in a SQLite file
  (`connectors.db`); settings in `connectors.json`; the Woo keys, webhook secret and POSitive key in
  a DPAPI-protected `secrets.json` (machine scope).
- **Webhooks through the POSitive server.** WooCommerce posts to
  `https://<your POSitive server or tunnel>/hooks/woocommerce/<connector id>`; the POSitive server
  forwards `/hooks/*` byte for byte to the connector host, which verifies the HMAC signature and
  queues the order. A poll every few minutes backs the webhooks up, so orders still arrive without
  them.
- **Pages through the POSitive server.** The setup, status, match-review and exceptions pages are
  served by the host but reached only at `https://<your POSitive server>/connectors/`, behind the
  POSitive portal login. The server adds the signed-in user and a shared forward token; requests
  without that token are anonymous.

Projects:

| Project | What |
|---|---|
| `PA.Connectors.Core` | engine: durable job queue and retry policy, SQLite stores, the `IConnector` contract, `PosApiClient` (the `/api/v2` client), settings, paths, secret store |
| `PA.Connectors.WooCommerce` | the WooCommerce adapter: `WooClient` (wc/v3), models, webhook signature, catalog sync, order import |
| `PA.Connectors.Host` | the Windows service: worker, `/health`, `/hooks/*` receiver, the embedded pages and their API |
| `PA.Connectors.Tests` | unit tests (xUnit); everything faked in-process, no network or database |

The design, with the exact rules, is in [docs/connectors/WOOCOMMERCE-CONNECTOR.md](docs/connectors/WOOCOMMERCE-CONNECTOR.md);
setting a store up is in [docs/connectors/CONNECTOR-OPERATOR-GUIDE.md](docs/connectors/CONNECTOR-OPERATOR-GUIDE.md).

## Requirements

- **.NET 8 SDK** (or later) to build. The host targets `net8.0` and runs on Windows (Windows
  service, DPAPI).
- A **POSitive Anywhere V2 server** to talk to, and a POSitive user who may create API keys (or an
  API key made for the connector on the server's ops console).
- **WooCommerce REST API keys** (WooCommerce → Settings → Advanced → REST API, Read/Write).
- Optional: Node.js on `PATH` for the page-script tests (they are skipped without it).

## Build, test, run

```powershell
dotnet build PA.Connectors.sln
dotnet test PA.Connectors.sln
```

A release build of the service, as the installer ships it:

```powershell
dotnet publish PA.Connectors.Host -c Release -r win-x64 --self-contained -o publish\host
```

Run it from a console for development, with every file redirected away from `%ProgramData%`:

```powershell
dotnet run --project PA.Connectors.Host -- `
  --Connectors:ConfigPath=$PWD\dev\connectors.json `
  --Connectors:DatabasePath=$PWD\dev\connectors.db `
  --Connectors:SecretsPath=$PWD\dev\secrets.json `
  --Connectors:ForwardTokenPath=$PWD\dev\forward.token `
  --Logging:Directory=$PWD\dev\logs
```

`GET http://localhost:5220/health` answers with the version, schema, worker state and
per-connector counts. To use the pages, point a POSitive Anywhere server at it
(`Positive:Connectors:HostUrl`, default `http://localhost:5220`) and give both the same forward
token file (`Positive:Connectors:ForwardTokenPath` on the server, `Connectors:ForwardTokenPath`
here); then open `/connectors/` on the server.

## Configuration

`connectorhost.json`, next to the exe (it never reads `appsettings.json`, so it can share a folder
with the POSitive server). An edited file is kept by upgrades.

| Key | Default | What |
|---|---|---|
| `Urls` | `http://localhost:5220` | where the host listens; **loopback only**, or it refuses to start |
| `Connectors:PosApiBaseUrl` | `http://localhost:5210` | the POSitive server the connector calls and sends its API key to; loopback only |
| `Connectors:ConfigPath` | `%ProgramData%\POSitiveAnywhereV2\connectors.json` | connector settings (one entry per store) |
| `Connectors:DatabasePath` | `%ProgramData%\POSitiveAnywhereV2\connectors\connectors.db` | the SQLite store |
| `Connectors:SecretsPath` | `%ProgramData%\POSitiveAnywhereV2\connectors\secrets.json` | DPAPI-protected keys |
| `Connectors:ForwardTokenPath` | `%ProgramData%\POSitiveAnywhereV2\connectors\forward.token` | the secret the POSitive server's forwards carry; must be the server's file |
| `Connectors:TickSeconds` / `MaxJobsPerTick` | `5` / `25` | worker pace |
| `Connectors:AllowPrivateStoreHosts` | `false` | **development only.** `false` refuses store addresses on loopback, link-local or private networks (SSRF guard). `true` lets a test store on `localhost` or the LAN be connected. Never set it on a live server. |
| `Serilog` | daily `pa-connectors-*.log` | logging; files go to `%ProgramData%\POSitiveAnywhereV2\logs` |

Per-store settings (`connectors.json`: price level, stock buffer, draft or publish, on-hold orders,
pay-on-completion methods, payment tenders, intervals, webhook address) are edited on the
connector pages; see the operator guide.

## Modifying and deploying a customised build

1. Make and test your change here (`dotnet test`).
2. Publish: `dotnet publish PA.Connectors.Host -c Release -r win-x64 --self-contained -o publish\host`.
3. On the POSitive Anywhere server, with the official add-on installed:
   - stop the service: `sc stop POSitiveAnywhereConnectors`;
   - back up `C:\Program Files\POSitiveAnywhereConnectors\` and
     `%ProgramData%\POSitiveAnywhereV2\connectors\connectors.db`;
   - copy the published files over the program folder, **keeping your `connectorhost.json`**;
   - start it: `sc start POSitiveAnywhereConnectors`, and check `http://localhost:5220/health`.
4. Keep the service running as **Local System**: it shares the forward token file with the POSitive
   service, which runs as Local System too.

Settings, keys and `connectors.db` stay where they are. Things to know:

- Installing an official connector release later replaces your build.
- A build refuses a `connectors.db` whose schema is newer than it knows. If you add a schema
  version, an official release older than it will not start on that file until you restore your
  backup.

## Support

POSitive Software supports the connector **as released**, installed with the official installer.
A customised or self-built connector is the responsibility of whoever built and deployed it. If a
problem may be in your change, reproduce it with the official release before reporting it to us.

## API documentation

The connector uses only the public POSitive Anywhere V2 API. Its interactive reference is on your
own server at **`https://<your POSitive server>/docs`**. Comments that mention
`docs/openapi/v2.json` mean that same OpenAPI document.

## Contributing

Issues and pull requests are welcome. The source of truth is POSitive Software's internal
repository: this repository is generated from it at each release. Accepted changes are merged
there and appear here with the next release, so a pull request may be closed once its change has
been taken over.

## Licence

[MIT](LICENSE). Copyright (c) 2026 POSitive Software Company.
