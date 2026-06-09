# Language

User-facing copy and code comments are written in **en-US English**. Use
American spelling (color, behavior, center, synthesize, organize, analyze,
parameterize). Do not mix in British forms (colour, behaviour, centre, etc.).

Time is **24-hour** in English copy too — write `09:00`, `22:00`, never
`9 AM` / `10 PM`. The status bar and timestamps use `HH:MM`; prose stays
consistent with it.

**Exception:** shipped entries in `Huginn/ReleaseNotes.txt` are frozen — do
not retroactively rewrite their language. Only the rules above apply to new
entries added on top.

# Versioning

Version is single-sourced from `Huginn/PackageVersion.txt`. The csproj reads
it into `<Version>`, GitHub Actions reads it to tag the release, and the
running app reads it via `AssemblyInformationalVersionAttribute`. **Every PR
that changes code under `Huginn/` must bump `PackageVersion.txt` and add a
matching `v.<version>` section at the top of `Huginn/ReleaseNotes.txt`** —
the `version-guard` workflow enforces this on PR.
