# vPilot Tablet Bridge

vPilot plugin that mirrors radio, private, broadcast and SELCAL messages
(plus connect/disconnect notices) to a small web page served over your home
network, so you can read them on a tablet in a cockpit where the PC itself
is out of reach.

## How it works

The tablet page mirrors vPilot's own window layout (toolbar, Controllers In
Range on the left, Messages/Notes tabs on the right) rather than being just
a message log:

- The plugin implements vPilot's `IPlugin` interface and subscribes to the
  `IBroker` events for messages (`RadioMessageReceived`, `PrivateMessageReceived`,
  `BroadcastMessageReceived`, `SelcalAlertReceived`), connection state
  (`NetworkConnected`, `NetworkDisconnected`) and ATC in range
  (`ControllerAdded`, `ControllerDeleted`, `ControllerFrequencyChanged`).
- Every message is appended to an in-memory log (last 300 entries).
- A small HTTP server, built directly on `TcpListener` (not
  `System.Net.HttpListener`, which needs admin rights or a `netsh` URL
  reservation to bind anything but `localhost`), serves the page and a JSON
  API: `GET /api/messages`, `GET /api/status` (connection state), `GET /api/controllers`
  (ATC in range), and `POST /api/connect`, `/api/disconnect`, `/api/send`,
  `/api/metar`, `/api/atis`, `/api/settings` for the actions the tablet can
  trigger.
- From the tablet you can: connect/disconnect from the network (with the
  callsign/aircraft type/SELCAL remembered between sessions), see who's
  in range grouped exactly like vPilot's own Center/Approach-Departure/
  Tower/Ground/Ramp/Clearance Delivery/ATIS/Observers categories, read and
  filter messages, reply - either on the current radio frequency or, by
  tapping "Odpovědět" on a private message or the reply chip itself, to a
  specific callsign - and request METAR/ATIS (`RequestMetar`/`RequestAtis`,
  ☁ button in the toolbar), with the result appearing in the message log.
  A "Notes" tab gives a scratchpad saved locally in the tablet's browser.
- The page beeps on a new message, with a distinct, more insistent tone
  specifically for SELCAL alerts; sounds can be turned off entirely from
  the settings (⚙) panel.
- The page requests a screen wake lock so the tablet doesn't dim/lock
  itself while it's open - see the caveat about this below.

