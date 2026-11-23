# Localization workflow: English (1033) and Hebrew (1037)

The solution ships every label in English (1033) and Hebrew (1037), and message content supports right-to-left text end to end.

## Right-to-left message content

- Push and inbox content is UTF-8 JSON from CIJ to the device ([RFC 8259 UTF-8](https://www.rfc-editor.org/rfc/rfc8259#section-8.1)). Hebrew title and body text is never transcoded or escaped into another encoding.
- Notifications set `dir: "auto"` and the content's `lang` ([Notification.dir](https://developer.mozilla.org/docs/Web/API/Notification/dir)).
- Inbox items render with `dir="auto"`, and user-supplied fragments inside mixed text are wrapped in `<bdi>` ([HTML dir attribute](https://developer.mozilla.org/docs/Web/HTML/Global_attributes/dir), [W3C: dir in HTML](https://www.w3.org/International/questions/qa-html-dir)).
- The app's `<html lang dir>` follows the user's locale, so Hebrew users get a right-to-left layout.

## What is translated where

| Item | Mechanism | Where in source |
|---|---|---|
| Table names, all columns (including system columns such as Owner, Status and Created On), status options | Translation file ([Translate table and column text](https://learn.microsoft.com/power-apps/maker/data-platform/export-customized-entity-field-text-translation)) | `Entities/alex_pushchannelinstance/Entity.xml` |
| Forms (tabs, sections, labels) | Translation file ([Translate table and column text](https://learn.microsoft.com/power-apps/maker/data-platform/export-customized-entity-field-text-translation)) | `Entities/alex_pushchannelinstance/FormXml/**` |
| System views (names and descriptions) | Translation file ([Translate table and column text](https://learn.microsoft.com/power-apps/maker/data-platform/export-customized-entity-field-text-translation)) | `Entities/alex_pushchannelinstance/SavedQueries/*.xml` |
| Environment variable, custom APIs and their parameters | Translation file ([Translate table and column text](https://learn.microsoft.com/power-apps/maker/data-platform/export-customized-entity-field-text-translation)) | `environmentvariabledefinitions/`, `customapis/` |
| Channel definition and message parts | `msdyn_channeldefinitionlocale`, one record per language ([Custom channel localization](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-localization-admin)) | `Other/Customizations.xml` |
| `deliveryMode` option labels | No locale key exists ([Message parts](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-message-parts), [Custom channel localization](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-localization-admin)) | English only; explained in the Hebrew part description |
| Microsoft's `msdyn_channelinstance` labels | Shipped by Microsoft | Not changed |

## Rules

- **1033 is English and 1037 is Hebrew. No exceptions.** English is never copied into 1037, and no label is left empty or left to fall back to 1033.
- Write it as professional, plain-language Hebrew for marketers and admins, not a literal translation. Keep product names in Latin script: Customer Insights - Journeys, Azure, Push, API, URL.
- Glossary and all strings: `tools/l10n/he_glossary.py` (English text → Hebrew).
- Message part `msdyn_description` is at most 100 characters in both languages.
- `default="…"` attributes in the unpacked XML are the exporting user's UI language. `check_solution.py --fix` resets them to the 1033 text so the source doesn't depend on who exported it.

## Procedure

Requires `az login` with access to the environment, Python 3 with `lxml`, and `pac`.

```bash
ORG=https://<org>.crm.dynamics.com
# 0. Add or change labels in English (1033) first, then add each new English string with its Hebrew to tools/l10n/he_glossary.py

# 1. Export the translation file for the solution (ExportTranslation) ([ExportTranslation](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/exporttranslation))
python3 tools/l10n/translations.py export $ORG /tmp/tx.zip

# 2. Fill 1037 for every row we own. It fails and lists any English string missing from the glossary
python3 tools/l10n/translations.py fill /tmp/tx.zip /tmp/tx-he.zip

# 3. Import it (ImportTranslation) and publish the table ([ImportTranslation](https://learn.microsoft.com/power-apps/developer/data-platform/webapi/reference/importtranslation))
python3 tools/l10n/translations.py import $ORG /tmp/tx-he.zip

# 4. Verify DisplayName, Description and option labels per language in the metadata
python3 tools/l10n/translations.py verify $ORG

# 5. Bring the result into source control
pac solution export --environment $ORG --name AlexPushInboxChannel --path /tmp/s.zip --overwrite
pac solution unpack --zipfile /tmp/s.zip --folder /tmp/s --packagetype Unmanaged --allowDelete
#    copy /tmp/s over solution/AlexPushInboxChannel, keeping Entities/msdyn_ChannelInstance from Git
python3 tests/solution/check_solution.py --fix && python3 tests/solution/check_solution.py
```

You can also do steps 1–3 in the maker portal: **Solutions > … > Translations > Export / Import translations** ([Translate table and column text](https://learn.microsoft.com/power-apps/maker/data-platform/export-customized-entity-field-text-translation)). Hebrew must be enabled in the environment ([Enable languages](https://learn.microsoft.com/power-platform/admin/enable-languages)).

Channel definition and message part texts are edited directly in the two `msdyn_channeldefinitionlocale` records in `Other/Customizations.xml` ([Custom channel localization](https://learn.microsoft.com/dynamics365/customer-insights/journeys/real-time-marketing-custom-channel-localization-admin)).

## Solution check (pre-commit)

`tests/solution/check_solution.py` runs locally in the versioned pre-commit hook `.githooks/pre-commit`, and blocks the commit if the check fails. Enable the hook once per clone with `tools/setup-hooks.sh`. GitHub-hosted runners aren't available for this repository, so `.github/workflows/solution-check.yml` runs only when started manually. The check fails when:

- a localizable element is missing its 1033 or 1037 label, or the label is empty;
- a 1037 label contains no Hebrew letter (U+0590–U+05FF), for example English copied in;
- a 1033 label, or a `default=` attribute, contains Hebrew;
- a channel definition locale lacks a key that the other language has, or a key has the wrong language;
- a message part description is longer than 100 characters.

The same script checks the solution structure: the relationship file, the `Relationships.xml` line, the shell entity, lowercase GUIDs, a single channel definition, and the form ID.

## Verify in the UI

Set **Personal options > Languages > User interface language** to Hebrew ([Personal options (language)](https://learn.microsoft.com/power-apps/user/set-personal-options)) and open the app:

- The *שולחי פוש* views, the *מידע* form with Hebrew tabs and fields, and Hebrew column help text all appear in right-to-left layout.
- In Customer Insights - Journeys, the **Push ותיבת הודעות** channel shows Hebrew message part names and descriptions.
