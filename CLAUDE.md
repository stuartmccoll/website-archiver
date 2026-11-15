# CLAUDE.md

## Project overview

WebsiteArchiver is a .NET console app. It crawls a single website and uploads a timestamped snapshot of its pages and every on-site file they use (stylesheets, images, scripts, icons and so on) to Azure Blob Storage. It runs once and exits; there is no server and no scheduler in this repo.

The owner runs it only against their own personal website, so `DOMAIN` is trusted input. Anyone can still clone the repo and point it at another site, so changes shouldn't assume a particular site's structure, and the crawler should behave sensibly against arbitrary sites (e.g. not crash on unexpected HTML).

## Layout

- `WebsiteArchiver.sln`: solution at the repo root
- `WebsiteArchiver/`: the only project, which also holds the Dockerfile and docker-compose.yml
  - `Program.cs`: entry point. It runs `Archiver.Crawl()` and exits with code 1 if `FailedAddresses` is non-empty, so a partial archive doesn't look like a success. The partial snapshot is still published, marked incomplete
  - `Archiver.cs`: crawling logic. It fetches everything with one `HttpClient`, which follows redirects, and holds what it captures in memory until the crawl ends. The homepage's final address after redirects (its folder) becomes the site root, so an `http` `DOMAIN` that redirects to `https` still works. Each response is handled by its `Content-Type`: HTML beneath the root is parsed with HtmlAgilityPack for links and the files it uses, CSS is read for `url(…)` and `@import`, a web app manifest is read for `icons[].src`, and everything else is stored as-is. Links to pages (`<a>`, `<area>`) are followed only beneath the root. Files a page or stylesheet uses are followed anywhere on the same scheme and host. Every reference is resolved against its page (or `<base href>`) or stylesheet, with the query string and fragment dropped, so each file is fetched once. `<link>` elements with other `rel` values, such as `canonical`, are rewritten but never followed. If a request redirects, as GitHub Pages does from `/post` to `/post/`, the file is stored, deduplicated and link-resolved under the address it redirected to, and the redirect is recorded. A file that can't be fetched, or returns a non-2xx status, is logged to stderr, added to `FailedAddresses` and skipped, and the crawl carries on. Only a failure to sign in to storage, which happens before crawling, or to fetch the homepage throws. Once the crawl ends, `LinkRewriter` rewrites every HTML, CSS and web manifest file (see Snapshot layout). If a file can't be rewritten, it's logged and its original goes in `site/`
  - `References.cs`: finds the addresses in HTML, CSS and web manifests, and resolves them. Each format has one `Map…` walker that takes a `ReferenceMapper`. Finding references uses a mapper that records each one and returns it unchanged, and rewriting uses one that returns the new value, so the two always agree on where references are. Edits are spliced into the source by position (`TextEdit`), so the rest of a file is left exactly as it was
  - `LinkRewriter.cs`: rewrites references in the `site/` copy, given every captured address's path, including redirects and addresses whose path was already taken
  - `TextDecoding.cs`: decodes HTML, CSS and manifests. The encoding comes from the BOM, then the `Content-Type` charset, then `<meta charset>` (HTML) or `@charset` (CSS), and otherwise UTF-8. Labels such as `iso-8859-1` are read as windows-1252, as browsers do
  - `SnapshotPaths.cs`: maps an address to its path in a snapshot. Paths mirror the URL from the host's root and are percent-decoded: `/posts/x/` becomes `posts/x/index.html`, an HTML page at `/about` becomes `about/index.html`, and other files keep their names, e.g. `css/site.css`. If two addresses map to the same path, the first one wins
  - `Snapshot.cs`: the manifest and index formats, and publishing. See Snapshot layout
  - `Viewer.cs`: uploads the viewer, which is embedded in the app as a resource, to `index.html` at the container root with `Cache-Control: no-cache`
  - `viewer/index.html`: the snapshot viewer. One static page in plain HTML, CSS and JavaScript, with no build step, framework or external resources. It lists `snapshots/index.json` newest first (date and time, page count, an incomplete warning) and shows the chosen snapshot in an iframe, under a banner with the archive time, the current page, a snapshot picker, and links to open the page full-screen or see the live version. The address holds the snapshot and page, e.g. `#2026-11-09T14-05-00Z/posts/x/`. The page is the URL path, so `posts/x/index.html` appears as `posts/x/`. Because it's served from the same address as the snapshots, it reads the frame's location to keep the banner and address in step (with `replaceState`, so the back button isn't doubled), and makes links that leave the snapshot open in a new tab. It reads each snapshot's manifest to find live URLs and to check that a page exists; an address for a page that isn't in the snapshot shows its homepage, with a notice. It's uploaded on every run, so changes to the index or manifest format must keep it in step
  - `AzureBlobStorageClient.cs`: connects with either a connection string or an endpoint plus `WorkloadIdentityCredential` (see Configuration), and reports a missing or conflicting setting as an `InvalidOperationException`. `SignIn()` fetches the token before the crawl, so the workflow's short-lived GitHub OIDC token is exchanged while it's still valid, and the cached Azure token is reused by every upload. It uploads blobs with their `Content-Type`, and updates a blob with an optimistic-concurrency loop: read with its ETag, write with `If-Match` (or `If-None-Match: *` if it didn't exist), and retry from a fresh read on 412 or 409
- `WebsiteArchiver.Tests/`: xUnit end-to-end tests (see Testing)
- `infra/`: Bicep for the Azure infrastructure, deployed by hand at subscription scope (see `infra/README.md`). `main.bicep` creates the `website-archiver` resource group, and `modules/archive.bicep` creates a storage account with shared-key access turned off, plus a user-assigned managed identity that trusts the website repo's archival workflow through GitHub OIDC and holds Storage Blob Data Contributor on the account. Static website hosting is turned on afterwards with one CLI command, because Bicep can't set it
- `.github/workflows/pull-request-checks.yml`: on pull requests to `main`, checks formatting (`dotnet format --verify-no-changes`), runs `dotnet test`, compiles the Bicep, and builds the Docker image without pushing it. It can also be run manually
- `.github/dependabot.yml`: weekly update pull requests for NuGet, the Dockerfile's base images, the images pinned in `docker-compose.local.yml`, and GitHub Actions. Minor and patch updates are grouped, and new versions wait a 7-day cooldown
- `.claude/settings.json`: project permission allowlist for Claude Code. It pre-approves `dotnet build`, `dotnet test`, `dotnet list package` and `xxd -p`
- `README.md`: overview for people browsing the repo. Keep it in step with behaviour changes
- `.github/workflows/publish-image.yml`: on every push to `main`, runs the tests, then builds the image and pushes it to `ghcr.io/stuartmccoll/websitearchiver`, tagged with the commit SHA and `latest`. It authenticates with `GITHUB_TOKEN`, so it needs no secrets. The image is run by the archival workflow in the `stuartmccoll/stuartmccoll.github.io` repo

## Snapshot layout

Each run publishes one snapshot to the configured container. The container will be `$web` in production, so Azure static website hosting can serve it:

```
index.html                               the viewer, replaced on every run
snapshots/index.json                     every published snapshot, read by the viewer
snapshots/2026-11-10T09-15-00Z/          one folder per run, named after its UTC start time
  manifest.json                          root, status, failed addresses, redirects, and every file's URL, path, type, size and SHA-256
  site/<path>                            every captured file, with links rewritten so it can be browsed
  original/<path>                        HTML, CSS and web manifests, exactly as served
```

- `site/` is self-contained and can be browsed from any address. In HTML, CSS and web manifests, every reference to a captured file becomes a path relative to the file it's in, with each segment percent-encoded and pointing at an explicit file (`about/index.html`, never `about/`). Fragments are kept and query strings dropped. A reference to an address that redirected points at the file it redirected to. Rewritten: `href`/`src` and the other attributes `References` finds, `srcset`/`imagesrcset`, `poster`, `url(…)` and `@import` in `style` attributes, `<style>` blocks and stylesheets, and `icons[].src`. `<base href>` becomes the page's own file name, because every rewritten link is relative to the page
- Absolute references to anything not captured, including every off-site link and scheme-relative `//host/` links, are left unchanged. A relative reference to something not captured (a failed fetch, or a page outside the root) becomes an absolute link to the live site, because it would be broken in the snapshot
- Every HTML, CSS and manifest file in `site/` is re-encoded as UTF-8, even if no links changed. Its blob's `Content-Type` gets `charset=utf-8`, and `<meta charset>`, `<meta http-equiv="Content-Type">` and `@charset` are updated to match. No declaration is added to a page that has none. `original/` keeps the exact bytes, so rewritten copies can be regenerated. The manifest's `contentType`, `size` and `sha256` describe the file as served, i.e. the original
- Before uploading anything, a run claims its folder by creating a placeholder `manifest.json` with `If-None-Match: *`. If another run started in the same second and got there first, `-2`, `-3` and so on are added to the ID, e.g. `2026-11-10T09-15-00Z-2`
- Files are uploaded next, then the real `manifest.json`, then the viewer, and `index.json` last, so a half-uploaded snapshot is never listed, and a newly listed one is never shown by an older viewer. Updates to `index.json` keep existing entries and any properties this version doesn't know about. If `index.json` isn't in the expected format, the run fails rather than overwriting it
- A run with failures is still published, with `"status": "incomplete"` and its `failedAddresses` in both the manifest and the index
- Blobs keep the `Content-Type` they were served with, or `application/octet-stream` if they had none. `index.json` and the viewer are served with `Cache-Control: no-cache`

## Configuration

All configuration comes from environment variables, read with `Environment.GetEnvironmentVariable`:

| Variable | Purpose |
|---|---|
| `DOMAIN` | Root URL to crawl, e.g. `https://example.com/`. A trailing `/` is added if it's missing. The `Archiver` constructor throws `InvalidOperationException` if it's unset or isn't an http(s) URL |
| `AZURE_BLOB_STORAGE_CONNECTION_STRING` | Connection string for the storage account (use `UseDevelopmentStorage=true` with the Azurite emulator). Used by the tests and Azurite. Set this or `AZURE_BLOB_STORAGE_ENDPOINT`, not both |
| `AZURE_BLOB_STORAGE_ENDPOINT` | The account's https blob endpoint, e.g. `https://<account>.blob.core.windows.net/`, for signing in with workload identity instead. Production uses this, because the account created by `infra/` has shared-key access turned off. It needs the three variables below |
| `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` | The managed identity's tenant and client ID, from the `infra/` deployment outputs. Read by `WorkloadIdentityCredential` |
| `AZURE_FEDERATED_TOKEN_FILE` | A file holding the archival workflow's GitHub OIDC token, with audience `api://AzureADTokenExchange`, mounted into the container. It must exist when the app starts |
| `AZURE_BLOB_STORAGE_CONTAINER_NAME` | Container the blobs are uploaded to: `$web` in production |

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
docker compose -f docker-compose.local.yml run --rm list-blobs     # show what was uploaded
docker compose -f docker-compose.local.yml run --rm snapshot-url   # print the latest snapshot's address in the viewer
docker compose -f docker-compose.local.yml down                    # remove Azurite and its data
```

Only `DOMAIN` is read from the environment or from `.env`. The connection string is Azurite's public development key, so the real Azure connection string is never used. Azurite's blob port is published on `127.0.0.1:10000`, and the `archive` container allows anonymous reads, so the URL `snapshot-url` prints opens in a browser.

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

- `ArchiverTests` pins the crawling behaviour. `CaptureTests` covers which files are captured, their paths and their content types. `SnapshotTests` covers the manifest, the originals and the index. `RewriteTests` covers link rewriting and re-encoding. Its acceptance test, `EveryOnSiteLink_ResolvesToAFileInTheSnapshot`, serves the snapshot from an unrelated address and checks that every on-site link in every rewritten file resolves to a file in it. Tests that check which page was stored at a path compare `run.OriginalText(path)`, not the rewritten `run.Text(path)`. If you knowingly keep a quirk, start the test's name with `Quirk_`. The purpose of these tests is to catch regressions after dependency upgrades. If a change is meant to alter behaviour, update the test in the same change.
- When you find a new bug, add a test describing the intended behaviour with `[Fact(Skip = "Bug: ...")]`. Confirm it fails for that reason, then remove the `Skip` as part of the fix.
- `ConfigurationTests` covers how the storage variables are checked. The workload identity sign-in itself isn't tested: the SDK only sends tokens over https, and signing in would contact Microsoft Entra ID. Those tests stop at constructing the `Archiver`, which makes no requests
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
