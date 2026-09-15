# Huginn

A small, cross-platform desktop monitor for the things that need you now:
pull requests waiting on your review, builds that have failed, app crashes,
and services that are failing, slow or silent. Desktop toasts and a taskbar
badge make them hard to miss, and it can answer an AI coding agent asking
what is currently broken.

Each source is independent. Configure one and the rest stay out of the way.

Built on [Avalonia](https://avaloniaui.net/) and .NET 10.

## Features

- **Pull request monitoring**: your assigned reviews, your own PRs, and
  status-by-priority sections: failed validation, missing reviewers,
  autocomplete off, awaiting review, plus a separate group for your active
  PRs and one for items you've acknowledged.
- **Build failure monitoring**: your personal build failures + any pipelines
  you choose to watch. Retrying builds are de-escalated to a warning section
  while the rerun is in flight.
- **Crash monitoring** (Sentry): issues are raised either because they
  changed (new, returned after being resolved, or outgrew a mute) or because
  they are big, measured by how many people they reach. Each carries a
  sparkline of the last 24 hours and a phrase for its shape, so a constant
  drip reads differently from a single burst.
- **Service monitoring** (Azure Application Insights): failing routes, slow
  routes at the 95th percentile, failing dependencies, and resources that
  have gone silent. Ordered by severity rather than raw magnitude, and
  failing routes name the result code. Opening one lands in the portal on the
  query behind it: the exceptions and their stack traces for a failing
  route, percentiles over the window for a slow one.
- **Honest empty panes**: every pane says when it was last checked, and a
  pane whose source cannot answer says so instead of looking calm. An empty
  list from a broken source is the one thing a monitor must not get wrong.
- **Desktop notifications**: one per source per check, summarised. Ten new
  items produce one toast naming the count and what is in it, not ten toasts.
- **Taskbar / dock badge**: count of items needing attention, with
  acknowledged and approved items excluded.
- **Launch at login**: Windows registry `Run` key on Windows, LaunchAgent on
  macOS.
- **Auto-update** via [Velopack](https://velopack.io) against GitHub
  Releases. Checks at startup and every 30 minutes thereafter; new versions
  download in the background and apply on next launch, or you can install
  immediately from the **Updates** section in Settings.

## Installing

Grab the latest installer from the
[Releases page](https://github.com/malforge/Huginn/releases/latest).

### Windows

1. Download the file ending in `win-Setup.exe`.
2. Run it. Windows SmartScreen will warn that the app is from an unknown
   publisher, which is expected, because Huginn isn't code-signed (it's a
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
   signed with an Apple Developer ID, which is expected, for the same reason
   as Windows above. In the warning, click **Done** (or **Cancel**), **not
   "Move to Trash"**, which deletes the installer you just downloaded.
3. Open **System Settings → Privacy & Security** and scroll down to the
   **Security** section. You'll see a note that Huginn was blocked, with an
   **Open Anyway** button: click it, authenticate, then confirm **Open** in
   the dialog. Follow the installer prompts.

Once installed, future updates download and apply themselves the next time
you launch the app, and you don't need to repeat any of this.

## First-time setup

When you first launch Huginn, the Settings panel opens automatically.

Each connection is separate, and you only need the ones you want. Every
credential below is stored in Windows Credential Manager (DPAPI-encrypted) or
the macOS Keychain, never in a settings file.

### Azure DevOps

1. **Organization & Project**: the values from your DevOps URL
   `https://dev.azure.com/{Organization}/{Project}`.
2. **Personal Access Token**: click "Open the token settings page ↗" in
   Settings. Create a token with **Code → Read** and **Build → Read** scopes,
   then paste it.
3. **Test connection**, then pick any pipelines you want watched.

### Sentry

1. **Organization**: your Sentry organisation slug.
2. **Region URL**: EU-resident organisations answer on a regional host rather
   than `sentry.io`; the org settings page shows which region yours is in.
3. **Auth token**: click "Open the auth tokens page ↗". Read scopes are
   enough: `org:read`, `project:read`, `event:read`.
4. **Test connection**, then tick the projects to watch. Nothing is watched
   until you choose something.

### Application Insights

1. **Sign in** with your own Azure account. Sign-in opens your system browser
   and waits for it to come back, so finish it there.
2. Huginn then lists only the resources you personally have access to, so
   nothing here is shared between people. Tick the ones to watch.

Settings are saved when you close the panel or the window, so an edit
interrupted by a restart is not thrown away.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
./deploy.ps1
```

This produces a self-contained, trimmed single-file executable under
`./deploy/`. End-user installs should use the GitHub Releases artifacts
above, because they bundle Velopack so auto-updates work; the `deploy.ps1` output
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
      "command": "C:\\Users\\you\\AppData\\Local\\Huginn\\current\\Huginn.exe",
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
| `huginn_investigate` | Why one finding is happening, over the window Huginn measured. |
| `huginn_pull_requests` | The review queues. |
| `huginn_builds` | Failing and retrying builds. |
| `huginn_refresh` | Asks a running Huginn to poll now, and waits for the result. |

Every answer opens with how old the snapshot is, and says so plainly once it is stale, because an
empty list from a source that stopped answering looks exactly like nothing being wrong.

Reading needs no credentials and opens no port: `--mcp` only reads the file the running app wrote,
and exits when the agent disconnects. `huginn_refresh` and `huginn_investigate` are the two that
talk back, by leaving a request the running app picks up. If Huginn is not running, it says so
rather than pretending.

Set `HUGINN_PROFILE` to keep a second install's settings, credentials and snapshot separate from
the first.

## Keeping internal names out

This repository is public, so nothing in it may name a customer, an employer or their
infrastructure. Two checks enforce that rather than leaving it to care.

Enable the hooks once per clone:

```bash
git config core.hooksPath .githooks
```

They refuse a commit whose **added lines or message** name a protected term, an email address
or something shaped like a credential. Removals are ignored, so taking a leaked term back out
is never blocked.

The list of real names lives in `.git/leak-terms.txt`, which is inside `.git` and so is never
committed; `.githooks/leak-terms.example.txt` documents the format. A deny-list naming the
things you are keeping out of a public history must not itself be published.

`.github/workflows/leak-scan.yml` runs the same script over every pushed commit, because a
local hook is one `--no-verify` away from doing nothing. Give the repository a `LEAK_TERMS`
secret to apply the literal list there too; without it the built-in shapes still apply.

## License

[MIT](LICENSE)
