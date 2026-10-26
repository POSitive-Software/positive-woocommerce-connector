# Connecting a WooCommerce store — operator guide

For whoever sets up or looks after a store's WooCommerce link. The design and the exact rules are in `WOOCOMMERCE-CONNECTOR.md` (§5.1 for these pages).

## Installing the add-on

The connector is included with POSitive Anywhere at no extra charge, and its source is published under the MIT licence (this repository). It has its own installer, **`POSitiveAnywhereConnectors-Setup-<version>-<sha>.exe`**, available from POSitive Software. Run it on the POSitive Anywhere server, as an administrator. (To run a build of your own instead, see "Modifying and deploying a customised build" in the README.)

- **POSitive Anywhere V2 must already be installed.** Setup checks for it and asks before going on without it.
- It installs to `C:\Program Files\POSitiveAnywhereConnectors\` and registers the Windows service **POSitiveAnywhereConnectors** ("POSitive Anywhere Connectors"): runs as Local System (like the POSitive service, so the two share their forward token), **Automatic (Delayed Start)**, restarts by itself after a failure. Setup starts it.
- It listens on `http://localhost:5220` only. Nothing is opened in the firewall: stores and users reach it through the POSitive Anywhere server (`/hooks/…` and `/connectors/`).
- `connectorhost.json` next to the exe holds the host settings: `Urls` (loopback only), `Connectors:PosApiBaseUrl` (the POSitive server on this machine, default `http://localhost:5210`; change it for a store whose POSitive service uses another port, then restart the service), and `AllowPrivateStoreHosts` (keep `false`: it is for test stores on the local network only). Upgrades keep an edited file.
- Data lives in `%ProgramData%\POSitiveAnywhereV2\`: `connectors.json` (settings), the `connectors\` folder (`connectors.db`, the stored keys in `secrets.json`, `forward.token`), which Setup restricts to SYSTEM and Administrators, and the `logs\` folder shared with POSitive.
- **Upgrade:** run the newer installer. It stops the service, replaces the program and starts it again. The settings, database and keys are kept.
- **Uninstall** (Apps & features → POSitive Anywhere Connectors): stops and removes the service. It asks whether to remove the connector data too; the default **No** keeps `connectors.db`, the stored keys and `connectors.json`, so a reinstall carries on where it left off. Revoke the connector's POSitive key on **/ops → API keys** if it is not coming back. (Uninstalling the main product with "remove settings" deletes this data as well.)
- After installing, the ops console's **Connectors** card shows the add-on as running; then connect a store as below.

## Before you start

- The **connector add-on** is installed on the POSitive Anywhere server (Windows service `POSitiveAnywhereConnectors`). The ops console's **Connectors** card says *"Connector add-on not installed / not running"* when it is not.
- Your POSitive user has:
  - **Able To Access Operations Dashboard** to look at the connector pages;
  - **Able To Access Operations Settings (MA 106)** to change anything;
  - **Able To Create API Keys (MA 107)** for the one-click key step (or ask someone who has it to make the key for you).
- In WordPress: **WooCommerce → Settings → Advanced → REST API → Add key**, permissions **Read/Write**. Keep the consumer key (`ck_…`) and secret (`cs_…`) to hand.
- Optional, for instant orders: the server's ngrok tunnel (ops console → Tunnel). Without it, orders still arrive by polling, every 5 minutes by default.

## Connect

Open **/ops → Connectors → Open connectors** (or go to `/connectors/` on the server, or on its tunnel address) and click **+ Connect a store**. The page walks you through five steps listed on the left (on a phone, across the top): the step you are on is highlighted, finished steps get a green ✓, and you can click a finished step to go back to it. Each step has **← Back** and **Next →**. **Next** saves the step; it stays greyed out, with the reason next to it, until the step is complete.

1. **Store connection**
   - Connector id: a short name such as `shop1`. It cannot be changed later.
   - Store address, consumer key and secret. Click **Test connection**: you should see ✓ with the product count, currency, decimals and tax settings. **Next** unlocks once the test passes (change a field and you test again).
   - **Next** saves the connector, **disabled**, with the default settings.
2. **Settings** — check them, then **Next**:
   - price level;
   - stock buffer;
   - new products as **draft** (the default);
   - on-hold orders (off by default);
   - payment methods paid on delivery (default `cod`);
   - **Payments post as**: one row per payment method enabled in WooCommerce, each with the POSitive tender its payments are posted as — **Charge card (R)** (the default), **Check (C)** or **On account (A)** (also records the deposit on the customer's account, closed). A method left on charge card behaves exactly as before. There is no cash choice: POSitive's API cannot post a cash payment yet. If the store cannot be reached, the step shows only the methods already mapped; reopen it later to see the rest;
   - how often price and stock are pushed, and orders polled.
3. **POSitive API key**: click **Create key for this connector**. It makes a key named *WooCommerce connector - &lt;id&gt;* under Developer ID `WOO-<id>` and hands it to the connector. You never see the key. The step then says **"✓ Your key was created and is stored for this connector (Developer ID WOO-&lt;id&gt;)"** and **Next** unlocks. **Check it** tests the stored key; **Replace key…** is for a lost or leaked one.
   - *"Developer ID … is already used by …"*: stop, unless you are replacing this connector's own lost key. Only then click **Create key sharing this Developer ID**. Two stores must never share one: they would link over each other's customers, orders and items.
   - *"A key called … already exists"*: change the key name and try again.
   - No MA 107? Someone who has it creates the key on **/ops → API keys** (Developer ID `WOO-<id>`), and you paste it under **Or paste a key made on the ops console**.
4. **Orders & webhooks**
   - **Public address**: filled in from the tunnel when it is online — Woo then sends each order at once. Leave it empty for polling only.
   - **Import orders**: *from now on* (the default; older orders are not imported) or *since a date*.
5. **Enable**: click **Enable connector** (it is greyed out until steps 1–4 are done). A **Next steps** panel appears under it (and on the Status page until the first match is reviewed):
   1. **Run the match** — pairs each Woo product with the POSitive item that has the same SKU or barcode, creates the POSitive web items Woo lacks (as drafts) and sends POSitive's price and stock. It starts on its own within a minute of enabling; the button runs it now (or again) and shows its progress and result.
   2. **Review the matches** — opens Match review with the number of items waiting.
   3. **Keep an eye on it** — Status and Exceptions.
   4. **Back to the Server console**.

## After connecting

- **Match review**: what the match could not pair on its own. **This is normal, not an error** — a store usually has Woo products that are not in POSitive. These items are *not* counted as exceptions (Status shows *Needs review: N* apart from *Exceptions: N*).
  - Items are grouped: *Woo products with no POSitive match*, a variable product's variations together under the product (*V-Neck T-Shirt (Woo #10, variable, 3 variations, none matched)*, with **Link…** to start on its first variation and **Ignore all N**; each variation shows its own options such as *Red*, its SKU and Woo #), *Same SKU on more than one item*, *Links POSitive refused during the match*, *POSitive web items not created in Woo*, *POSitive matrix items* and *POSitive web items without a SKU* — each group says once what it means and what to do. Every row shows the product's name and SKU, then its Woo # (rows from before this version are named from Woo when the page opens).
  - **Link…**: search POSitive by item number, barcode or description and **Choose**. An item already linked to another Woo product says so ("linked to Woo #58"); choosing it asks whether to unlink that one first. An item POSitive could not load right now shows as its own row ("item 900248 couldn't be loaded right now") — the rest of the search still works; try again in a moment.
  - If a link is refused (*"WID-011 is already linked to Woo product #58 (Draft, '…'). Link a different POSitive item, or unlink #58 first."*), the message appears right under the row with **Unlink #58 and link** (asks first; #58 stays in Woo, it just stops getting POSitive's price and stock — delete it in WooCommerce if you do not need it) or **Choose a different item**.
  - **Ignore** one item, tick several and **Ignore selected**, or **Ignore all** in a group (sample products, things you only sell online). Ignored items are listed at the bottom: tick them and **Un-ignore selected**, or **Un-ignore** one.
  - **Create in Woo** for a POSitive web item Woo lacks (not for matrix items: create the variable product in Woo with the same SKUs, then run the match).
  - **Run the match** again at any time. Every action is queued and runs within a few seconds; the page shows the job until it finishes.
- **Exceptions**: only things that need you — an order that could not be imported, a payment, a failed price/stock push or create, a refused link, webhooks. Nothing is adjusted silently.
  - Open one to see the reason and the snapshot.
  - **Retry** re-runs it. If it fails again, it comes back.
  - **Ignore** closes it (it asks first).
  - *Order: item not linked*: **Link item…** on each line; the order is imported again once the link is made.
  - A resolved one opens with **what fixed it** (e.g. *"Imported as POSitive order 50001919 on retry; line 14 'Demo Widget' linked to POSitive item 900996."*); the original problem is folded underneath.
  - *Order changed after import* (cancelled, refunded, edited): the connector never reverses anything in POSitive. **Fix the order in POSitive, then Mark as handled** — the button is on the row in the list and on the detail (it asks first, and the list then shows it as *handled*). If the order changes again in Woo, it comes back as a new exception.
- **Status**: last push and import, *Exceptions* and *Needs review*, the queue, stock held for Woo orders not yet in POSitive, the webhook state, recent jobs. **Import since this date** re-reads older orders (ones already imported are recognised). The page refreshes itself every 10 s while it is the visible tab, stops while the tab is hidden, and slows to every 30 s after 10 minutes without a click.
- **Settings** is the same five-step rail: each step shows ✓ or *needs attention* (for example the POSitive key went missing); click a step to change it, then **Save and continue**.
- The **Server console**'s Connectors card shows each store's exceptions and items to review.

## Replacing a key, disconnecting

- **Lost or leaked POSitive key**: Settings → **3 POSitive API key** → **Replace key…** → **Create a new key** → confirm *sharing this Developer ID* (it names the old key). Then revoke the old key on **/ops → API keys**. The links are kept, because they belong to the Developer ID, not the key.
- **New Woo keys**: Settings → **1 Store connection** → paste the new consumer key and secret → **Test connection** → **Save and continue**. Blank fields keep the stored keys.
- **Disable** stops pushes and polls. Webhooks are still accepted and queued.
- **Remove connector** deletes its settings and stored keys; its history and item links in the connector database are kept. Afterwards:
  - revoke its POSitive key on /ops;
  - delete its webhooks in WooCommerce (Settings → Advanced → Webhooks) if the store is not coming back.

## Where to look when something is wrong (support)

- Connector log: `%ProgramData%\POSitiveAnywhereV2\logs\pa-connectors-*.log`. Every change made on these pages is an `AUDIT connector …` line, with the user who made it. Exceptions are logged as they happen: `Catalog exception <kind> <ref>: <reason>`, and for orders `Order exception <kind> <Woo order id>: raised` / `raised again, occurrence N` / `resolved, imported on retry` / `ignored by <user>` / `closed for a retry … by <user>`. Order lines carry the kind, the Woo order id and the POSitive invoice only, never the reason (it can name the customer): read the reason on the Exceptions page.
- *"The connector service is not running"*: start `POSitiveAnywhereConnectors` in Windows Services.
- *Test connection* fails with "refused the consumer key/secret": make a new Read/Write key in WooCommerce. "not a WooCommerce store": check the address. A timeout: the site is slow or down.
- *Connectors card says not installed / not running*: `sc query POSitiveAnywhereConnectors`. If it will not start, read `pa-connectors-startup.log` in the logs folder (a bad `connectorhost.json`, a non-loopback address, or a `connectors.db` from a newer build).
- *The pages say to sign in through POSitive Anywhere*: the request did not come through the server's forward (someone opened port 5220 directly), or the server and the add-on do not share `connectors\forward.token`. Both services must run as Local System; restart both after changing that.
- *Orders arrive only every few minutes*: webhooks are off or failing. Status → webhook state; check the tunnel, then Retry the `webhook_unhealthy` exception. Orders still arrive by polling meanwhile.
- *An item's Woo stock looks wrong*: the push value is POSitive's live available stock (`availableLive`) minus the stock buffer minus what Woo orders not yet in POSitive hold (Status → stock held). Fix the cause (link the item, retry the order) rather than editing stock in Woo: the next push overwrites it.
- **Support bundle** (tray → *Collect support bundle…*): when the add-on is installed the zip also carries the `POSitiveAnywhereConnectors` service state, the `PA.Connectors.Host.exe` version, `connectors.json` and `connectorhost.json` (secret-named values masked, store URLs kept without any user:password), the host's loopback `/health` answer as `connector-health.json` (no credentials sent; open exception counts per connector) and the `pa-connectors-*.log` files under the same 7-day / 20 MB cap as the server logs. It never carries `connectors\secrets.json`, `forward.token` or `connectors.db` (exception reasons can name customers), and any `ck_…`/`cs_…` key, webhook secret, `X-PA-Forward-Token` or `X-Api-Key` in the files above is replaced with `[REDACTED]`. Without the add-on, `manifest.txt` just says "connector add-on not installed".

## Trying the connector against a test store (developers)

- Use a **test** POSitive Anywhere server and database, never a live one: the connector creates customers, transactions and payments.
- Run a WooCommerce test store, for example the official WordPress and WooCommerce images in Docker, with its sample products, and make a Read/Write REST key there.
- Run the connector host from source (see the README) with `--Connectors:AllowPrivateStoreHosts=true`, so a store on `localhost`, `host.docker.internal` or the LAN may be connected. A real install refuses such an address.
- Point the test POSitive server's `Positive:Connectors:HostUrl` at the host and give both the same forward token file, then open `/connectors/` on that server and connect the store as above.
- WooCommerce delivers webhooks with `wp_safe_remote_request`, which refuses private or loopback hosts and unusual ports. For webhooks from a Docker store to a dev server, a small dev-only must-use plugin that filters `http_request_host_is_external` and `http_allowed_safe_ports` is needed; otherwise leave the public address empty and test with polling.