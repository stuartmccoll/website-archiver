# Website Archiver

A small .NET console app that takes a snapshot of a website and stores it in Azure Blob Storage. It starts at the homepage and follows every link within the site. It saves each page's HTML and the site's stylesheets under a folder for the date of the run, so each run adds a new snapshot.

It's built to archive [stuartmccoll.github.io](https://stuartmccoll.github.io/) after each deployment, but it works with any site.

## How it works

- **What it crawls:** it starts at `DOMAIN` and follows every `<a href>` on the same scheme and host, beneath `DOMAIN`'s path. Relative links are resolved against the page they appear on, using the address the page was actually served from after any redirects. Query strings and `#fragments` are ignored, so each page is fetched once.
- **What it stores:**
  - HTML pages, as `<date>/<path>.html`, e.g. `09112026/posts/my-post.html`. The homepage is stored as `main.html`.
  - Stylesheets linked from the homepage.
- **What it skips:**
  - off-site links and stylesheets
  - `mailto:`, `tel:` and `javascript:` links
  - `.xml` files, such as feeds
- **Failures:** a page or stylesheet that can't be fetched, or returns a status code other than 2xx, is logged and skipped, and the crawl carries on. If anything was skipped, the run finishes with exit code `1` and lists what failed.

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
