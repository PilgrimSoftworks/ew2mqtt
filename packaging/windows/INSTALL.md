# Windows Service install

`ew2mqtt.exe` is built against `Microsoft.Extensions.Hosting.WindowsServices`, so it
runs equally well as a console app and as a Windows Service. To install as a service:

```pwsh
# Place ew2mqtt.exe and appsettings.json under C:\Program Files\ew2mqtt
sc.exe create ew2mqtt `
    binPath= "C:\Program Files\ew2mqtt\ew2mqtt.exe" `
    DisplayName= "EasyWorship to MQTT bridge" `
    start= auto

# Crash recovery: restart after 5s twice, then 30s
sc.exe failure ew2mqtt `
    reset= 86400 `
    actions= restart/5000/restart/5000/restart/30000

sc.exe start ew2mqtt
```

## Configuration

The service reads `appsettings.json` from its working directory. Override with
environment variables (set via `sc.exe config ew2mqtt env=...` is not supported —
use `setx /M` or the `Environment` registry key under
`HKLM\SYSTEM\CurrentControlSet\Services\ew2mqtt`):

```pwsh
[Environment]::SetEnvironmentVariable(
    "Ew2Mqtt__Mqtt__Server",
    "mqtt://broker.lan:1883",
    "Machine")
```

## Logs

Logs go to the Windows Event Log (Application source `ew2mqtt`) when running as a
service. To redirect to a file, configure `Logging:File` in `appsettings.json` or
attach a tail-friendly logger.

## Uninstall

```pwsh
sc.exe stop ew2mqtt
sc.exe delete ew2mqtt
```

## Code signing (TODO)

The published `ew2mqtt.exe` is currently unsigned. Authenticode signing requires a
code-signing certificate; we plan to wire `signtool` into the release pipeline once
a cert is available. PRs welcome.
