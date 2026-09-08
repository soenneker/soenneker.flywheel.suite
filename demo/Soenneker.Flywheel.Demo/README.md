# Soenneker.Flywheel.Demo

Sample jobs with the Flywheel dashboard.

## Setup

Install .NET 10, start Redis 6.0.9 or newer at `localhost:6379`.

## Usage

From the suite repository root:

```powershell
dotnet dev-certs https --trust
dotnet run --project demo/Soenneker.Flywheel.Demo
```

Open [the dashboard](https://localhost:7443/) and sign in with `admin` / `flywheel-demo`. Signed-out visitors go to `/signin`, then return to `/` after signing in. These credentials apply only in Development when no password hash is configured.

Press `Ctrl+C` in the terminal to stop the demo.

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
