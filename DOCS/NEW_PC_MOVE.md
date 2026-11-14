# Moving the work to the new PC (TODO 645)

Started 2026-11-14. The new PC is a Dell OptiPlex 5080 (i7-10700, 32 GB, 1 TB). It replaces the HP EliteDesk
800 G5 Mini (16 GB, 256 GB). The live service runs in Azure and is not affected; only the working setup moves.

**The old PC stays as it is until the new one has passed a full gate.** Do not retire it, change it, or delete
anything on it before that.

## Why the new PC was reset first

It came from whoever prepared it. Its local administrators were `Administrator` and `User`, with passwords
unknown to the owner, and the owner's `admin` account was a standard user. The owner reset it ("Remove
everything", Cloud download) and set Windows up again with the local account `admin` as its administrator.
Remote Desktop is on.

## The owner's part, on the new PC

1. Windows set up: a local account named exactly `admin`, Remote Desktop on, Windows Update done.
2. OneDrive signed in. Do **not** turn on OneDrive's backup of Desktop or Documents: it would move
   `C:\Users\admin\Documents`, and every path breaks.
3. The Claude desktop app (x64) installed and signed in.
4. In `OneDrive\Codex_Code_Bkup\move-20261114`:
   - double-click `1-Install-Tools.cmd`, clicking Yes when Windows asks, then restart;
   - with Codex closed, double-click `2-Place-Files.cmd`.
5. Open Claude, go to Code, choose the folder
   `C:\Users\admin\Documents\Codex\2026-06-30\ca\work\iPro_Project\iPro_Project\IPRO_Modern`, and say
   "carry on with the move to this PC (TODO 645)".

## What the move folder holds

| File | Becomes |
|---|---|
| `1-Install-Tools.cmd` | winget installs: Git, .NET 10 SDK, Node.js LTS, Python 3.14 (all users, on PATH), Azure CLI, GitHub CLI, VS Code; then `npm install -g azurite@3.36.0` |
| `2-Place-Files.cmd` / `.ps1` | Places everything below. Each part runs once: markers in `C:\Users\admin\iPro_tools\move-steps`. |
| the newest recovery kit in `Codex_Code_Bkup` | the iPro repo (all 63 branches, `main` checked out) at the project path above; the builder repo (`ipro/beta`) at `C:\Users\admin\Projects\ipro-builder`; the notes-and-tools zip in `C:\Users\admin\iPro_tools` |
| `move_buildersite_*.zip` | `Projects\ipro-builder-site`: both builders' data, `build-e853cae` (what they run on), the start files, the old backups (key rings left out) |
| `move_claude_*.zip` | `.claude\projects` (the notes and past conversations) and `.claude\settings*.json`; then the kit's newer copy of the notes goes over the old one |
| `move_codex_*.zip` | `.codex`: Codex's threads, archived threads, settings, rules, skills, plugins, pictures and visualizations, and its databases (snapshotted safely) |
| `move_documents_*.zip` | `Documents`: the System Book, iPro_Builder_Images, the e-card brief, marketing backups, Testing UI, the v0.4 checker, the rest of the Codex folder, and the six old backups that exist only in `Documents\IPRO_Backups` |
| `move_projects_*.zip` | `Projects\IPRO.TemplateBuilder` (the outside developer's folder: review packages, his backups; no build output) and `Projects\ipro-builder-roundtrip` |
| `mysql-8.0.44-winx64.zip`, `my.ini`, `move_localdb_ipro_crm_*.sql` | `C:\Users\admin\ipro-local`: the same MySQL as the old PC and a dump of the local `ipro_crm` |
| `gitconfig.txt` | `C:\Users\admin\.gitconfig`: the commit name and email |

**Left on the old PC on purpose:**
- **Secrets.** The two `appsettings.Development.json` files are recreated, and the owner types or approves
  their values. The builders' key rings: a new one is made on first start, and the builder page asks for one
  reload. Codex's sign-in (`auth.json`). The scratchpad's `local614_cred.txt`: a new local test password is made
  when needed.
- **Codex's built-in browser profile.** Its saved logins are locked to the old PC.
- **Build output, caches, logs, and the old builder build `build-6ffed14`.**

## The new PC's Claude session: the rest

1. **Notes and tools.** Read `MEMORY.md`, the newest `SESSION_HANDOFF` and this file. Copy
   `C:\Users\admin\iPro_tools\scripts` into the session's scratchpad, because the gate and release tools live
   there.
2. **Tools.** Check `git`, `dotnet --list-sdks` (10.0.x; `global.json` asks 10.0.301 or a later feature band),
   `node`, `azurite`, `python`, `az`, `gh`. Fix PATH problems; the Microsoft Store's python stub must not win.
3. **Local MySQL**, the same as the old PC: service `IPROLocalMySQL`, automatic start, LocalSystem.
   - Initialise the empty data folder:
     `ipro-local\mysql-8.0.44-winx64\bin\mysqld.exe --defaults-file=C:\Users\admin\ipro-local\my.ini --initialize-insecure`.
     `my.ini` sets basedir and datadir under `C:/Users/admin/ipro-local`, port 3306, bind-address 127.0.0.1 and
     mysqlx OFF. root has no password and listens on loopback only.
   - Register the service, elevated (the owner clicks Yes):
     `mysqld.exe --install IPROLocalMySQL --defaults-file=C:\Users\admin\ipro-local\my.ini`. Then start it.
   - `SET PERSIST binlog_expire_logs_seconds=259200` (three days, as on the old PC).
   - Create `ipro_local`@`localhost` and @`127.0.0.1` with the local password in `DOCS/16_LOCAL_DEV.md`.
     Grant ALL on `ipro_crm`.* (the old PC also had grants on a few `ipro_fresh*` scratch databases; add them
     only if a test needs them).
   - Load `ipro-local\move_localdb_ipro_crm_20261114.sql` (it creates `ipro_crm`).
4. **The two `appsettings.Development.json`**, as in `DOCS/16_LOCAL_DEV.md`. Local-only values; the
   SuperAdmin password and the AdminPreview secret are typed by the owner or generated straight into the
   files, never printed. PayPal sandbox and Stripe test keys stay empty until a task needs them.
5. **The builders.** The owner double-clicks `Projects\ipro-builder-site\Start-Builder.cmd` and
   `Start-Builder-For-Handovers.cmd` (a builder started from a tool dies later). Check that 5087 and 5088
   answer 200 and that his site shows his pages.
6. **Sign-ins, by the owner.**
   - `az login`: two Authenticator checks, "Microsoft" then "Default Directory".
   - `gh auth login`.
   - Git's credential manager on the first push.
   - Codex: sign in again; his threads should be there.
7. **Build:** `dotnet build IPRO.sln`, and build the builder.
8. **Local check:** run `ops\Start-LocalEnv.ps1` (Azurite) and open the local portal.
9. **The proof: a full gate** with `gate_parts.py`, the same way as on the old PC. Record its time; the old PC
   took about 40 minutes.
10. **X: (the picture drive):** in the Remote Desktop client, Local Resources -> More -> Drives, the same as for
    the old PC.
11. **When the gate passes**, tell the owner. The old PC can then be retired on his word, and the move folder
    in OneDrive can be deleted on his word.
