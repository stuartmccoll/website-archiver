# CLAUDE.md

## Project overview

WebsiteArchiver is a .NET console app. It crawls a single website and uploads a dated snapshot of its HTML pages and linked stylesheets to Azure Blob Storage. It runs once and exits; there is no server and no scheduler in this repo.

The owner runs it only against their own personal website, so `DOMAIN` is trusted input. Anyone can still clone the repo and point it at another site, so changes shouldn't assume a particular site's structure, and the crawler should behave sensibly against arbitrary sites (e.g. not crash on unexpected HTML).

## Layout

- `WebsiteArchiver.sln`: solution at the repo root
- `WebsiteArchiver/`: the only project, which also holds the Dockerfile and docker-compose.yml
  - `Program.cs`: entry point. It runs `Archiver.Crawl()` and exits with code 1 if `FailedAddresses` is non-empty, so a partial archive doesn't look like a success
  - `Archiver.cs`: crawling logic (HtmlAgilityPack). It loads `DOMAIN`, uploads its on-site stylesheets and the home page (as `main.html`), then recursively follows `<a href>` links. Every `href` is resolved against the page it appears on with `Uri`, and the query string and fragment are dropped, so each page is fetched once. A link is followed only if it has the same scheme and host as `DOMAIN`, sits under its path, and doesn't end in `.xml`. Fragment-only (`#...`) links are skipped, and the homepage is never fetched twice. Blob names come from the resolved path relative to `DOMAIN`: `.html` is added unless the path already ends in `.html` or `.htm`, so `python-3.7-data-classes/` becomes `python-3.7-data-classes.html`. If a page redirects, as GitHub Pages does from `/post` to `/post/`, it's stored, deduplicated and link-resolved under the address it redirected to. A page or stylesheet that can't be fetched, or returns a non-2xx status, is logged to stderr, added to `FailedAddresses` and skipped, and the crawl carries on. Only a failure to fetch the homepage throws
  - `AzureBlobStorageClient.cs`: writes each file to a temp file next to the assembly, uploads it, then deletes the temp file. Blob names are prefixed with the date in `ddMMyyyy` format, e.g. `09112026/blog/post.html`, so each run is a new snapshot
- `WebsiteArchiver.Tests/`: xUnit end-to-end tests (see Testing)
- `.github/workflows/pull-request-checks.yml`: on pull requests to `main`, checks formatting (`dotnet format --verify-no-changes`), runs `dotnet test`, and builds the Docker image without pushing it. It can also be run manually
- `.github/dependabot.yml`: weekly update pull requests for NuGet, the Dockerfile's base images, the images pinned in `docker-compose.local.yml`, and GitHub Actions. Minor and patch updates are grouped, and new versions wait a 7-day cooldown
- `.claude/settings.json`: project permission allowlist for Claude Code. It pre-approves `dotnet build`, `dotnet test`, `dotnet list package` and `xxd -p`
- `README.md`: overview for people browsing the repo. Keep it in step with behaviour changes
- `.github/workflows/publish-image.yml`: on every push to `main`, runs the tests, then builds the image and pushes it to `ghcr.io/stuartmccoll/websitearchiver`, tagged with the commit SHA and `latest`. It authenticates with `GITHUB_TOKEN`, so it needs no secrets. The image is run by the archival workflow in the `stuartmccoll/stuartmccoll.github.io` repo

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

- `Fakes/FakeWebsite.cs` serves fixture pages. Page bodies can use the `{{DOMAIN}}` placeholder.
- `Fakes/FakeBlobStorage.cs` implements just enough of the Put Blob REST API to record uploads. The SDK is pointed at it through a connection string with an explicit `BlobEndpoint`.

How the tests are organised:

- `ArchiverTests` pins the current behaviour. If you knowingly keep a quirk, start the test's name with `Quirk_`. The purpose of these tests is to catch regressions after dependency upgrades. If a change is meant to alter behaviour, update the test in the same change.
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
