# Larik MongoDB add-on

A MongoDB provider for [Larik SQL Studio](https://github.com/dwifuady/larik-winui). The editor
surface is T-SQL; the provider translates `SELECT` / `INSERT` / `UPDATE` / `DELETE` into MongoDB
commands and passes a small `db.collection.method(...)` mongosh subset through to the driver.

This repository is the add-on's home. It consumes the `Larik.Data` contract package, not a project
reference, so it can be released independently of the app.

## Build and test

```powershell
dotnet restore -r win-x64
dotnet test tests\Larik.Data.MongoDB.Tests\Larik.Data.MongoDB.Tests.csproj
```

The integration test is a no-op unless a `mongod` answers on `localhost:27017`.

## Package

```powershell
dotnet restore -r win-x64
pwsh ./scripts/package.ps1 -Version 1.0.0 -Rid win-x64
```

The script publishes the provider, removes the host's `Larik.Data.dll`, adds `larik-addon.json`,
`LICENSE` and `THIRD-PARTY-NOTICES.md`, and writes `out/larik.mongodb-<version>-<rid>.zip`.

## Release

Push a `v*` tag (for example `v1.0.0`). The workflow runs the tests, packages `win-x64` and
`win-arm64`, and attaches both zips plus `SHA256SUMS.txt` to a GitHub Release.

After the release, add its URL and SHA-256 to the
[`larik-addons`](https://github.com/dwifuady/larik-addons) index.

## Contract

Built against `Larik.Data` 1.0.0 and declares `>=1.0 <2.0` in `addon/larik-addon.json`. The app
binds the contract to its own assembly, so the add-on must not ship `Larik.Data.dll`.

The provider contract and the add-on format are documented in
[`docs/ADDONS.md`](https://github.com/dwifuady/larik-winui/blob/main/docs/ADDONS.md).
