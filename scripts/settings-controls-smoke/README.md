# Settings control regression

Run after the shared desktop build is free:

```powershell
dotnet run --project scripts/settings-controls-smoke/SettingsControlsSmoke.csproj -c Release -- artifacts/settings-controls-smoke
```

The fixture uses an unshown WPF window and fresh isolated settings. It exercises the recorder's key-processing and release paths without sending native keyboard input or installing global hooks. It verifies tap/hold voice transitions, immediate shortcut persistence, Agent composer preparation, distinct Left/Right Shift, Ctrl, and Alt buttons, AltGr handling, modifier combinations, bare F1–F24, letters, digits, Space, Enter, navigation keys, and punctuation against the actual parser. It also checks exact-side press/release dispatch, cancellation, physical release after focus loss, repeat-key safety, reserved and duplicate validation, custom-color persistence, and the compact input-reactive companion state. The full Windows palette is constructed and inspected, never shown. No microphone, model, full Settings Save, startup registration or owner data is used.

`results.json` records the checks and `controls.png` captures an off-screen render. Real keyboard capture and the native color dialog still need a separate coordinated desktop UI check.
