# pad

Notes and files in a VS Code-style web UI, stored in Azure Blob Storage. See [SPEC.md](SPEC.md).

## Local development

Needs .NET 10 and Node. Storage is emulated with Azurite (no Azure account needed).

```bash
npx azurite --silent --skipApiVersionCheck --location /tmp/azurite &   # blob emulator
npm --prefix web install && npm --prefix web run build                  # builds UI into src/Pad.Api/wwwroot
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Pad.Api --urls http://localhost:5080
```

Open http://localhost:5080. For UI hot reload run `npm --prefix web run dev` (proxies `/api` to :5080).

## Tests

```bash
dotnet test                                                      # in-memory store + HTTP tests
PAD_TEST_BLOB_CONNECTION="UseDevelopmentStorage=true" dotnet test   # also runs the blob store against Azurite
```

## Deploy

`infra/main.bicep` defines the Azure resources (already deployed). To ship a new version, sign in once with `az login`, then:

```bash
scripts/deploy.sh                # build UI, run tests, publish, zip-deploy to Azure, health check
scripts/deploy.sh --skip-tests   # same without the test run
```

GitHub Actions (`.github/workflows/ci.yml`) only builds and tests on push and pull requests. It has no access to Azure; deploying is always a deliberate, local step.
