# Contributing

## Setup

```bash
tools/setup-hooks.sh   # once per clone
```

This enables the versioned pre-commit hook in `.githooks/`. The hook runs `tests/solution/check_solution.py` and blocks commits that fail it. It checks the solution structure (relationship file, shell entity, lowercase GUIDs, form ID) and English/Hebrew labels. The `solution-check` GitHub workflow is the same check, run on demand.

## Rules

- Edit the unpacked solution in `solution/AlexPushInboxChannel/`, and build only with `pac solution pack`. Never hand-edit an exported package.
- When you bring changes back from an environment, unpack them over the source and keep the hand-authored `Entities/msdyn_ChannelInstance/` and `Other/Relationships/alex_pushchannelinstance.xml`. Then run `python3 tests/solution/check_solution.py --fix`.
- Write every new label and description in English (1033) and Hebrew (1037). Never copy English into 1037. Follow [docs/localization.md](docs/localization.md).
- Generate new lowercase GUIDs for new channel components.
- Cite public documentation for design changes in [docs/design-choices.md](docs/design-choices.md).
- Don't commit credentials, environment URLs or tenant-specific data.

## Plug-ins

```bash
dotnet build solution/src/Alex.PushInbox.Plugins -c Release
```

The Release build reproduces the DLL in `solution/AlexPushInboxChannel/PluginAssemblies/`.
