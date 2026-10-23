# Third-party notices

PolyMigrate itself is licensed under the [MIT License](LICENSE). It builds on the
third-party components below. All of their licences are compatible with MIT, and none
of them are copyleft. The full licence texts are at the end of this file.

## What each channel ships

Whether a channel redistributes a component — and so has to carry its notices — depends
on how it is packaged:

| Channel | What is inside | Notices shipped with it |
|---|---|---|
| **`Cornhsu.PolyMigrate`** — .NET tool on NuGet | `PackAsTool` puts the whole publish output into the package (`tools/net10.0/any/`): every dependency DLL **and** Magick.NET's native libraries for all platforms (`runtimes/*/native/Magick.Native-Q8-*`, which contain ImageMagick). The tool package itself is a redistribution of all of them. | `NOTICE.txt` (Magick.NET's `Notice.txt`, verbatim) and this file, at the package root |
| **`Cornhsu.PolyMigrate.Core`** — library on NuGet | `PolyMigrate.Core.dll` only. Its dependencies are declared, not bundled: NuGet resolves them on the consumer's side, and each package carries its own licence. | Nothing further is required |
| **npm** — `cornhsu-polymigrate` and `@cornhsu/polymigrate-<os>-<cpu>` | Each platform package is a self-contained executable: every dependency DLL, the `Magick.Native-Q8-*` for that platform, **and the .NET runtime**. The main package holds only the launcher script. | Platform packages: `NOTICE.txt`, this file and `LICENSE`. Main package: this file and `LICENSE` |

## Components

| Component | Licence | Copyright | Project | Shipped in |
|---|---|---|---|---|
| AngleSharp | MIT | Copyright (c) 2013 - 2026 AngleSharp | https://anglesharp.github.io/ | tool, npm |
| ReverseMarkdown | MIT | Copyright (c) 2015 Babu Annamalai | https://github.com/mysticmind/reversemarkdown-net | tool, npm |
| YamlDotNet | MIT | Copyright (c) 2008, 2009, 2010, 2011, 2012, 2013, 2014 Antoine Aubry and contributors | https://github.com/aaubry/YamlDotNet | tool, npm |
| Magick.NET (`Magick.NET-Q8-AnyCPU`, `Magick.NET.Core` and the native `Magick.Native-Q8-*`) | Apache-2.0 | Copyright 2013-2026 Dirk Lemstra | https://github.com/dlemstra/Magick.NET | tool, npm |
| ImageMagick and the libraries built into `Magick.Native-Q8-*` | ImageMagick License; each bundled library under its own licence | ImageMagick Studio LLC and the respective authors — see `NOTICE.txt` | https://imagemagick.org/ | tool, npm |
| .NET runtime | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime | npm only |

The copyright lines are taken from each project's own licence file (Magick.NET: its package
metadata) at the versions this release resolves: AngleSharp 1.7.2, ReverseMarkdown 6.2.1,
YamlDotNet 18.1.0, Magick.NET 14.16.0. The exact versions inside a given package are listed
in its `polymigrate.deps.json`.

The .NET runtime contains third-party code of its own; its notices are at
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT.

## Magick.NET and ImageMagick: `NOTICE.txt`

Magick.NET is Apache-2.0 and its package includes a `Notice.txt`, so section 4(d) of the
Apache License applies: whoever redistributes it must pass that notice on. The file also
reproduces the ImageMagick License and the licence of every library built into the native
binaries, for the exact ImageMagick version of that Magick.NET release — which is why the
packages ship the file itself, not a summary of it.

**This is automated, not a checklist item.** Both channels that redistribute Magick.NET
resolve the Magick.NET version that was actually restored, read `Notice.txt` from the NuGet
cache, and ship it as `NOTICE.txt` — **and fail if they cannot find it**:

- **tool package:** the `_PackMagickNotice` target in `src/PolyMigrate.Cli/PolyMigrate.Cli.csproj`
  fails `dotnet pack`;
- **npm:** `npm/prepare.mjs` aborts, and `release.yml` then checks that every built package
  really carries `NOTICE.txt` and this file.

A licence obligation that depends on someone remembering a checklist is one that will
eventually be missed.

## Keeping this current

This file describes what the CLI's build output actually contains
(`src/PolyMigrate.Cli/bin/Release/net10.0`, listed in `polymigrate.deps.json`), not only what the
`.csproj` files reference: a build-time package can still leak a runtime DLL into the output.
**When a dependency is added, removed, or bumped, check the output and update this file in the
same commit** — Dependabot bumps will not do it for you.

## Licence texts

### MIT License

Applies to AngleSharp, ReverseMarkdown, YamlDotNet and the .NET runtime.

```text
MIT License

Copyright (c) 2013 - 2026 AngleSharp
Copyright (c) 2015 Babu Annamalai
Copyright (c) 2008, 2009, 2010, 2011, 2012, 2013, 2014 Antoine Aubry and contributors
Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Apache License 2.0

Applies to Magick.NET (`Magick.NET-Q8-AnyCPU`, `Magick.NET.Core` and the native
`Magick.Native-Q8-*`), Copyright 2013-2026 Dirk Lemstra. ImageMagick and the libraries built
into the native binaries are under their own licences, reproduced in full in `NOTICE.txt`.

```text

                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```
