# QuickJack registration API

A loopback HTTP API for adding commands to the widget from anything on the machine — a
script, a build step, a scheduled job. Register a command and it appears on the palette
within a second.

## Connecting

QuickJack writes two files on first run:

| File | Contents |
|---|---|
| `%APPDATA%\QuickJack\endpoint.json` | `{ "port": 47821, "baseUrl": "...", "tokenPath": "..." }` |
| `%APPDATA%\QuickJack\api-token` | 32 random bytes, base64url, ACL'd to you alone |

The server binds **127.0.0.1 only** and requires the token as a bearer credential.

```bash
QJ_TOKEN=$(cat "$APPDATA/QuickJack/api-token")
curl -s -H "Authorization: Bearer $QJ_TOKEN" http://127.0.0.1:47821/api/health
```

```powershell
$token = (Get-Content "$env:APPDATA\QuickJack\api-token" -Raw).Trim()
$headers = @{ Authorization = "Bearer $token"; "X-QuickJack-Client" = "my-script" }
Invoke-RestMethod http://127.0.0.1:47821/api/health -Headers $headers
```

Send `X-QuickJack-Client: <name>` so the widget can show where a command came from. It shows
up as `registered by my-script` under the command name.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/health` | version, port, command count |
| GET | `/api/commands` | list every command |
| GET | `/api/commands/{id}` | one command |
| POST | `/api/commands` | create or replace (id from body, or generated from the name) |
| PUT | `/api/commands/{id}` | create or replace at a known id |
| DELETE | `/api/commands/{id}` | remove |
| POST | `/api/commands/{id}/run` | start a run, returns `{ runId }` |
| GET | `/api/runs/{runId}` | state, exit code, buffered output |
| GET | `/api/runs/{runId}/stream` | live output as server-sent events |

## Registering a command

```bash
curl -X POST http://127.0.0.1:47821/api/commands \
  -H "Authorization: Bearer $QJ_TOKEN" \
  -H "X-QuickJack-Client: deploy-scripts" \
  -H "Content-Type: application/json" \
  -d '{
        "name": "Restart IIS",
        "icon": "R",
        "group": "Web",
        "shell": "cmd",
        "script": "iisreset",
        "elevation": "uac"
      }'
```

### Fields

| Field | Default | Notes |
|---|---|---|
| `name` | **required** | shown on the button |
| `script` | **required** | multi-line is fine; 64 KB max |
| `id` | from `name` | lowercase slug, e.g. `restart-iis` |
| `shell` | `powershell` | `powershell` / `ps`, `pwsh`, `cmd` |
| `elevation` | `none` | `none`, `uac` / `admin` — see below |
| `output` | `capture` | `capture` inline, or `interactive` for a real console window |
| `description`, `icon`, `group` | — | display only |
| `workingDirectory` | — | ignored if it does not exist |
| `timeoutSeconds` | `120` | `0` means no timeout |
| `confirmBeforeRun` | `false` | asks before every run |
| `parameters` | `[]` | see below |

## Elevation — what the API may and may not do

| Value | Effect | Allowed from the API? |
|---|---|---|
| `none` | runs as you, no prompt | yes |
| `uac` | runs elevated, **Windows consent dialog every time** | yes |
| `agent` | runs elevated with **no prompt** | **never — 403** |

`agent` commands can only be pinned from the widget, which itself requires administrator.
This is deliberate and there is no flag that relaxes it: an API that could mint no-prompt
admin commands would make QuickJack a privilege-escalation service for anything that can
reach a loopback port.

## Approval

By default a command registered through the API arrives **unapproved**. It shows on the
palette greyed with a `new` badge and will not run — through the API or a click — until you
approve it once in the widget. This is what stops a rogue script planting a button you
fat-finger.

```bash
curl -X POST .../api/commands/disk-free/run -H "Authorization: Bearer $QJ_TOKEN" -d '{}'
# 409  {"error":"'Disk free' has not been approved yet. Approve it in the widget first."}
```

Changing an approved command's script, shell, elevation or working directory revokes its
approval — otherwise a caller could register something innocuous, wait, then swap the script.

Turn the gate off with `"requireApprovalForApiCommands": false` in
`%APPDATA%\QuickJack\settings.json`.

## Parameters

Parameters are collected in the widget before the command runs, and reach the script as
environment variables named `QJ_<NAME>`:

```json
{
  "name": "Ping a host",
  "shell": "ps",
  "script": "ping -n 4 $env:QJ_HOST",
  "parameters": [{ "name": "host", "label": "Hostname", "required": true }]
}
```

**In cmd, read parameters as `!QJ_HOST!`, not `%QJ_HOST%`.** cmd expands `%VAR%` before
parsing the line, so a value containing `&` or `|` would execute as a second command. The
`%`-form is rejected at registration with a message pointing at the fix.

### Inline substitution

You can splice a value into the script with `{{name}}`, but only if the parameter declares a
`pattern` the value must match:

```json
{
  "script": "ping -n 4 {{host}}",
  "parameters": [{ "name": "host", "pattern": "[A-Za-z0-9.\\-]+" }]
}
```

Without a pattern this is refused. Values are also rejected if they contain control
characters, so a permissive pattern cannot smuggle in a newline. Prefer the environment
variable form — it needs no validation because nothing is spliced into code.

## Running and watching

```bash
RUN=$(curl -s -X POST http://127.0.0.1:47821/api/commands/restart-iis/run \
      -H "Authorization: Bearer $QJ_TOKEN" -d '{}' | jq -r .runId)

curl -N -H "Authorization: Bearer $QJ_TOKEN" \
     "http://127.0.0.1:47821/api/runs/$RUN/stream"
```

```
data: {"stream":"StdOut","text":"Attempting stop...","at":"..."}
data: {"stream":"StdOut","text":"Internet services successfully restarted","at":"..."}
event: done
data: {"state":"Succeeded","exitCode":0}
```

Pass arguments for parameterised commands:

```bash
curl -X POST http://127.0.0.1:47821/api/commands/ping-a-host/run \
  -H "Authorization: Bearer $QJ_TOKEN" -H "Content-Type: application/json" \
  -d '{"arguments":{"host":"example.com"}}'
```

Runs go through the widget, not around it — so an API-triggered command shows in the same
output panel and obeys the same approval and elevation checks as a click.

## Status codes

| Code | Meaning |
|---|---|
| 401 | missing or wrong token |
| 403 | `elevation: "agent"`, an `Origin` header, a non-loopback `Host`, or a pinned command |
| 400 | malformed request, unknown shell, or a script that fails validation |
| 409 | command not approved yet, or the 200-command cap reached |
| 413 | script over 64 KB |

## Why `Origin` is refused

A web page cannot read the token file, but it *can* be pointed at this port by DNS
rebinding — which is exactly what defeats "it only listens on loopback". A real client has
no reason to send an `Origin` header, so its presence means a browser is calling, and the
request is refused. The `Host` header must also be a loopback name.

## Limits

- 200 commands
- 64 KB per script
- 16 parameters per command
- 256 KB request body
