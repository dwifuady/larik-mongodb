# Larik MongoDB add-on

A MongoDB provider for [Larik SQL Studio](https://github.com/dwifuady/larik-winui). The editor
surface is T-SQL; the provider translates `SELECT` / `INSERT` / `UPDATE` / `DELETE` into MongoDB
commands and passes a small `db.collection.method(...)` mongosh subset through to the driver.

This repository is the add-on's home. It consumes the `Larik.Data` contract package, not a project
reference, so it can be released independently of the app.

## Connect

**Atlas / `mongodb+srv`**: paste the URI the cluster gives you into **Connection string** and connect.
The field is declared as a password, so the app keeps it in Windows Credential Manager rather than the
workspace file, and the provider builds the client with `MongoClientSettings.FromUrl` — SRV discovery,
TLS, `authSource`, `replicaSet` and `retryWrites` all come from the URI. A URI wins over the discrete
host/port fields; User and Password still apply when the URI has no user info.

**Direct `mongod`**: leave the connection string empty and use Host (`db.example.com` or
`db.example.com:27017`, the inline port wins over Port), Database, User and Password. The Auth source,
Port, Direct connection and timeouts sit under Advanced.

A private-link cluster (SRV targets are `vpce-…amazonaws.com` names resolving to `172.16.0.0/12`
addresses) only answers from inside the VPC/VPN; a timeout there is the network, not the driver.

## Build and test

```powershell
dotnet restore
dotnet test tests\Larik.Data.MongoDB.Tests\Larik.Data.MongoDB.Tests.csproj
```

The integration test is a no-op unless a `mongod` answers on `localhost:27017`.

`Larik.Data` 1.2.0 carries the contract the read gate implements. Until that package is on nuget.org,
restore against the feed the app repository packs it into (or any folder containing the `.nupkg`):

```powershell
dotnet restore -s <local-feed> -s https://api.nuget.org/v3/index.json
```

## Package

```powershell
dotnet restore
pwsh ./scripts/package.ps1 -Version 1.0.0 -Rid win-x64
```

The script publishes the provider, removes the host's `Larik.Data.dll`, adds `larik-addon.json`,
`LICENSE` and `THIRD-PARTY-NOTICES.md`, and writes `out/larik.mongodb-<version>-<rid>.zip`.

## Release

Push a `v*` tag (for example `v1.0.0`). The workflow runs the tests, packages `win-x64` and
`win-arm64`, and attaches both zips plus `SHA256SUMS.txt` to a GitHub Release.

After the release, add its URL and SHA-256 to the
[`larik-addons`](https://github.com/dwifuady/larik-addons) index.

## Agent access (MCP)

The provider implements `IReadOnlyStatementPolicy`, so a workspace exposed to an agent as **Read
only** runs reads instead of refusing everything: a `SELECT` the translator compiles to a pipeline,
a `USE`, or one mongosh call on the read allow-list (`find`, `findOne`, `aggregate`, `count`,
`countDocuments`, `estimatedDocumentCount`, `distinct`, `getIndexes`, `getCollectionNames`, `stats`).

The gate is fail-closed. `INSERT` / `UPDATE` / `DELETE`, `insertOne` / `updateMany` /
`deleteMany`, `runCommand`, chained calls and any batch mixing a read with something else are
refused, and `aggregate` is refused when it carries a writing stage (`$out`, `$merge`). Write
workspaces still go through the studio's approval dialog as usual.

## Contract

Built against `Larik.Data` 1.2.0 and declares `>=1.2 <2.0` in `addon/larik-addon.json`. The app
binds the contract to its own assembly, so the add-on must not ship `Larik.Data.dll`.

The provider contract and the add-on format are documented in
[`docs/ADDONS.md`](https://github.com/dwifuady/larik-winui/blob/main/docs/ADDONS.md).
