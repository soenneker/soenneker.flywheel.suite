# Soenneker.Flywheel.Demo

Flywheel engine and API with sample jobs.

The solution's `Demo` startup configuration runs both projects. `Soenneker.Flywheel.Demo` runs the engine/API at `https://localhost:7443`; `Soenneker.Flywheel.Dashboard.Demo` runs the dashboard at `https://localhost:7039`. Only the dashboard opens a browser. The engine allows credentialed dashboard requests from that HTTPS origin.

## Setup

Install .NET 10, start Redis 6.0.9 or newer at `localhost:6379`.

## Usage

From the suite repository root:

```powershell
dotnet dev-certs https --trust
dotnet run --project test/Soenneker.Flywheel.Demo
```

In a second terminal, run `dotnet run --project test/Soenneker.Flywheel.Dashboard.Demo`.

Open [the dashboard](https://localhost:7039/) and sign in with `admin` / `flywheel-demo`. Signed-out visitors go to `/signin`, then return to `/` after signing in. These credentials apply only in Development when no password hash is configured.

Press `Ctrl+C` in both terminals to stop the demos.

## Configuration

Set these environment variables before running if you need different settings:

| Variable | Default | Purpose |
|---|---|---|
| `Flywheel__Redis` | `localhost:6379` | Redis connection |
| `Demo__RedisNamespace` | `demo` | Separate demo data from other jobs |
| `Demo__SeedOnStartup` | `true` | Create sample jobs on startup |
| `Flywheel__Dashboard__Username` | `admin` | Login name |
| `Flywheel__Dashboard__PasswordPhc` | Development demo password PHC string | Set your own PBKDF2 PHC string |

Jobs and recurring schedules remain in Redis after shutdown. Use a new namespace for a fresh tour. Outside Development, a password hash is required.

The tour includes `demo.work.fifteen-seconds.v1`, which runs for approximately 15 seconds and logs progress every three seconds. It is also registered as a recurring job every two minutes, so you can use **Run now** to watch it execute again.
