# Chargit-JA

What this project is and why it is useful
- Chargit-JA is a C#/.NET solution created as an MVP reminder utility to prompt users to charge their phone before sleep. The repo contains a Visual Studio solution (ChargitJA.slnx and ChargitJA/ project folder). It's useful as a lightweight desktop reminder to prevent waking up with a dead phone.

Usage (exact commands / environment)
1. Prereqs: .NET SDK matching the project (open the solution in Visual Studio or use dotnet CLI).
2. Run locally:
   - Open the solution in Visual Studio and press Run, or from CLI:
     - cd Chargit-JA/ChargitJA
     - dotnet restore
     - dotnet run
3. Packaging: Use Visual Studio Publish or dotnet publish to create a distributable binary for Windows.

Project structure & how it works
- ChargitJA.slnx / ChargitJA/ — Visual Studio solution and project files.
- The application uses a timer/event loop to schedule reminders; settings are stored locally in app config or simple files.

Tech stack & architecture choices
- C# / .NET (Windows desktop) — suitable for native notifications and system tray integration.
- Design trade-offs: local-first and simple architecture keep it private and reliable, but distribution/updating requires manual packaging.

Notes & improvements
- Add a Settings section describing configurable reminder intervals and how to enable autostart. Include release artifacts or installer instructions for non-developers.