Two things vPilot's plugin API has no getter for at all, so they simply
aren't in this UI: current COM1/COM2 frequency and TX/RX state, and flight
plan filing (no `RequestFlightPlan`/`FileFlightPlan` method exists in the
installed vPilot version's plugin API). A transponder (Mode C) toggle was
tried too, but was dropped - the API has a setter but no getter for it, so
the tablet could only ever show "the last state we set from here," not the
real state, which wasn't worth the confusion.

Everything is self-contained: no internet access, no external libraries,
just the plugin DLL sitting in vPilot's `Plugins` folder.

## User guide

What each part of the tablet page actually does, once it's up and running
on `http://<your-pc-lan-ip>:8686/`.

### Connecting

Tap **Connect** in the top-left. A form drops down for callsign, aircraft
type and an optional SELCAL code. The aircraft type field suggests ICAO
type designators as you type (e.g. `A320`, `B738`) - vPilot needs the ICAO
type, not an IATA airline code. Both fields remember what you used last
time and pre-fill automatically; the aircraft type box also puts your last
one at the top of its suggestion list. Whether the callsign specifically
gets remembered can be turned off in Settings (see below) - useful if
different people fly from the same tablet/cockpit.

Once connected, the same button becomes **Disconnect** (asks for
confirmation first), the pill next to it turns green and shows your
callsign plus how long you've been connected, updated every 30 seconds.

### Reading messages

The **Messages** tab lists everything: radio traffic, private messages,
broadcasts, SELCAL alerts, METAR/ATIS results, and your own connect/
disconnect notices - color-coded by type. The filter chips above the list
(All / Radio / Private / SELCAL / System / METAR/ATIS) narrow it down. The
active filter switches itself automatically to whatever the most relevant
new arrival is - SELCAL first, then a private message, then a radio call
that mentions your own callsign, then a METAR/ATIS result - so you don't
have to go looking for it. A new arrival also plays a short beep (a more
insistent double-tone specifically for SELCAL), unless sounds are turned
off in Settings.

### Replying

The bar at the bottom of Messages sends on the **current radio frequency**
by default - shown as the "Rádio" chip. Three quick-reply buttons (Wilco /
Roger / Standby) send that word immediately, respecting whatever mode
you're currently in.

To send a **private message**: either tap "Odpovědět" under an incoming
private message (targets that sender automatically), tap a callsign in the
Traffic tab's PM button, or tap the mode chip itself and type in any
callsign directly - useful for starting a conversation with someone who
hasn't messaged you first. Tap the ✕ next to the chip to go back to radio
mode.

### Weather - METAR and ATIS

The ☁ button in the toolbar opens a small form. Both fields just need an
airport's ICAO code (e.g. `LKPR`) - for ATIS, `_ATIS` is appended
automatically, and if the airport splits it into separate Arrival and
Departure ATIS, both are requested at once; whichever is actually staffed
answers, the other is silently ignored. Results land in the message log
under the "METAR/ATIS" filter. Both fields also accept a specific
callsign typed in full (e.g. `LKPR_A_ATIS`) if you already know it.

### Traffic

The **Traffic** tab lists nearby aircraft as vPilot currently sees them -
callsign, type, altitude, heading, speed. There's a **PM** button on each
row that jumps straight to Messages with a private reply already addressed
to that callsign. Note: this list isn't sorted by actual distance from
you - vPilot's plugin API doesn't expose your own aircraft's position, so
there's no way to compute a real distance or bearing here. It's simply
whatever vPilot itself is currently modeling as traffic, which vPilot
already limits to nearby aircraft before these ever reach a plugin.

### Notes

A plain scratchpad on the **Notes** tab, saved locally in that tablet's
browser only - it doesn't sync anywhere, including back to the PC.

### Settings

The ⚙ button opens:
- **Remember callsign between connections** - on by default.
- **Sounds for new messages** - on by default; a per-device preference,
  doesn't affect other tablets/browsers pointed at the same plugin.
- **Language** - CS/EN, switches the whole interface instantly. The
  Controllers In Range panel and its 8 categories stay in English
  regardless, matching standard ATC phraseology and vPilot's own window.

### Controllers In Range

The left-hand panel (collapsible on narrow/portrait screens via the button
below it) mirrors vPilot's own "Controllers In Range" list: Center,
Approach/Departure, Tower, Ground, Ramp, Clearance Delivery, ATIS,
Observers, each showing who's online and their frequency.

## Status

Built and already deployed to your local vPilot install
(`%LocalAppData%\vPilot\Plugins\VpilotTabletBridge.dll`). Next time you
start vPilot, check its debug/log window for a line like:

```
[Tablet Bridge] Running. Open one of these addresses on your tablet:
[Tablet Bridge]   http://192.168.x.x:8686/
```

Open that address in the tablet's browser, while it's on the **same Wi-Fi
network** as the PC. Add it to the home screen for a quick full-screen
shortcut. **On iPad, use Safari** - see the known issue below if you'd
rather use Chrome.

## Troubleshooting - plugin doesn't seem to load

If vPilot's Messages panel never shows any `[Tablet Bridge]` lines and the
tablet page won't load, check `%LocalAppData%\vPilot\Plugins\TabletBridge-debug.log`
(created next to the DLL as soon as `Initialize()` runs). No file at all
after vPilot has fully started means vPilot never called into the plugin.

That's exactly what happened during initial setup here, and the cause
wasn't this plugin: a stray copy of **`RossCarlson.Vatsim.Vpilot.Plugins.dll`
(and its `.xml`) had been left sitting directly in the `Plugins` folder** -
a known side effect of the FSLabs vPilot-integration installer. vPilot
already loads its own copy of that assembly from its main install folder;
having a second copy of the exact same assembly inside `Plugins` caused an
intermittent .NET assembly-identity collision that silently broke plugin
loading for *every* plugin (not just this one - a Telegram-notification
plugin on this same machine was equally affected and equally intermittent).

Fix: make sure `Plugins` contains only actual plugin DLLs - `.dll` files
that implement `IPlugin` - never a copy of `RossCarlson.Vatsim.Vpilot.Plugins.dll`
or `.xml` itself. If you ever reinstall or update an add-on that touches
vPilot (FSLabs' integration in particular), check the `Plugins` folder
afterwards and delete those two files again if they reappear.

## Known issue - flickering in Chrome on iPad

On iPad, the message list and other polled parts of the page flicker
continuously in **Chrome**. **Safari on the same iPad does not have this
problem** - use Safari there instead. Not investigated further since most
iPad users are on Safari anyway, but the working theory, for whoever picks
this up later: Chrome for iOS auto-hides/shows its own address bar on the
slightest scroll, which changes `window.visualViewport`'s reported height;
the page used to react to that (to keep form fields clear of the on-screen
keyboard), and that reaction itself was apparently enough to retrigger the
address bar's own show/hide - a feedback loop, Chrome-only because its
address-bar-hiding behavior differs from Safari's even though both run on
WebKit. The `visualViewport` "resize" listener was removed to fix it
(keyboard avoidance now happens only on a field's `focusin`/`focusout`, see
`www/index.html`), which didn't fully resolve it on Chrome - so there's
still something else there worth a closer look if it ever becomes a priority
(e.g. capturing a Safari remote-debugging session over USB from a Mac while
reproducing it in Chrome would be the next real step, rather than guessing
further blind).

## Known limitation - keeping the screen on

The page tries to use the Screen Wake Lock API to stop the tablet from
dimming/locking itself while mounted in the cockpit. That API only works in
a "secure context" per the browser spec - `https://` or `http://localhost` -
and this page is plain `http://<lan-ip>:8686/`, which does **not** qualify.
In practice this means the wake lock will likely just silently do nothing on
most tablets/browsers as set up here. The code is left in (it's harmless,
and works if this is ever put behind HTTPS), but don't rely on it: the
actually-reliable fix today is to just turn off the tablet's own auto-lock
in its system settings for as long as it's mounted (e.g. on iPad: Settings
→ Display & Brightness → Auto-Lock → Never).

## First run - Windows Firewall

The first time the plugin opens the port, Windows may show a "Windows
Defender Firewall has blocked some features of vPilot.exe" prompt. Allow it
on your **Private** network profile - otherwise the tablet won't be able to
reach the page even though vPilot itself works fine. If you don't see a
prompt but the tablet still can't connect, add a rule manually:

```powershell
New-NetFirewallRule -DisplayName "vPilot Tablet Bridge" -Direction Inbound -Protocol TCP -LocalPort 8686 -Action Allow -Profile Private
```

## Changing the port

Default port is `8686`. To use a different one, create
`%LocalAppData%\vPilot\Plugins\TabletBridge.ini` with:

```ini
Port=8686
```

and restart vPilot.

## Project layout

```
VpilotTabletBridge/
  VpilotTabletBridge.csproj   - .NET Framework 4.8 class library (SDK-style project)
  Plugin.cs                   - IPlugin implementation, event wiring, IP/port discovery
  MessageStore.cs             - thread-safe capped message log + JSON serialization
  PluginState.cs              - connection state + persisted last-used connect details
  Controllers.cs               - "Controllers In Range" snapshot, categorized like vPilot's own list
  Traffic.cs                    - nearby-aircraft snapshot for the Traffic tab
  Actions.cs                   - tablet-triggered actions (connect/disconnect/send) against the broker
  Json.cs                      - tiny shared JSON escaping/formatting helpers
  WebServer.cs                 - TcpListener-based HTTP server, serves the page + JSON API
  www/index.html                - the tablet page (HTML/CSS/JS, embedded as a resource)
lib/
  RossCarlson.Vatsim.Vpilot.Plugins.dll  - copy of vPilot's plugin API, for compiling only (not
                                            in this repo - see "Setting up a fresh clone" below)
```

## Setting up a fresh clone

`lib/RossCarlson.Vatsim.Vpilot.Plugins.dll` isn't committed here - it's Ross Carlson's own assembly,
not something this project owns, so it doesn't belong in a public repo. Before building, copy it
in yourself from any machine with vPilot installed:

```powershell
mkdir lib -Force
copy "$env:LocalAppData\vPilot\RossCarlson.Vatsim.Vpilot.Plugins.dll" lib\
```

## Rebuilding

```powershell
cd "VpilotTabletBridge"
dotnet build -c Release
copy "bin\Release\VpilotTabletBridge.dll" "$env:LocalAppData\vPilot\Plugins\"
```

(vPilot must be closed while you overwrite the DLL, since it locks the file
while running.)

## Notes / possible follow-ups

- The log and controllers-in-range list are in-memory only; they reset
  whenever vPilot restarts.
- No authentication - anyone on your LAN who knows the address can read
  messages, connect/disconnect the session, and send messages as you.
  Fine for a home network; don't expose the port beyond your router.
- The connect form remembers the last callsign/aircraft type/SELCAL you
  used, saved to `TabletBridge-connect.ini` next to the DLL.
