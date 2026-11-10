# CLAUDE.md

## Project overview

WebsiteArchiver is a .NET console app. It crawls a single website and uploads a timestamped snapshot of its pages and every on-site file they use (stylesheets, images, scripts, icons and so on) to Azure Blob Storage. It runs once and exits; there is no server and no scheduler in this repo.

The owner runs it only against their own personal website, so `DOMAIN` is trusted input. Anyone can still clone the repo and point it at another site, so changes shouldn't assume a particular site's structure, and the crawler should behave sensibly against arbitrary sites (e.g. not crash on unexpected HTML).

## Layout

- `WebsiteArchiver.sln`: solution at the repo root
- `WebsiteArchiver/`: the only project, which also holds the Dockerfile and docker-compose.yml
  - `Program.cs`: entry point. It runs `Archiver.Crawl()` and exits with code 1 if `FailedAddresses` is non-empty, so a partial archive doesn't look like a success. The partial snapshot is still published, marked incomplete
  - `Archiver.cs`: crawling logic. It fetches everything with one `HttpClient`, which follows redirects, and holds what it captures in memory until the crawl ends. The homepage's final address after redirects (its folder) becomes the site root, so an `http` `DOMAIN` that redirects to `https` still works. Each response is handled by its `Content-Type`: HTML beneath the root is parsed with HtmlAgilityPack for links and the files it uses, CSS is read for `url(…)` and `@import`, a web app manifest is read for `icons[].src`, and everything else is stored as-is. Links to pages (`<a>`, `<area>`) are followed only beneath the root. Files a page or stylesheet uses are followed anywhere on the same scheme and host. Every reference is resolved against its page (or `<base href>`) or stylesheet, with the query string and fragment dropped, so each file is fetched once. If a request redirects, as GitHub Pages does from `/post` to `/post/`, the file is stored, deduplicated and link-resolved under the address it redirected to, and the redirect is recorded. A file that can't be fetched, or returns a non-2xx status, is logged to stderr, added to `FailedAddresses` and skipped, and the crawl carries on. Only a failure to fetch the homepage throws
  - `References.cs`: finds the addresses in HTML, CSS and web manifests, and resolves them
  - `SnapshotPaths.cs`: maps an address to its path in a snapshot. Paths mirror the URL from the host's root and are percent-decoded: `/posts/x/` becomes `posts/x/index.html`, an HTML page at `/about` becomes `about/index.html`, and other files keep their names, e.g. `css/site.css`. If two addresses map to the same path, the first one wins
  - `Snapshot.cs`: the manifest and index formats, and publishing. See Snapshot layout
  - `AzureBlobStorageClient.cs`: uploads blobs with their `Content-Type`, and updates a blob with an optimistic-concurrency loop: read with its ETag, write with `If-Match` (or `If-None-Match: *` if it didn't exist), and retry from a fresh read on 412 or 409
- `WebsiteArchiver.Tests/`: xUnit end-to-end tests (see Testing)
- `.github/workflows/pull-request-checks.yml`: on pull requests to `main`, checks formatting (`dotnet format --verify-no-changes`), runs `dotnet test`, and builds the Docker image without pushing it. It can also be run manually
- `.github/dependabot.yml`: weekly update pull requests for NuGet, the Dockerfile's base images, the images pinned in `docker-compose.local.yml`, and GitHub Actions. Minor and patch updates are grouped, and new versions wait a 7-day cooldown
- `.claude/settings.json`: project permission allowlist for Claude Code. It pre-approves `dotnet build`, `dotnet test`, `dotnet list package` and `xxd -p`
- `README.md`: overview for people browsing the repo. Keep it in step with behaviour changes
- `.github/workflows/publish-image.yml`: on every push to `main`, runs the tests, then builds the image and pushes it to `ghcr.io/stuartmccoll/websitearchiver`, tagged with the commit SHA and `latest`. It authenticates with `GITHUB_TOKEN`, so it needs no secrets. The image is run by the archival workflow in the `stuartmccoll/stuartmccoll.github.io` repo

## Snapshot layout

Each run publishes one snapshot to the configured container. The container will be `$web` in production, so Azure static website hosting can serve it:

```
snapshots/index.json                     every published snapshot, read by the viewer
snapshots/2026-11-10T09-15-00Z/          one folder per run, named after its UTC start time
  manifest.json                          root, status, failed addresses, redirects, and every file's URL, path, type, size and SHA-256
  site/<path>                            every captured file
  original/<path>                        HTML, CSS and web manifests, unmodified
```

- This is stage 1 of the snapshot viewer plan. Links aren't rewritten yet, so `site/` and `original/` are identical for now. Stage 2 will rewrite links in `site/` so a snapshot can be browsed, and `original/` keeps the exact bytes so the rewritten copies can be regenerated
- Before uploading anything, a run claims its folder by creating a placeholder `manifest.json` with `If-None-Match: *`. If another run started in the same second and got there first, `-2`, `-3` and so on are added to the ID, e.g. `2026-11-10T09-15-00Z-2`
- Files are uploaded next, then the real `manifest.json`, and `index.json` last, so a half-uploaded snapshot is never listed. Updates to `index.json` keep existing entries and any properties this version doesn't know about. If `index.json` isn't in the expected format, the run fails rather than overwriting it
- A run with failures is still published, with `"status": "incomplete"` and its `failedAddresses` in both the manifest and the index
- Blobs keep the `Content-Type` they were served with, or `application/octet-stream` if they had none. `index.json` is served with `Cache-Control: no-cache`

## Configuration

All configuration comes from environment variables, read with `Environment.GetEnvironmentVariable`:

| Variable | Purpose |
|---|---|
| `DOMAIN` | Root URL to crawl, e.g. `https://example.com/`. A trailing `/` is added if it's missing. The `Archiver` constructor throws `InvalidOperationException` if it's unset or isn't an http(s) URL |
| `AZURE_BLOB_STORAGE_CONNECTION_STRING` | Connection string for the storage account (use `UseDevelopmentStorage=true` with the Azurite emulator) |
| `AZURE_BLOB_STORAGE_CONTAINER_NAME` | Container the blobs are uploaded to |

For Docker, these go in `WebsiteArchiver/.env`, which git ignores. Never commit real values. `.env` is optional for `docker compose build`. Whatever runs the image must pass the variables in itself.

## Commands

Run these from `WebsiteArchiver/`:

```sh
dotnet build
dotnet run                          # needs the three env vars set
dotnet publish -c Release -o out
dotnet list package --outdated
dotnet list package --vulnerable --include-transitive
docker compose build                # build context is WebsiteArchiver/
docker compose up                   # reads .env
```

To test the Docker image without Azure, use `docker-compose.local.yml`. It runs the image against Azurite, the local storage emulator. An `azure-cli` container creates the `archive` blob container first, because the app expects it to exist. Run these from `WebsiteArchiver/`:

```sh
DOMAIN=https://example.com/ docker compose -f docker-compose.local.yml run --rm --build websitearchiver
docker compose -f docker-compose.local.yml run --rm list-blobs   # show what was uploaded
docker compose -f docker-compose.local.yml down                  # remove Azurite and its data
```

Only `DOMAIN` is read from the environment or from `.env`. The connection string is Azurite's public development key, so the real Azure connection string is never used.

Run `dotnet test` from the repo root. `global.json` switches `dotnet test` to Microsoft.Testing.Platform, which xUnit v3 requires on the .NET 10 SDK. That means VSTest's `--filter` option doesn't work. Use xUnit's options instead, e.g. `dotnet test --filter-class WebsiteArchiver.Tests.ArchiverTests` or `--filter-method "*Homepage*"`.

Both projects target `net10.0` (the current LTS). The Docker images use `sdk:10.0` and `runtime:10.0`, so keep them on the same major version as `TargetFramework`.

Running the app makes real HTTP requests to `DOMAIN` and real uploads to Azure. Don't run it without the user's agreement, unless it is pointed at Azurite.

## Testing

`WebsiteArchiver.Tests` uses xUnit v3 (the `xunit.v3` package, which needs `OutputType` `Exe`) and references the app project. The tests are black-box: each one runs the real `Archiver.Crawl()`, with no test seams in the production code, against two Kestrel servers on random local ports:

- `Fakes/FakeWebsite.cs` serves fixture pages and files. Text bodies can use the `{{DOMAIN}}` placeholder.
- `Fakes/FakeBlobStorage.cs` implements just enough of the Put Blob and Get Blob REST APIs, including `If-Match` and `If-None-Match` conditions. It records each blob's contents, `Content-Type` and `Cache-Control`. Its `BeforeConditionalPut` hook lets a test change a blob just before a conditional write, as if another run got there first. The SDK is pointed at it through a connection string with an explicit `BlobEndpoint`.
- `ArchiveRun` runs a crawl and reads back the snapshot it published: its `site/` and `original/` files by path, its manifest and the index.
- `TestSites` has the shared helpers for building fixture sites.

How the tests are organised:

- `ArchiverTests` pins the crawling behaviour. `CaptureTests` covers which files are captured, their paths and their content types. `SnapshotTests` covers the manifest, the originals and the index. If you knowingly keep a quirk, start the test's name with `Quirk_`. The purpose of these tests is to catch regressions after dependency upgrades. If a change is meant to alter behaviour, update the test in the same change.
- When you find a new bug, add a test describing the intended behaviour with `[Fact(Skip = "Bug: ...")]`. Confirm it fails for that reason, then remove the `Skip` as part of the fix.
- Environment variables are process-wide, so test parallelisation is disabled for the whole assembly, using `[assembly: Parallelization(Mode = ParallelMode.None)]` in `ArchiverTests.cs`.

## Conventions

- `.editorconfig` defines the C# style: braces on their own lines (Allman), 4-space indents, `this.` on instance members, `System` usings first, block-scoped namespaces, and UTF-8 with BOM for `.cs` files. CI enforces it, so run `dotnet format` before committing C# changes
- Not enforced by tooling: `///` XML doc comments on every method
- `.gitattributes` sets `* text=auto`
- Commit to a branch and open a PR rather than pushing to `main`, because every push to `main` publishes the `latest` image that the website's archival workflow runs

## Docker notes

- `Program.Main` must catch every exception and return an exit code. The app runs as PID 1 in the container, and Linux ignores SIGABRT for PID 1 unless it has a handler, so an unhandled .NET exception leaves the process hanging instead of exiting, unless the container was started with `--init`
- The Dockerfile is multi-stage: `sdk` builds, then a runtime image runs `dotnet ./app/bin/Release/net/WebsiteArchiver.dll`
- The build context is `WebsiteArchiver/`, so `.dockerignore` lives there too. It keeps `bin/`, `obj/` and `.env` out of the image build
