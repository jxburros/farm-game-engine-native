# Security policy

## Supported versions

Only the latest release gets security fixes. The app updates itself through
**Help → Update Center**, so staying current is one click.

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub's
[private vulnerability reporting](https://github.com/jxburros/farm-game-engine-native/security/advisories/new)
rather than in a public issue. Include the version (Help → About Farming RPG
Maker), what you did, and what happened; a project file, content pack or
cartridge that shows the problem helps most.

Areas where a report matters most:

- **Content packs and plugins.** Pack plugins run in a QuickJS-in-WebAssembly
  sandbox with fuel budgets and declared capabilities
  ([docs/PLUGINS.md](docs/PLUGINS.md)); anything that escapes the sandbox, its
  budgets or its permissions.
- **Opening files.** Projects, packs, images, cartridges and saves from other
  people: crashes, hangs, or writes outside the expected folders.
- **Export Game and exported games.** Player templates, archives, and the web
  demo page's Content-Security-Policy ([docs/EXPORT.md](docs/EXPORT.md)).
- **Updates.** The Update Center downloads releases from GitHub through
  Velopack ([docs/RELEASING.md](docs/RELEASING.md)).

Fixes ship as a normal release and are named in the
[changelog](CHANGELOG.md).
