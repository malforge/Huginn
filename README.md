# Huginn

A small, cross-platform desktop monitor for Azure DevOps — surfaces pull
requests that need your review and pipeline builds that have failed, with
desktop toasts and a taskbar badge so they're hard to miss.

Built on [Avalonia](https://avaloniaui.net/) and .NET 10.

## Features

- **Pull request monitoring** — your assigned reviews, your own PRs, and
  status-by-priority sections: failed validation, missing reviewers,
  autocomplete off, awaiting review, plus a separate group for your active
  PRs and one for items you've acknowledged.
- **Build failure monitoring** — your personal build failures + any pipelines
  you choose to watch. Retrying builds are de-escalated to a warning section
  while the rerun is in flight.
- **Desktop notifications** — toasts for new review-PRs and new build
  failures.
- **Taskbar / dock badge** — count of items needing attention, with
  acknowledged and approved items excluded.
- **Launch at login** — Windows registry `Run` key on Windows, LaunchAgent on
  macOS.
- **Auto-update** via [Velopack](https://velopack.io) against GitHub
  Releases. Checks at startup and every 30 minutes thereafter; new versions
  download in the background and apply on next launch — or you can install
  immediately from the **Updates** section in Settings.

## Installing

Grab the latest installer from the
[Releases page](https://github.com/malforge/Huginn/releases/latest).

### Windows

1. Download the file ending in `win-Setup.exe`.
2. Run it. Windows SmartScreen will warn that the app is from an unknown
   publisher — that's expected, because Huginn isn't code-signed (it's a
   free, open-source personal project, and code-signing certificates cost
   real money). Click **More info**, then **Run anyway**.
3. The installer runs and Huginn starts.

### macOS

1. Download the installer for your Mac:
   - **Apple Silicon** (M1, M2, M3, M4): the file with `osx-arm64` in the name.
   - **Intel**: the file with `osx-x64` in the name.

   Not sure which you have? Click the Apple menu → **About This Mac** and
   look at the **Chip** or **Processor** line.

2. Open the downloaded `.pkg`. macOS will block it because the app isn't
   signed with an Apple Developer ID — that's expected, for the same reason
   as Windows above. In the warning, click **Done** (or **Cancel**) — **not
   "Move to Trash"**, which deletes the installer you just downloaded.
3. Open **System Settings → Privacy & Security** and scroll down to the
   **Security** section. You'll see a note that Huginn was blocked, with an
   **Open Anyway** button — click it, authenticate, then confirm **Open** in
   the dialog. Follow the installer prompts.

Once installed, future updates download and apply themselves the next time
you launch the app — you don't need to repeat any of this.

## First-time setup

When you first launch Huginn, the Settings panel opens automatically.

1. **Organization & Project** — enter the values from your DevOps URL
   `https://dev.azure.com/{Organization}/{Project}`.
2. **Personal Access Token** — click "Open the token settings page ↗" in
   Settings (the link is generated from your Organization). Create a token
   with **Code → Read** and **Build → Read** scopes, then paste it.
   - Stored securely: Windows Credential Manager (DPAPI-encrypted) on
     Windows, macOS Keychain on macOS.
3. **Test Connection** to verify your credentials work.
4. **Pipeline monitoring** (optional) — pick which pipelines to watch.
5. **Save & Connect** — Huginn polls every 5 minutes and sends desktop
   notifications for new items.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
./deploy.ps1
```

This produces a self-contained, trimmed single-file executable under
`./deploy/`. End-user installs should use the GitHub Releases artifacts
above — they bundle Velopack so auto-updates work; the `deploy.ps1` output
is for local development only.

## Asking Huginn from an agent

Huginn publishes what the dashboard is showing to `state.json`, beside its settings, after every
poll. It can also serve that over [MCP](https://modelcontextprotocol.io), so a coding agent can ask
what is currently broken instead of being told.

Open **Settings -> Agents** and press **Register with Claude Code**. That writes your own Claude
configuration, for your user rather than the folder Huginn happened to launch from, and it points at
whichever copy of Huginn is running.

The same panel prints everything needed to set it up by hand, for a different agent or when the
button will not do: the command, the `--mcp` argument, the Claude Code one-liner, and the server as
configuration, which is the shape nearly every MCP client takes.

```json
{
  "mcpServers": {
    "huginn": {
      "command": "C:\Users\you\AppData\Local\Huginn\current\Huginn.exe",
      "args": ["--mcp"]
    }
  }
}
```

The tools:

| Tool | Answers |
|---|---|
| `huginn_status` | Everything needing attention right now. Start here. |
| `huginn_crashes` | Sentry issues, worst reach first. |
| `huginn_services` | Application Insights findings, most severe first. |
| `huginn_pull_requests` | The review queues. |
| `huginn_builds` | Failing and retrying builds. |
| `huginn_refresh` | Asks a running Huginn to poll now, and waits for the result. |

Every answer opens with how old the snapshot is, and says so plainly once it is stale, because an
empty list from a source that stopped answering looks exactly like nothing being wrong.

Reading needs no credentials and opens no port: `--mcp` only reads the file the running app wrote,
and exits when the agent disconnects. `huginn_refresh` is the one thing that talks back, by leaving
a request the running app picks up. If Huginn is not running, it says so rather than pretending.

Set `HUGINN_PROFILE` to keep a second install's settings, credentials and snapshot separate from
the first.

## License

[MIT](LICENSE)
