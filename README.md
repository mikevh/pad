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

`infra/main.bicep` defines the Azure resources. The app is published with `npm --prefix web run build` then `dotnet publish src/Pad.Api` and zip-deployed. Do not deploy before passkey auth (Milestone 5) lands: the API has no login yet.
