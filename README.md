# Website Archiver

A small .NET console app that takes a snapshot of a website and stores it in Azure Blob Storage. It starts at the homepage, follows every link within the site, and captures every page along with the files they use, such as stylesheets, images, scripts and icons. Each run adds a new snapshot, in its own folder.

It's built to archive [stuartmccoll.github.io](https://stuartmccoll.github.io/) after each deployment, but it works with any site.

## How it works

- **What it crawls:** it starts at `DOMAIN`. If that redirects, for example from `http` to `https`, it uses where it redirects to instead. It follows every `<a href>` on the same scheme and host, beneath that address's path. Relative links are resolved against the page they appear on, using the address the page was actually served from after any redirects. Query strings and `#fragments` are ignored, so each page is fetched once.
- **What it captures:** every page, plus every file on the same host that a page, stylesheet or web app manifest refers to. That includes stylesheets, images (including `srcset`), scripts, favicons and touch icons, the web manifest and its icons, media, and anything referenced with `url(…)` or `@import` in CSS. These files can be anywhere on the host, even outside `DOMAIN`'s path. Each file is stored byte for byte, with the `Content-Type` it was served with, so PDFs, feeds and images are kept as they are.
- **What it skips:**
  - off-site links and files
  - `mailto:`, `tel:`, `javascript:` and `data:` links
- **Failures:** a file that can't be fetched, or returns a status code other than 2xx, is logged and skipped, and the crawl carries on. The snapshot is still published, marked `incomplete` with the addresses that failed, and the run finishes with exit code `1`.

## How snapshots are stored

```
snapshots/index.json                     a list of every snapshot
snapshots/2026-11-10T09-15-00Z/          one folder per run, named after its start time in UTC (with -2 added if two runs start in the same second)
  manifest.json                          what the snapshot contains
  site/index.html                        the homepage
  site/posts/my-post/index.html          https://example.com/posts/my-post/
  site/css/site.css                      https://example.com/css/site.css
  original/…                             unmodified copies of the HTML, CSS and web manifests
```

- Paths mirror the site's URLs, so a page at `/posts/my-post/` is stored as `posts/my-post/index.html`.
- `manifest.json` records when the run started and finished, whether it was complete, any addresses that failed, the redirects it followed, and each file's URL, path, `Content-Type`, size and SHA-256 hash.
- `index.json` is updated last, so a snapshot only appears in it once all its files are uploaded. Two runs at the same time can't overwrite each other's entries.
- Links inside the pages still point at the live site. Rewriting them so a snapshot can be browsed on its own is the next planned change. `original/` keeps the pages exactly as they were fetched, so the rewritten copies can be rebuilt.

## Configuration

Everything is configured through environment variables:

| Variable | Description |
|---|---|
| `DOMAIN` | The site to archive, e.g. `https://example.com/`. Must be an `http` or `https` URL. |
| `AZURE_BLOB_STORAGE_CONNECTION_STRING` | Connection string for the Azure Storage account. |
| `AZURE_BLOB_STORAGE_CONTAINER_NAME` | The blob container to upload to. It must already exist. |

## Running it

### From the published image

Each push to `main` publishes an image to GitHub Container Registry:

```sh
docker run --rm \
  -e DOMAIN=https://example.com/ \
  -e AZURE_BLOB_STORAGE_CONNECTION_STRING="..." \
  -e AZURE_BLOB_STORAGE_CONTAINER_NAME=archives \
  ghcr.io/stuartmccoll/websitearchiver:latest
```

Images are tagged `latest` and with the full commit SHA.

### Locally, without Azure

`WebsiteArchiver/docker-compose.local.yml` builds the image and runs it against [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite), Microsoft's local Azure Storage emulator, so you can try it without an Azure account:

```sh
cd WebsiteArchiver
DOMAIN=https://example.com/ docker compose -f docker-compose.local.yml run --rm --build websitearchiver
docker compose -f docker-compose.local.yml run --rm list-blobs   # list what was archived
docker compose -f docker-compose.local.yml down                  # remove the emulator and its data
```

### With the .NET SDK

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download), set the three environment variables, then:

```sh
cd WebsiteArchiver
dotnet run
```

## Development

```sh
dotnet test      # run the test suite
dotnet format    # apply the code style in .editorconfig
```

The tests are end to end. Each one runs the real archiver against a fake website and a fake Blob Storage endpoint, both hosted in the test process, so they need no network or Azure access.

Every pull request runs the tests, checks formatting and builds the Docker image. Dependabot opens weekly pull requests for outdated NuGet packages, Docker images and GitHub Actions.
