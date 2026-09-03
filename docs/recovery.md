# HeyBuddy backup, upgrade, and recovery

HeyBuddy's executable and its personal data are separate. Installing or uninstalling the application must not remove your history, memory, skills, credentials, or downloaded models.

| Content | Default location |
| --- | --- |
| Installed application | `%LOCALAPPDATA%\Programs\HeyBuddy` |
| Settings, history, memory, skills, credentials, workspace | `%LOCALAPPDATA%\ClickyLocal` |
| History/tasks database | `%LOCALAPPDATA%\ClickyLocal\clicky.db` |
| Settings and previous settings | `settings.json` and `settings.json.bak` in the data folder |
| Inspectable memory and skills | `Memory` and `Skills` in the data folder |
| Downloaded models | User-selected `<model-folder>`; otherwise the app data folder's `Models` directory |
| Managed inference and voice runtimes | The data folder's `Runtime` directory |

The existing `ClickyLocal` directory and internal names remain in use after the HeyBuddy rename to preserve continuity. `CLICKY_DATA_DIR` can override the data root. Model/runtime/workspace locations can also be changed in settings; inspect those settings before copying or restoring data.

## Stop and back up

1. Use the tray's **Stop everything** or the configured emergency shortcut to cancel current work. This does not close the app.
2. In Settings, **Back up local data** creates a consistent SQLite backup through SQLite's backup API while the app is open. That backup contains database history/tasks, not the separate memory, skills, files, settings, or credentials.
3. For a complete file backup, choose **Quit** from the HeyBuddy tray menu. Closing the main window may leave HeyBuddy running in the tray. Confirm `HeyBuddy.exe` has exited before copying the whole data folder.
4. Copy the data folder to a dated backup directory. Preserve `clicky.db`, any `clicky.db-wal`/`clicky.db-shm` sidecars still present, `settings.json`, `Memory`, `Skills`, `Credentials`, connector configuration, and generated workspace files together. Do not copy only a live WAL-mode database with a normal file-copy command.
5. Models can be backed up separately or downloaded again. Keep the configured model path intact if storage is available; reinstalling HeyBuddy does not require downloading already verified models again.

Treat backups as private. The database and memory files are locally inspectable, not encrypted containers. DPAPI encrypts connector/provider secrets for the current Windows user, but the rest of the data may contain personal content.

## Install, upgrade, or switch to portable

The per-user installer installs to `%LOCALAPPDATA%\Programs\HeyBuddy`, creates a Start menu shortcut, and offers an optional desktop shortcut. It does not enable startup at sign-in. A user can enable that later in Settings. Installation requires Windows 11 on x64; ARM64 is not enabled in this release. The bundled Whisper CPU runtime requires AVX/AVX2/FMA/F16C support. No administrator account is required; the installer uses Inno Setup's [non-administrative installation mode](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm).

Quit the current instance before upgrading. The installer checks the existing application mutex, replaces application binaries, and preserves the separate data/model directories. Keep a full backup before changing versions. A portable ZIP contains the same self-contained .NET application; extract its entire `HeyBuddy` folder and run `HeyBuddy.exe`. Keep the accompanying DLL/native runtime files together. Portable means the executable does not require a machine-wide .NET installation; personal state still uses the normal local data directory unless explicitly overridden.

The release archive excludes model weights and personal data. On a new machine, use the app's model/speech setup to download the required files. On the current PC, the existing configured models remain available. Builds are not code-signed unless a future release explicitly states otherwise; a file hash proves artifact integrity, not a publisher signature.

The release command is:

```powershell
pwsh -File .\scripts\release.ps1 -Version 0.2.2
```

It publishes a self-contained Windows x64 app, packages the portable ZIP and Inno Setup installer, and writes SHA-256 manifests under `artifacts\release`. It does not install or launch the app. Prior generated artifacts are preserved under `artifacts\release\.previous` before replacement. This folder can grow across repeated packaging runs; its contents are build artifacts, not user data.

## Restore safely

1. Quit HeyBuddy and preserve the entire current data folder under a new dated backup name before overwriting anything. A failed startup may still have useful recent history or settings.
2. Restore a complete known-good data folder, or use a known-good SQLite backup to replace `clicky.db` only after preserving the current database and its WAL/SHM sidecars. Do not combine a restored database with sidecars from a different version of that database.
3. Use the application version compatible with the restored database. The current migration reads SQLite `user_version`; schema version 1 is supported, and a newer version is rejected. The version-1 migration takes a `clicky.db.before-v1.bak` backup when a nonempty older database exists, then runs schema changes in a transaction. A migration backup is not a substitute for a full personal-data backup.
4. If only settings are damaged, preserve the unreadable `settings.json`, then restore `settings.json.bak`. The app reports invalid JSON instead of silently overwriting it. Review restored model paths, shortcuts, selected devices, and provider settings after opening.
5. If a model is unavailable, check that its drive is mounted and its configured directory still exists. Run installation/verification in Settings; verified files are reused and interrupted `.part` downloads can resume. Do not delete unrelated model installations.

DPAPI credentials usually restore only under the same Windows user profile. Copying the `Credentials` folder to another Windows account or a reinstalled system may not restore decryptability. Reconnect accounts and re-enter provider credentials through the application instead of copying or printing secrets. Connector verification must be repeated after reconnecting.

Interrupted running/queued/awaiting-approval tasks are paused when the database opens. Review each task before continuing so an external action is not repeated accidentally. A task that reports an action may already have been performed must not be retried blindly.

## Uninstall and troubleshooting

Uninstall through Windows Installed apps. The installer removes installed binaries and shortcuts. It removes a startup registry entry only if it points exactly to this installed HeyBuddy executable. It does not delete `%LOCALAPPDATA%\ClickyLocal`, configured model/runtime folders outside the application directory, or personal backups.

If HeyBuddy says it is already running, open it from the tray or quit that instance; do not start a second copy against the same state. If microphone/output devices disappear, select a current device in Settings. If input reports that focus changed or a window is protected/elevated, return focus to the intended normal application; do not change Windows security settings or stop protected services to force an agent action.

If a runtime integrity check fails, quit HeyBuddy and move only the versioned runtime folder named in the error to a dated backup location. Then run **Verify local AI installation** in **Models & voices**. Its verified cached archives recreate the missing runtime; keep the model files and voice folders in place.

For a reproducible issue, preserve the exact message, application version, selected local provider/model, and a minimal description of the failed action. Avoid sharing the whole data folder, credentials, screenshots, or database merely to report an error.
