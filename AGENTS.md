# AGENTS.md

## Working agreement for this repository

### Ask for administrator rights by Telegram, and wait

Any step that needs administrator rights on the user's machine - a UAC
prompt, a driver install or restart, a service start, a registry write
under `HKLM`, a device enable or disable - must follow this procedure:

1. **Send a Telegram message first.** Use the `telegram_notify` tool
   (namespace `user-telegram`) with a short message saying what is
   needed and what will happen. The user asked for this explicitly:
   they are not always watching the terminal.

2. **Then trigger the elevation.** Start the elevated process with
   `Start-Process ... -Verb RunAs`, and poll for the result.

3. **Wait, and report.** Do not treat a dismissed UAC prompt as a
   failure of the script: this machine has `PromptOnSecureDesktop = 1`
   and `FilterAdministratorToken = 1`, so the prompt appears on the
   secure desktop where no automation can click it. A cancellation
   means the user was not at the machine - message them again rather
   than retrying in a loop.

   The user has to be at the keyboard for the "Yes". Telegram is the
   way to tell them to come.

### The example that got this wrong

The Virtual Display Driver (MttVDD) repair needed elevation six
times in one session. Every prompt was silently dismissed, and each
retry burned a couple of minutes. A Telegram message on the first
prompt would have saved all of it.

### Never send these driver commands

The driver's control pipe (`\\.\pipe\MTTVirtualDisplayPipe`) takes a
`SETDISPLAYCOUNT` command. **Both** values are destructive: `0` tears
the adapter down and leaves the driver in error code 43, and `1`
republishes the adapter in a way that has the same effect. The
driver's own count lives in `HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver`
and in `C:\VirtualDisplayDriver\vdd_settings.xml`; the pipe is not
how it is set.

The display's lifecycle goes through the CCD API
(`DisplayConfigController`), which removes the display's *path* from the
desktop instead of tearing the adapter out. That is reversible, and it
needs no administrator rights.

### Watch out for the XML comment trap

`C:\VirtualDisplayDriver\vdd_settings.xml` is read by the driver with
a strict XML parser. A comment may not contain `--`, so do not use a
row of dashes as an underline in that file. When the file fails to
parse, the driver reads its settings as empty and publishes no monitor
at all - which looks exactly like a broken driver.

The copy of the file that is kept in this repository is the one the
driver reads; keep it valid XML.

### Build and test

```powershell
dotnet test -c Release          # 345 tests

# The fast build: a folder.  `artifacts\release\RokidDiyStation.exe`
# runs from there.  Good enough for testing on this machine.
dotnet publish src\RokidDiyStation.App\RokidDiyStation.App.csproj `
    -c Release -r win-x64 --self-contained false -o artifacts\release

# The SHIPPED build: one self-contained exe, so the user needs no
# .NET runtime.  This is what goes into the GitHub release, and it is
# the shape the README promises.
dotnet publish src\RokidDiyStation.App\RokidDiyStation.App.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o artifacts\single
```

Do not confuse the two: the release asset is the **single** file from
`artifacts\single`, not the folder build. Shipping the folder build
would leave the user needing a .NET install that the README says they
do not need.

### Commit discipline

Commit after a verified change, with a message that explains WHY the
change was needed, not just what it was. The repository's history is
the design record for this project: the comments in the code explain
which approaches were tried and why they failed, and a reader should
be able to reconstruct the reasoning from the commits alone.
