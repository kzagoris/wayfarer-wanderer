# Syntax colours: third-party notices

Wayfarer Wanderer colours files with TextMate grammars, read by the `Tint` package. This file names what that uses
and under what terms. The licence texts it refers to are beside it: `MIT.txt`, `Apache-2.0.txt` and `EPL-2.0.txt`.

## Packages

| Package | Used for | Licence | Copyright |
|---|---|---|---|
| `Tint` | TextMate grammar tokenization and colour mapping | MIT | Copyright (c) 2026 Sébastien Ros |
| `GregsEx` | Tint's managed Oniguruma-compatible regular expressions | MIT | Copyright (c) 2026 Sébastien Ros |

## TextMate grammars

The grammars are embedded in the application. Each is a file of Visual Studio Code's own extensions
(https://github.com/microsoft/vscode, MIT, Copyright (c) 2015 - present Microsoft Corporation), unchanged, from the
project its header names:

- `java.tmLanguage.json` (VS Code 1.146.0, `extensions/java`): from redhat-developer/vscode-java
  (https://github.com/redhat-developer/vscode-java) at commit f09b712f, `language-support/java/java.tmLanguage.json`,
  EPL-2.0, Copyright (c) Microsoft Corporation, Copyright (c) 2004, 2006 The Linux Foundation and its contributors, as
  VS Code's notices state. The licence's text is `EPL-2.0.txt`.
- `groovy.tmLanguage.json` (VS Code 1.146.0, `extensions/groovy`): converted from textmate/groovy.tmbundle
  (https://github.com/textmate/groovy.tmbundle) at commit 85d8f7c9, under the TextMate bundle licence below. It colours
  Groovy and Gradle's Groovy builds.
- `xml.tmLanguage.json` (VS Code 1.144.0, `extensions/xml`): from atom/language-xml
  (https://github.com/atom/language-xml), MIT, Copyright (c) 2014 GitHub Inc., which was converted from the TextMate
  bundle textmate/xml.tmbundle, under the TextMate bundle licence below.
- `JSON.tmLanguage.json` (VS Code 1.144.0, `extensions/json`): from microsoft/vscode-JSON.tmLanguage
  (https://github.com/microsoft/vscode-JSON.tmLanguage), MIT, Copyright (c) 2015 simonzack, zertosh, benvie.
- `yaml.tmLanguage.json`, with `yaml-1.0.tmLanguage.json`, `yaml-1.1.tmLanguage.json`, `yaml-1.2.tmLanguage.json`,
  `yaml-1.3.tmLanguage.json` and `yaml-embedded.tmLanguage.json`, which it reads by (VS Code 1.144.0,
  `extensions/yaml`): from RedCMD/YAML-Syntax-Highlighter (https://github.com/RedCMD/YAML-Syntax-Highlighter) at
  commit 53d38bbc, its 1.3.2, MIT, Copyright 2024 RedCMD.
- `ini.tmLanguage.json` (VS Code 1.144.0, `extensions/ini`): converted from textmate/ini.tmbundle
  (https://github.com/textmate/ini.tmbundle), under the TextMate bundle licence below. It colours properties files and
  .editorconfig files.
- `markdown.tmLanguage.json` (VS Code 1.144.0, `extensions/markdown-basics`): from microsoft/vscode-markdown-tm-grammar
  (https://github.com/microsoft/vscode-markdown-tm-grammar), MIT, Copyright (c) Microsoft 2018, derived from
  textmate/markdown.tmbundle, under the TextMate bundle licence below.
- `shell-unix-bash.tmLanguage.json` (VS Code 1.144.0, `extensions/shellscript`): from jeff-hykin/better-shell-syntax
  (https://github.com/jeff-hykin/better-shell-syntax), MIT, Copyright (c) 2019 Jeff Hykin.

And one that colours Kotlin's .kt and .kts files, Gradle's Kotlin builds among them: `Kotlin.tmLanguage.json`, the
file `dist/Kotlin.JSON-tmLanguage` of nishtahir/language-kotlin (https://github.com/nishtahir/language-kotlin,
Apache-2.0, Copyright 2018 Nish Tahir) at commit a4c6c660, unchanged but for its name. The licence's text is
`Apache-2.0.txt`.

When it reads them, Wayfarer leaves out what VS Code's reader passes over (a capture that is not a rule) and lets a rule
that begins with no end never end, as VS Code does. It takes YAML's typed scalars (booleans, numbers, null) out of a
block or flow mapping's keys, so a key such as `on` is read as a key, and it ends a shell heredoc only at a line that is
its delimiter alone, as bash does. The grammar files themselves are unchanged.

The MIT licence, for Tint, GregsEx and the grammars above under it:

```
Copyright (c) 2026 Sébastien Ros
Copyright (c) 2015 - present Microsoft Corporation
Copyright (c) 2014 GitHub Inc.
Copyright (c) 2015 simonzack, zertosh, benvie
Copyright 2024 RedCMD
Copyright (c) Microsoft 2018
Copyright (c) 2019 Jeff Hykin
```

with the terms in `MIT.txt`.

The TextMate bundles textmate/xml.tmbundle, textmate/markdown.tmbundle, textmate/ini.tmbundle and
textmate/groovy.tmbundle, each in its README:

```
If not otherwise specified (see below), files in this repository fall under the following license:

	Permission to copy, use, modify, sell and distribute this
	software is granted. This software is provided "as is" without
	express or implied warranty, and with no claim as to its
	suitability for any purpose.

An exception is made for files in readable text which contain their own license information, or files where an
accompanying file exists (in the same directory) with a "-license" suffix added to the base-name name of the original
file, and an extension of txt, html, or similar. For example "tidy" is accompanied by "tidy-license.txt".
```
