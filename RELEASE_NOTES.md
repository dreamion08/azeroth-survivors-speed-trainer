# v1.1.0

Automatic updates for Azeroth Survivors Speed Trainer.

- Check for the latest stable release at startup, every six hours while the trainer is open, or with **Check updates**.
- Download updates in the background while you play, with a published SHA-256 checksum check before installation. No GitHub login or token is required.
- Prepare the update and wait quietly until the trainer and all Azeroth Survivors processes have closed. The updater never forces an app or game to close.
- Use **Install update** once a download is ready, or close the trainer normally to queue installation. The trainer restarts after the update is applied.
- Back up replaced files and attempt rollback if installation fails. Offline checks and failed downloads leave the current version usable, with status shown without a blocking popup.

The five speed presets, Ctrl+F6 through Ctrl+F10, Shift plus mouse-wheel control, game selection and floating mini mode remain available.

## Install or upgrade

Download **Azeroth-Survivors-Speed-Trainer-v1.1.0-win-x64.zip** and extract all files to a writable folder. Keep these three files together:

- `Azeroth-Speed.exe`
- `AzerothSpeedHook.dll`
- `Azeroth-Speed-Updater.exe`

**Version 1.0.0 requires this one manual upgrade:** it does not include an updater and cannot install v1.1.0 automatically. Close the old trainer and Azeroth Survivors before replacing the old files. Future stable releases can be downloaded and queued automatically by v1.1.0.

Requires 64-bit Windows; .NET Framework 4.8 is recommended. Updates replace trainer files only and do not modify the game or its saves.

The release is unsigned. SHA-256 checks provide download integrity, not publisher signing. The trainer uses a conventional timing DLL loaded into the selected game; anti-cheat coexistence, including Aion 2, and co-op/network play remain unverified. Compatibility checks do not guarantee support for every future game update.
