# Solarpowers — Linux Mint + ASP.NET Core + SQLite

This project keeps the visual design from your supplied `index.html` and adds a server-backed ASP.NET Core API and SQLite database. Products, gallery, orders, enquiries and feedback are stored in `solarpowers.db`, not only in browser localStorage. The admin login uses an HttpOnly authentication cookie.

## Run on Linux Mint

1. Install the .NET 8 SDK from Microsoft's official .NET installation instructions if `dotnet --version` does not work.
2. Extract this ZIP.
3. Open a terminal in the extracted `SolarPowers-Full` folder (the folder containing `SolarPowers.csproj`).
4. Change the admin password in `appsettings.json` before running. Default demo login: **admin / ChangeThisNow!**
5. Run:

```bash
dotnet restore
dotnet run --urls http://localhost:5080
```

6. Open **http://localhost:5080** in Firefox. Use the site's Admin section/login. SQLite database is created automatically at first run.

## Features

- Original responsive solar storefront, categories, product cards, cart and checkout form
- Product and gallery management in the admin dashboard
- Orders, enquiries and feedback submitted by visitors and visible in admin
- SQLite persistence across restarts
- Admin-only changes protected by server-side cookie authentication
- Optional AI chat: set your own Anthropic API key with `dotnet user-secrets init` then `dotnet user-secrets set "Anthropic:ApiKey" "YOUR_KEY"` (or configure it securely outside source control)

## Important

- This is a local/demo starter. Before public deployment, use HTTPS, a strong unique admin password, rate limiting, backups, validation and security review. Do not expose a development server directly to the internet.
- Orders are pay-on-delivery; no payment gateway or email delivery is included.
- Product/gallery uploads are currently stored as data URLs in SQLite. For larger deployments, use file/object storage and strict image size limits.
- No .NET SDK was available in the generation environment, so this ZIP was not compiled here. If `dotnet restore` or `dotnet run` prints an error, share the exact terminal text and it can be fixed.
