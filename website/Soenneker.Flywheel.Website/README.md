# Flywheel website

The public marketing website for Flywheel at https://flywheel.soenneker.com.

Built with .NET 10, Razor components, and `Soenneker.Quark.Suite`. Quark's Tailwind and Lucide generators produce the styles and inline icons. The site exports to static HTML, CSS, and images; the deployed page does not require a .NET server or a WebAssembly download.

Run these commands from `website/Soenneker.Flywheel.Website` in the suite repository.

## Develop

```powershell
dotnet run --project Soenneker.Flywheel.Website.csproj --urls http://localhost:5173
```

## Build

```powershell
pwsh -File scripts/Export-Site.ps1
```

The script publishes the Razor application, renders all three pages, copies public assets into `out/`, and stops its temporary server. Cloudflare Workers serves that static output using `wrangler.jsonc`.

## Deploy

```powershell
npx wrangler login
pwsh -File scripts/Export-Site.ps1
npx wrangler deploy
```

The Worker is `soenneker-flywheel-website` in the Soenneker Cloudflare account, with custom domain `flywheel.soenneker.com`. Page sources are in `Pages`; navigation and footer are in `Components/Layout/MainLayout.razor`.

## Screenshots

`wwwroot/images/` contains captures of the real Flywheel dashboard with isolated illustrative demo data. Activity counts and logs are demonstration fixtures, not production usage or benchmark results. No production data or credentials are included.

The dashboard and core documentation use the same captures. Refresh them together when the UI changes.

## Continuous deployment

The suite’s `website.yml` workflow builds pull requests and pushes to `main` that change the website or its build configuration. After a successful `main` build, it deploys the exact static artifact to the production Cloudflare Worker and checks all three public pages. You can also run **website** manually from the Actions tab.

Add the suite repository secret `CLOUDFLARE_API_TOKEN` with Workers Scripts: Edit for the Soenneker account, and Zone: Read plus Workers Routes: Edit for `soenneker.com`. The account ID and custom domain are configured in the repository. Pull requests never receive deployment credentials or publish changes.

## Code examples

`Components/CodeExample.razor` renders Quark Suite's read-only `CodeEditor`, with a plain-text HTML fallback. Because the export has no Blazor runtime, `wwwroot/js/code-examples.js` initializes the component through Quark's shipped Monaco interop and wires its copy button. Editors load as they approach the viewport. The fallback remains readable when JavaScript is unavailable or initialization fails.

Monaco, its stylesheet, and its worker are served from the published Quark package assets under `/_content/`. No CDN or server-side Blazor session is required. Keep the static initializer in sync with CodeEditor interop when upgrading Quark; the export validates the required asset paths.
