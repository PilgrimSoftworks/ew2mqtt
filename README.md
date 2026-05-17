# ew2mqtt

[![CI](https://github.com/PilgrimSoftworks/ew2mqtt/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/PilgrimSoftworks/ew2mqtt/actions/workflows/ci.yml)
[![Release](https://github.com/PilgrimSoftworks/ew2mqtt/actions/workflows/release.yml/badge.svg)](https://github.com/PilgrimSoftworks/ew2mqtt/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/PilgrimSoftworks/ew2mqtt?logo=github&sort=semver)](https://github.com/PilgrimSoftworks/ew2mqtt/releases/latest)
[![NuGet — Pilgrim.EasyWorship](https://img.shields.io/nuget/v/Pilgrim.EasyWorship?logo=nuget&label=Pilgrim.EasyWorship)](https://www.nuget.org/packages/Pilgrim.EasyWorship)
[![NuGet — Pilgrim.EasyWorship.Discovery](https://img.shields.io/nuget/v/Pilgrim.EasyWorship.Discovery?logo=nuget&label=Pilgrim.EasyWorship.Discovery)](https://www.nuget.org/packages/Pilgrim.EasyWorship.Discovery)
[![Container](https://img.shields.io/badge/ghcr.io-ew2mqtt-2496ED?logo=docker&logoColor=white)](https://github.com/PilgrimSoftworks/ew2mqtt/pkgs/container/ew2mqtt)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A cross-platform .NET 10 service that bridges [EasyWorship](https://www.easyworship.com)'s
undocumented `ezwremote` TCP API to MQTT. State changes (slide number, presentation
number, overlays) are published as retained MQTT topics, and a parallel command topic
tree accepts incoming next/prev/goto/play/overlay commands and forwards them to
EasyWorship.

Designed for Home Assistant, Node-RED, OBS automations, lighting consoles, and
anything else that speaks MQTT.

> **Status:** alpha. Built from publicly reverse-engineered protocol details
> (see [Credits](#credits)) and tested end-to-end against a fake server. Behaviour
> against a real EasyWorship instance may surface protocol corner cases that
> need configuration tweaks — see [Risks & defaults](#risks--defaults).

## Features

- **Auto-discovery** of EasyWorship via mDNS (`_ezwremote._tcp.local.`) with manual
  `Host:Port` fallback.
- **Bidirectional**: publishes `status`/`paired`/`heartbeat` events to MQTT and
  subscribes to a command topic for `nextSlide`, `prevSlide`, `gotoSlide`,
  `play`/`pause`/`toggle`, schedule navigation, and overlay control.
- **Stable pairing**: the GUID used for pairing is generated and persisted on first
  run, so reconnects don't require operator approval again.
- **Robust reconnection**: two-phase exponential backoff that never gives up (1s→5s
  for three minutes, then steady 30s).
- **MQTT LWT** (`availability` topic) for clean dashboards.
- **Cross-platform**: Linux, macOS, Windows. systemd, launchd, and Windows Service
  integration ship in [`packaging/`](packaging/).
- **NuGet-publishable libraries** so you can embed the EasyWorship client in your
  own .NET app without taking the whole bridge.

## Install

### Docker

```sh
docker run --rm \
  --network host \
  -e Ew2Mqtt__Mqtt__Server=mqtt://broker.lan:1883 \
  -e Ew2Mqtt__EasyWorship__DiscoveryMode=AutoThenManual \
  ghcr.io/pilgrimsoftworks/ew2mqtt:latest
```

mDNS discovery requires `--network host` because multicast cannot cross the Docker
bridge. If you can't use host networking, configure a static
`Ew2Mqtt__EasyWorship__Host=10.0.0.5` and skip discovery.

### Self-contained binary

Download a tarball from the [Releases](https://github.com/PilgrimSoftworks/ew2mqtt/releases)
page (Linux/macOS/Windows, x64/arm64), extract, and run:

```sh
./ew2mqtt --Ew2Mqtt:Mqtt:Server=mqtt://localhost:1883
```

### systemd

```sh
sudo install -d /etc/ew2mqtt
sudo install -m644 packaging/systemd/ew2mqtt.service /etc/systemd/system/
sudo install -m755 ew2mqtt /usr/local/bin/
sudo systemctl enable --now ew2mqtt
```

### Windows Service

```pwsh
sc.exe create ew2mqtt binPath= "C:\Program Files\ew2mqtt\ew2mqtt.exe" start= auto
sc.exe failure ew2mqtt reset= 86400 actions= restart/5000/restart/5000/restart/30000
```

See [`packaging/windows/INSTALL.md`](packaging/windows/INSTALL.md) for details.

### macOS (launchd)

```sh
sudo cp packaging/launchd/no.pilgrim.ew2mqtt.plist /Library/LaunchDaemons/
sudo launchctl load /Library/LaunchDaemons/no.pilgrim.ew2mqtt.plist
```

## Pairing & permissions

When you approve the pairing in EasyWorship's Remote panel, the device starts in
**view-only** mode (lock icon 🔒 next to the device name). The bridge can receive
state but **all outgoing commands — slide navigation, overlay toggles,
play/pause — will be silently dropped by EasyWorship.**

Click the lock icon next to the `ew2mqtt` device in EasyWorship's Remote panel
to grant control. The icon flips to a remote-control symbol and commands start
working immediately. The next status frame will show `permissions` non-zero.

If overlay or navigation commands appear to do nothing despite the bridge
logging `EW out: {...}` and EasyWorship echoing back a status with unchanged
state, this is the cause 99 % of the time.

## Configuration

`appsettings.json` (or env vars with `Ew2Mqtt__...` / `EW2MQTT_...`):

```json
{
  "Ew2Mqtt": {
    "InstanceId": "default",
    "EasyWorship": {
      "DiscoveryMode": "AutoThenManual",
      "Host": null,
      "Port": null,
      "Uid": null,
      "DeviceName": "ew2mqtt",
      "DeviceType": 8,
      "HeartbeatIntervalSeconds": 3
    },
    "Mqtt": {
      "Server": "mqtt://localhost:1883",
      "Username": null,
      "Password": null,
      "Tls": false,
      "TopicBase": "ew2mqtt",
      "RetainState": true
    }
  }
}
```

EasyWorship's ezwremote listener binds a **dynamic port** — there is no fixed
default. Leave `Host`/`Port` `null` and let auto-discovery (mDNS, or the
same-machine process probe) learn the real address. Only set `Port` explicitly
for `Manual`/`AutoThenManual` fallback if you genuinely know the port the
running EasyWorship is on; a missing port in those modes is logged as a
configuration error rather than defaulted.

Set `Mqtt.Tls` to `true` (or use an `mqtts://` server URL) for TLS. The broker
certificate is validated against the system trust store with the default
settings — there is no option for self-signed certificates or a custom CA, so
the broker must present a publicly/enterprise-trusted certificate. `Username`/
`Password` are sent as MQTT credentials and should only be used over TLS.

`Uid` is auto-generated and persisted on first run to:

- Linux: `~/.local/state/ew2mqtt/uid`
- macOS: `~/Library/Application Support/ew2mqtt/uid`
- Windows: `%LOCALAPPDATA%\ew2mqtt\uid`

## MQTT topics

All topics are rooted at `<TopicBase>/<InstanceId>`, e.g. `ew2mqtt/default`.

### Published (state, retained)

| Topic                                  | Payload          |
| -------------------------------------- | ---------------- |
| `availability`                         | `online` / `offline` (LWT) |
| `connection`                           | `{"state":"Connected","paired":true,...}` |
| `state/logo`                           | `ON` / `OFF` |
| `state/black`                          | `ON` / `OFF` |
| `state/clear`                          | `ON` / `OFF` |
| `state/presentation/number`            | `<int>` |
| `state/presentation/rowid`             | `<int>` |
| `state/slide/number`                   | `<int>` |
| `state/slide/rowid`                    | `<int>` |
| `state/schedule/rev`                   | `<int>` |
| `state/live/rev`                       | `<int>` |
| `state/imagehash`                      | `<hex>` |
| `state/requestrev`                     | `<int>` |
| `state/slide/title`                    | song slide title (e.g. `Verse 1`) |
| `state/slide/content`                  | slide lyrics / text |
| `state/slide/index`                    | 1-based position in current presentation |
| `state/slide/total`                    | total slides in current presentation |
| `state/presentation/title`             | song / presentation title |
| `state/presentation/loaded`            | `true` once every slide's `slideInfo` has arrived; `false` while still loading |
| `state/presentation/slides`            | full song as JSON: `{pres_rowid, liverev, slides:[{index,slide_rowid,title,content}, …]}` — published when the presentation finishes loading |
| `state/schedule`                       | current EW schedule as JSON: `{count, items:[{index,pres_rowid,revision,title}, …]}` — fetched after pair and updated as song titles arrive |
| `state`                                | aggregate JSON  |

### Published (events, non-retained)

| Topic                | Payload |
| -------------------- | ------- |
| `events/pairing`     | `{"paired":true}` |
| `events/heartbeat`   | `{"requestrev":42,"ts":"…"}` |
| `events/unknown`     | `{"action":"…","raw":"…"}` |
| `events/slide-changed` | `{"slide_rowid":…,"index":…,"total":…,"title":"…","content":"…"}` (fires on every slide change) |
| `events/presentation-loaded` | `{"pres_rowid":…,"liverev":…,"slide_count":N,"ts":"…"}` (fires once per presentation after the final `slideInfo` lands) |

### Subscribed (commands)

| Topic                          | Payload | Effect |
| ------------------------------ | ------- | ------ |
| `cmd/nextSlide` / `prevSlide`  | empty   | navigate |
| `cmd/gotoStartSlide` / `gotoStartPresentation` | empty | jump |
| `cmd/gotoSlide`                | `<int>` or `{"slide":N}` | jump |
| `cmd/gotoSchedule`             | `<int>` or `{"schedule":N}` | jump |
| `cmd/nextSchedule` / `prevSchedule` | empty | navigate |
| `cmd/nextBuild` / `prevBuild`  | empty   | step animations |
| `cmd/play` / `pause` / `toggle` | empty  | media |
| `cmd/overlay/{logo,black,clear}` | `ON`/`OFF`/`TOGGLE` | per-bit |
| `cmd/overlay`                  | `{"logo":..,"black":..,"clear":..}` | atomic |
| `cmd/activate`                 | `{"pres_rowid":N,"slide_rowid":M}` or `{"schedule_index":N,"slide_index":M}` | go live with a specific schedule item's slide |
| `cmd`                          | `{"action":"...",...}` | envelope form |

## Library usage

The protocol client is published as **`Pilgrim.EasyWorship`** on NuGet. mDNS discovery
is split into **`Pilgrim.EasyWorship.Discovery`** so consumers who only want manual
host:port can skip the Zeroconf dependency.

```csharp
using Pilgrim.EasyWorship;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddEasyWorshipClient(o =>
{
    o.Host = "10.0.0.5";
    o.Port = 50123; // EasyWorship's ezwremote port is dynamic — use the actual port
    o.Uid = "your-stable-guid";
});

await using var sp = services.BuildServiceProvider();
var client = sp.GetRequiredService<IEasyWorshipClient>();
client.StatusReceived += (_, ev) => Console.WriteLine($"slide {ev.SlideNo}");
await client.StartAsync();
```

### Autodiscovery

Add **`Pilgrim.EasyWorship.Discovery`** to find the instance via mDNS instead of
hardcoding `Host`/`Port`. `AddEasyWorshipDiscovery()` registers an
`IEasyWorshipDiscovery` that transparently falls back across Bonjour `dns-sd`,
the managed Zeroconf resolver, and a same-machine process/port probe.

```csharp
using Pilgrim.EasyWorship;
using Pilgrim.EasyWorship.Discovery;
using Microsoft.Extensions.DependencyInjection;

// 1. Locate the EasyWorship ezwremote service on the LAN.
var discoveryServices = new ServiceCollection();
discoveryServices.AddLogging();
discoveryServices.AddEasyWorshipDiscovery();
await using var discoverySp = discoveryServices.BuildServiceProvider();

var discovery = discoverySp.GetRequiredService<IEasyWorshipDiscovery>();
EasyWorshipEndpoint? endpoint = await discovery.ResolveOnceAsync(TimeSpan.FromSeconds(5));
if (endpoint is null)
    throw new InvalidOperationException("No EasyWorship ezwremote service found.");

Console.WriteLine($"Found {endpoint.InstanceName} at {endpoint.Host}:{endpoint.Port}");

// 2. Point the client at the discovered host:port.
var services = new ServiceCollection();
services.AddLogging();
services.AddEasyWorshipClient(o =>
{
    o.Host = endpoint.Host;
    o.Port = endpoint.Port;
    o.Uid  = "your-stable-guid";
});

await using var sp = services.BuildServiceProvider();
var client = sp.GetRequiredService<IEasyWorshipClient>();
client.StatusReceived += (_, ev) => Console.WriteLine($"slide {ev.SlideNo}");
await client.StartAsync();
```

To enumerate every instance on the network (e.g. multiple machines), stream
`discovery.BrowseAsync(TimeSpan.FromSeconds(5))` instead of `ResolveOnceAsync`.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build
```

Run the tests (the suites use [Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro);
on the .NET 10 SDK run them directly rather than via `dotnet test`):

```sh
dotnet run --project tests/Pilgrim.EasyWorship.Tests
dotnet run --project tests/Pilgrim.EasyWorship.Discovery.Tests
dotnet run --project tests/Pilgrim.EasyWorship.Mqtt.Tests
```

Run the service locally:

```sh
dotnet run --project src/Pilgrim.EasyWorship.Mqtt \
  --Ew2Mqtt:Mqtt:Server=mqtt://localhost:1883 \
  --Ew2Mqtt:EasyWorship:Host=10.0.0.5
```

Publish a self-contained binary:

```sh
dotnet publish src/Pilgrim.EasyWorship.Mqtt -c Release -r linux-x64 \
  -p:PublishSingleFile=true -p:SelfContained=true -p:PublishTrimmed=true
```

## Slide content & navigation

The bridge automatically fetches the live presentation list and per-slide content
after pairing:

- After `paired`, sends `GetLiveData` to learn the ordered slide list (`slide_rowid`
  + `revision` for each slide).
- For every slide it learns about, sends `getSlideInfo` to fetch the slide title
  and lyric content; results are cached for the lifetime of the process.
- On every `status` frame (i.e. slide change), republishes
  `state/slide/{title,content,index,total}` and the song title under
  `state/presentation/title`. Also fires a single non-retained
  `events/slide-changed` event with the same data merged into one JSON payload —
  the easiest single trigger for automations.

Notes:

- In live-area "double-click from library" flow, EW assigns synthetic negative
  `slide_rowid` values (e.g. `-100165, -100164…`). Title/content lookup still
  works — EW responds to `getSlideInfo` for these IDs too.
- `pres_no` and `slide_no` on the raw status frame are **always 0** in current
  EW 7 builds; they only populate when a Schedule is being run as a presentation
  proper. Use `state/slide/index` (computed from LiveData) instead.
- Lyrics are sent as-is to MQTT. If your broker is shared and you need to keep
  lyric content private, run a private broker or namespace the topic.

## Risks & defaults

The two reverse-engineered references for the protocol disagree on a few details
that may change between EasyWorship versions:

| Field            | ew2vm | companion | ew2mqtt default |
| ---------------- | ----- | --------- | --------------- |
| `device_type`    | 0     | 8         | **8** (configurable) |
| Heartbeat        | 3 s   | 30 s      | **3 s** (configurable) |
| `requestrev`     | int   | string    | **string** in/out, parser accepts both |

EW 7 closes the TCP socket within milliseconds of pairing if heartbeats don't
arrive quickly enough — 30 s (Companion's default) is too slow. We default to
3 s (matching ew2vm). If you see clean pairings followed by immediate `read
loop ended (server likely closed connection)` lines, drop the interval further
or check that heartbeats are reaching EW at all.

If your EasyWorship version pairs but never sends `status` updates, try
`DeviceType=0`. Reports welcome.

## Credits

This project would not exist without the protocol reverse-engineering done by:

- [mikenor/ew2vm](https://github.com/mikenor/ew2vm) (Python, EW → vMix)
- [bitfocus/companion-module-softouch-easyworship](https://github.com/bitfocus/companion-module-softouch-easyworship) (Bitfocus Companion module)

## Legal & disclaimer

ew2mqtt is an independent, unofficial project. It is **not affiliated with,
authorized, sponsored, or endorsed by Softouch Development, Inc.** or any
EasyWorship entity. "EasyWorship" and any related marks are the property of
their respective owners and are used here only nominatively, to describe what
this software interoperates with.

The `ezwremote` protocol is undocumented; ew2mqtt's implementation was derived
**solely to achieve interoperability** between an independently created program
and EasyWorship — the kind of interoperability use protected under the EEA
Software Directive (2009/24/EC, as implemented in Norwegian *åndsverkloven*
§ 41). This repository contains **no EasyWorship source code, binaries, or
assets**; the protocol notes and tests describe functional wire-format facts
only. ew2mqtt is provided "as is", without warranty, as set out in the
[license](LICENSE).

## License

[MIT](LICENSE) — Copyright (c) 2026 Pilgrim Softworks AS
