# GRID0 Setup

A small GRID0-branded Windows wizard. It installs the official signed
ZeroTier client (if missing) and auto-joins the GRID0 network, with a
colored status indicator:

- gray: working (checking / installing / joining)
- green: connected to GRID0
- orange: waiting for network authorization, or timed out
- red: something went wrong (details in the log box)

## Layout

```
GRID0Setup.csproj
Program.cs
MainForm.cs          <- all UI + logic, built in code (no designer files)
app.manifest         <- requests admin rights (the MSI needs them)
banner.png           <- GRID0 banner from the GRID0 repo (img/banner.png), shown in the window header
grid0.ico            <- app icon, cropped from the banner's emblem
.github/workflows/build.yml
```

## Build

Locally (needs the .NET 8 SDK):

```
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist
```

Or push to `main` and grab `GRID0Setup.exe` from the workflow artifacts.

## Notes

- The app asks for admin rights on launch because the ZeroTier MSI
  requires them. The exe itself is unsigned, so SmartScreen warns once.
- `ZeroTierMsiUrl` in `MainForm.cs`: verify it matches the latest
  ZeroTier release before shipping.
- Joining the network is step one. Each new member still needs
  authorizing on the GRID0 network unless it is set to auto-authorize.
