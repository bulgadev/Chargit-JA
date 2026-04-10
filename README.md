# Chargit-JA

> **Never forget to charge your phone again.**

Chargit-JA is a **.NET MAUI + Blazor Hybrid** cross-platform app (Android, iOS, macOS Catalyst, Windows) that monitors your device battery level in real time, syncs every device's state to a shared Redis store, and fires a native alarm at **22:00 every night** whenever your battery is below **80%** — so you never wake up with a dead phone.

---

## Table of Contents

1. [What the project is and why it is useful](#1-what-the-project-is-and-why-it-is-useful)
2. [Usage — exact commands and environment variables](#2-usage--exact-commands-and-environment-variables)
3. [Project structure and how it works](#3-project-structure-and-how-it-works)
4. [Tech stack and architecture choices](#4-tech-stack-and-architecture-choices)
5. [Notes, limitations, and suggested improvements](#5-notes-limitations-and-suggested-improvements)

---

## 1. What the project is and why it is useful

### Problem

Charging habits are easy to forget: you go to bed, your phone is at 30%, and you wake up to find it dead or still draining. Simple notification apps solve this partially, but they can't tell the rest of your household's devices what's happening — and they don't give you a hard-to-miss alarm at a predictable time.

### Solution

Chargit-JA runs continuously in the background and does three things:

| Capability | How it works |
|---|---|
| Real-time battery readout | Subscribes to `Battery.Default.BatteryInfoChanged` (MAUI hardware API) |
| Cross-device visibility | Pushes every device's `{name, battery%, status, last_sync}` to a Redis JSON document keyed by the signed-in user |
| Nightly alarm | At exactly **22:00** each day, if battery < 80%, triggers a platform-native alarm — `AlarmClock` on Android, `UNNotificationCenter` on iOS |

### Who it's for

- Anyone who wants a single dashboard to see all their household devices' battery at a glance.
- Developers learning .NET MAUI Blazor Hybrid with real-world service integration (Redis, OAuth2/PKCE).

---

## 2. Usage — exact commands and environment variables

### Prerequisites

| Requirement | Notes |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | See `<TargetFrameworks>net10.0-*</TargetFrameworks>` in `ChargitJA.csproj` |
| MAUI workload | `dotnet workload install maui` |
| Android / iOS SDK or emulator | For mobile targets |
| Redis with RedisJSON module | The app uses `JSON.GET`, `JSON.SET`, `JSON.ARRAPPEND`. Address is currently hardcoded to `192.168.100.86:6379` in `BatteryService.cs` — change it for your environment |
| Zitadel tenant (for auth) | Can be skipped; the app falls back to a guest session (`user:bulga`) |

### Environment variables / `.env` file

`AuthService` reads authentication settings from **environment variables first**, then from a bundled `.env` file (placed at the repo root as `ChargitJA/.env`; it is included in the MAUI bundle as `env.config` via the project's `<MauiAsset>` entry).

```dotenv
# Note: the key is named AUTO_ENDPOINT in the source — likely a typo for AUTH_ENDPOINT (see §5)
AUTO_ENDPOINT=https://<your-zitadel-domain>/oauth/v2/authorize
TOKEN_ENDPOINT=https://<your-zitadel-domain>/oauth/v2/token
USERINFO_ENDPOINT=https://<your-zitadel-domain>/oidc/v1/userinfo
ZITADEL_CLIENT_ID=<your-client-id>
ZITADEL_CLIENT_SECRET=<your-client-secret>
```

If any of these keys are missing the app still runs; `AuthService.InitializeAsync()` logs a warning and the session defaults to a built-in guest account (`username: bulga`).

### Run locally (Android emulator or device)

```bash
cd ChargitJA/ChargitJA
dotnet restore
dotnet build -f net10.0-android
dotnet run -f net10.0-android
```

Or open `ChargitJA.slnx` in Visual Studio 2022 / Visual Studio for Mac, select the target device from the toolbar, and press **Run**.

### Run on Windows

```bash
cd ChargitJA/ChargitJA
dotnet run -f net10.0-windows10.0.19041.0
```

> **Note:** Windows does not support the platform alarm APIs (`AlarmInterface` throws `PlatformNotSupportedException` for non-Android/iOS targets). Battery monitoring and the Redis sync still work on Windows.

### Publish (produce a distributable binary)

```bash
# Android APK
dotnet publish -f net10.0-android -c Release

# Windows unpackaged executable (WindowsPackageType=None is already set)
dotnet publish -f net10.0-windows10.0.19041.0 -c Release
```

---

## 3. Project structure and how it works

```
Chargit-JA/
├── ChargitJA.slnx                  # Visual Studio solution (single-project format)
└── ChargitJA/
    ├── ChargitJA.csproj            # Multi-target MAUI project (Android/iOS/Mac/Win)
    ├── MauiProgram.cs              # App entry point — DI registration, startup
    ├── App.xaml / App.xaml.cs      # MAUI Application class → creates Window → MainPage
    ├── MainPage.xaml               # Single XAML page: hosts BlazorWebView
    ├── .env                        # (git-ignored) auth credentials loaded as env.config
    │
    ├── Services/
    │   ├── BatteryService.cs       # Core: battery polling, Redis sync, alarm scheduling
    │   ├── AlarmInterface.cs       # Platform-specific alarm: Android Intent / iOS UNNotif.
    │   ├── AuthService.cs          # OAuth2/PKCE login, token refresh, user info fetch
    │   └── UserSessions.cs         # In-memory session state (INotifyPropertyChanged)
    │
    ├── Components/
    │   ├── Routes.razor            # Blazor router root
    │   ├── _Imports.razor          # Global @using directives
    │   ├── Layout/
    │   │   ├── MainLayout.razor    # Shell layout wrapping all pages
    │   │   └── NavMenu.razor       # Top nav — Home / Devices / Settings + Login/Logout
    │   └── Pages/
    │       ├── Home.razor          # "/" — live battery %, status, test notification button
    │       ├── Devices.razor       # "/devices" — lists all devices from Redis for this user
    │       └── NotFound.razor      # Fallback 404 page
    │
    ├── Platforms/                  # Per-platform boilerplate (Android, iOS, Mac, Windows)
    ├── Resources/                  # App icon, splash, fonts, raw assets
    └── wwwroot/                    # Static web assets served into BlazorWebView
        ├── index.html              # Blazor bootstrap HTML
        └── app.css / lib/bootstrap # Styles
```

### Data flow — step by step

```
App launch
  └─ MauiProgram.CreateMauiApp()
        ├─ registers singletons: HttpClient, UserSessions, AuthService, BatteryService
        ├─ triggers AuthService.InitializeAsync()          ← reads .env / env vars
        │    └─ tries SecureStorage refresh token → calls Zitadel token endpoint
        │         └─ on success: UserSessions.SetAuthenticatedUser(id, name, email)
        └─ BatteryService constructor runs immediately
              ├─ reads DeviceInfo.Name → builds a slug-based _deviceId
              ├─ connects to Redis (AbortOnConnectFail=false, won't crash if unavailable)
              ├─ calls UpdateBatteryInfo(pushToRedis:true) → first sync
              ├─ subscribes to Battery.Default.BatteryInfoChanged
              ├─ subscribes to UserSessions.PropertyChanged (re-syncs on login/logout)
              └─ starts Timer (period: 1 minute) → CheckAndTriggerAlarm()

Every minute (Timer tick):
  CheckAndTriggerAlarm()
    ├─ calls UpdateBatteryInfo() → refreshes BatteryLevel/BatteryStatus
    ├─ if DateTime.Now != 22:00 → return (skip)
    ├─ if already fired today (_lastAlarmDate) → return (skip)
    ├─ if battery >= 80% → return (skip — you're fine)
    └─ AlarmInterface.SetAlarm(22:00, "Your {device} is not charging!")
          ├─ Android: fires ACTION_SET_ALARM Intent (native clock app)
          └─ iOS: schedules UNCalendarNotificationTrigger via UNUserNotificationCenter

On every battery change (BatteryInfoChanged event):
  UpdateBatteryInfo()
    ├─ updates BatteryLevel and BatteryStatus properties (fires PropertyChanged → UI refresh)
    ├─ if percentage dropped (or first run) → PushBatteryToRedis()
    │    └─ EnsureUserDocumentExists(): JSON.SET "user:{username}" NX
    │         { "user_info": {...}, "devices": [], "settings": {"theme": "dark", "notifications": true} }
    │    └─ JSON.GET / JSON.ARRAPPEND / JSON.SET on $.devices[?(@.id=='{deviceId}')]
    └─ LoadDevicesFromRedis()
         └─ JSON.GET "user:{username}" $.devices → deserialises into IReadOnlyList<UserDevice>
              → PropertyChanged fires → Devices.razor re-renders cards
```

### Blazor UI pages

| Route | Component | Key injection |
|---|---|---|
| `/` | `Home.razor` | `BatteryService` — shows live `BatteryLevel` + `BatteryStatus`; "Test Notification" uses `Plugin.LocalNotification`; debug-only "Test Alarm Check" button calls `BatteryService.TriggerAlarmCheckForDebug()` |
| `/devices` | `Devices.razor` | `BatteryService.Devices` — renders a Bootstrap card per device with name, battery%, status |
| `/settings` | *(not yet implemented)* | Linked from nav and Home page |

---

## 4. Tech stack and architecture choices

| Layer | Technology | Why |
|---|---|---|
| App framework | **.NET MAUI** (net10.0) | Single codebase for Android, iOS, macOS, and Windows; exposes native APIs (battery, alarms, secure storage) |
| UI | **Blazor Hybrid** (BlazorWebView) | Web-stack (Razor/HTML/CSS/Bootstrap) with direct C# service injection; no REST layer needed between UI and services |
| Real-time battery | `Microsoft.Maui.Devices.Battery` | Platform-agnostic hardware API; push-based via `BatteryInfoChanged` event |
| Local notifications | `Plugin.LocalNotification` 14.1.0 | Handles notification permission requests and cross-platform presentation with a single API |
| Platform alarms | `Android.Provider.AlarmClock` / `UNUserNotificationCenter` | Uses each OS's native scheduling primitive so the alarm fires even if the app is backgrounded |
| Data store | **Redis + RedisJSON** via `StackExchange.Redis` 2.8.41 | Allows multiple devices to see each other's battery state through a shared JSON document per user |
| Authentication | **Zitadel** OAuth2 / OIDC with **PKCE** | `S256` code challenge; refresh token stored in `SecureStorage` (OS keystore); supports both login and registration flows via `screen_hint=signup` |
| Dependency injection | `Microsoft.Extensions.DependencyInjection` (MAUI built-in) | All services registered as singletons in `MauiProgram`; Blazor pages inject them with `@inject` |
| Logging | `Microsoft.Extensions.Logging.Debug` | Debug-only; production builds omit developer tools |

### Architecture decisions and trade-offs

- **Blazor Hybrid over native XAML UI** — lets the UI be built in a familiar web stack and shared across platforms without writing platform-specific UI; the trade-off is a heavier WebView process per page.
- **Redis as the shared device store** — gives instant multi-device visibility without building a dedicated API server; the trade-off is that a Redis instance (with JSON module) must be reachable on the local network; the connection string is currently hardcoded.
- **Timer polling at 1-minute granularity** — `CheckAndTriggerAlarm` runs every 60 seconds. The alarm time is checked at second-zero precision (`Hour == 22 && Minute == 0`), so the alarm window is exactly one minute wide each night. This avoids needing a background OS scheduler but requires the app to be running (or resumed) at that moment.
- **Guest fallback** — if auth is unconfigured all data is stored under `user:bulga`. This is convenient for local testing but means any unconfigured device shares the same Redis key.

---

## 5. Notes, limitations, and suggested improvements

### Known limitations

| Issue | Detail |
|---|---|
| Hardcoded Redis address | `BatteryService.cs` line 14: `private const string RedisConnectionString = "192.168.100.86:6379";`. This must be changed before running on any network other than the developer's LAN. Move this to the `.env` file or app settings. |
| Redis requires RedisJSON module | Plain Redis does not support `JSON.GET`/`JSON.SET`. A Redis Stack instance or `redis-stack-server` Docker image is needed. |
| Alarm only works on Android and iOS | `AlarmInterface.SetAlarm()` throws `PlatformNotSupportedException` on Windows and macOS Catalyst. Consider using `Plugin.LocalNotification` (already a dependency) on those platforms for consistency. |
| Guest key collision | Without auth, all anonymous devices write to `user:bulga`. Running the app on two devices without logging in will cause them to share one Redis document. |
| Settings page is missing | The nav links to `/settings` and `Home.razor` has a "Settings" button, but no `Settings.razor` page exists yet. |
| No autostart | The app must be manually launched for the nightly alarm to fire; there is no OS-level autostart registration. |
| `AUTO_ENDPOINT` key name | The constant `RequiredKeys[0]` is `"AUTO_ENDPOINT"` — likely a typo for `"AUTH_ENDPOINT"`. |

### Suggested improvements

1. **Move the Redis connection string to `.env`** — add a `REDIS_CONNECTION` key alongside the Zitadel keys; `BatteryService` should read it the same way `AuthService` reads its options.
2. **Implement the Settings page** — expose configurable reminder time (currently hardcoded to `22:00`), the minimum battery threshold (currently `80`), and a toggle for notifications.
3. **Add autostart registration** — on Android, register a `BOOT_COMPLETED` broadcast receiver; on Windows, add a registry run key or use the Task Scheduler via `dotnet publish` post-install steps.
4. **Publish release artifacts** — add a GitHub Actions workflow that runs `dotnet publish` for Android and Windows and attaches the APK/EXE as release assets so non-developers can install the app directly.
5. **Use `Plugin.LocalNotification` for the scheduled alert on all platforms** — this would unify the alarm path, remove the `#if ANDROID / #elif IOS` branching in `AlarmInterface`, and extend support to Windows and macOS.
6. **Configurable Redis key prefix** — multi-tenant deployments should namespace keys more carefully; consider a `REDIS_KEY_PREFIX` env variable.
7. **Unit tests** — the business logic in `BatteryService.CheckAndTriggerAlarm` and `AuthService` is testable in isolation; adding xUnit tests with mocked `IDatabase` and `IBattery` interfaces would catch regressions early.