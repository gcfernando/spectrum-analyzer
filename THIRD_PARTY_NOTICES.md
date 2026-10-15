# Third-party dependency notices and provenance

Reviewed on 2026-10-14. This file records verified dependency information,
attributions and available license texts. It does not grant additional rights,
certify a publisher's identity or purchase history, or declare any existing use
unauthorized. Application build/test acceptance and a publisher's compliance
with the terms of a particular distribution are separate questions.

No registration material is reproduced here. Existing registration and
third-party binaries have not been changed.

## Bundled audio dependencies

| File | File version | Verified attribution / source | License review |
| --- | --- | --- | --- |
| `Spectrum\Ref_Files\bass.dll` | 2.4.10 | Un4seen Developments; binary copyright 1999-2013 | Proprietary, conditional vendor license; see below. |
| `Spectrum\Ref_Files\basswasapi.dll` | 2.4 | Un4seen Developments; binary copyright 2009-2014 | Official BASS extension; exact historical distribution terms were not included in this checkout or independently recovered. Do not infer an unrestricted license. |
| `Spectrum\Ref_Files\Bass.Net.dll` | 2.4.11.1 | Copyright 2005-2015 radio42, Bernd Niedergesaess, Germany | Proprietary BASS.NET license; see below. |
| `Spectrum\Ref_Files\NAudio.dll` | 1.7.3.0 | Copyright 2001-2014 Mark Heath | Microsoft Public License (Ms-PL), reproduced below. Exact binary matches the official NAudio 1.7.3 NuGet package. |

### NAudio 1.7.3 exact-package evidence

Official package:
https://api.nuget.org/v3-flatcontainer/naudio/1.7.3/naudio.1.7.3.nupkg

Both the bundled `NAudio.dll` and that package's `lib/net35/NAudio.dll` have SHA-256:

`332D9CAF9C0172AABD7FF8CA909967D31DC17329B64B65D1FB13B84C6CA5A729`

The following Ms-PL text is from the exact package's `license.txt`.
The package metadata credits Mark Heath. A matching historical upstream license
is available at:
https://raw.githubusercontent.com/naudio/NAudio/NAudio_1.7.2_Release/license.txt

Do not label this historical binary MIT merely because newer NAudio releases
use another license. Ms-PL section 3(C) requires retention of existing notices;
section 3(D) distinguishes source distribution from compiled/object-code
distribution.

### Microsoft Public License (Ms-PL)

This license governs use of the accompanying software. If you use the software, you accept this license. If you do not accept the license, do not use the software.

1. Definitions

The terms "reproduce," "reproduction," "derivative works," and "distribution" have the same meaning here as under U.S. copyright law.

A "contribution" is the original software, or any additions or changes to the software.

A "contributor" is any person that distributes its contribution under this license.

"Licensed patents" are a contributor's patent claims that read directly on its contribution.

2. Grant of Rights

(A) Copyright Grant- Subject to the terms of this license, including the license conditions and limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free copyright license to reproduce its contribution, prepare derivative works of its contribution, and distribute its contribution or any derivative works that you create.

(B) Patent Grant- Subject to the terms of this license, including the license conditions and limitations in section 3, each contributor grants you a non-exclusive, worldwide, royalty-free license under its licensed patents to make, have made, use, sell, offer for sale, import, and/or otherwise dispose of its contribution in the software or derivative works of the contribution in the software.

3. Conditions and Limitations

(A) No Trademark License- This license does not grant you rights to use any contributors' name, logo, or trademarks.

(B) If you bring a patent claim against any contributor over patents that you claim are infringed by the software, your patent license from such contributor to the software ends automatically.

(C) If you distribute any portion of the software, you must retain all copyright, patent, trademark, and attribution notices that are present in the software.

(D) If you distribute any portion of the software in source code form, you may do so only under this license by including a complete copy of this license with your distribution. If you distribute any portion of the software in compiled or object code form, you may only do so under a license that complies with this license.

(E) The software is licensed "as-is." You bear the risk of using it. The contributors give no express warranties, guarantees or conditions. You may have additional consumer rights under your local laws which this license cannot change. To the extent permitted under your local laws, the contributors exclude the implied warranties of merchantability, fitness for a particular purpose and non-infringement.

### BASS and BASSWASAPI

Official current vendor licensing statement:
https://www.un4seen.com/bass.html

That page permits free BASS use by qualifying non-commercial entities in
non-money-making products; otherwise an appropriate license is required.
It describes product/platform scope and prohibits reselling/sublicensing.
It also lists BASSWASAPI as an official add-on.

This is a review of the published terms, not evidence that the owner does or
does not qualify. The bundled binaries predate the current releases. Confirm
the applicable original archive/license terms and the intended publisher,
product, platform and commercial use before a particular redistribution.
The official archive could not be retrieved during this review because of a
certificate/retrieval failure; certificate checks were not bypassed.

### BASS.NET

Official vendor terms, dated January 2015:
https://www.radio42.com/bass/bass_purchase.html
https://www.radio42.com/bass/bass_register.html

The published freeware license is conditional on an individual person's
non-commercial, personal-use, non-money-making freeware. It permits distributing
the DLL part with qualifying freeware, does not permit transfer of a personal
registration key, and requires a separate agreement for commercial/company
use. A BASS license is a separate matter.

This file does not infer the type or validity of the owner's existing license
from application behavior or from the presence of registration code. A
publisher should confirm the agreement that actually covers its intended use.
Do not redistribute registration material as a substitute for that agreement.

### Historical Windows Media Format wrapper

`Spectrum\Ref_Files\NAudio.WindowsMediaFormat.dll` remains an unchanged historical
source-tree asset, but is not referenced or shipped by the current application.
Neither the executable nor its NAudio dependency references this separate
wrapper. The following provenance notes concern that retained historical asset,
not an additional runtime component.

The historical project identifies assembly/file version 1.0.1.0 and copyright
2008:
https://raw.githubusercontent.com/naudio/NAudio/NAudio_1.7.2_Release/NAudio.WindowsMediaFormat/Properties/AssemblyInfo.cs

Historical interop source includes an original Idael Cardoso copyright
2002-2004 and a notice allowing free distribution while its header remains
unchanged, with later modifications credited to Yuval Naveh and Mark Heath:
https://raw.githubusercontent.com/naudio/NAudio/NAudio_1.7.2_Release/NAudio.WindowsMediaFormat/Interop/WMFFunctions.cs

These source references support a provenance investigation but are not an
exact build/hash match for the bundled wrapper. Obtain its original
distribution and preserve all applicable notices when preparing a
redistribution; this notice does not invent a missing license grant.

## Microsoft runtime packages

The application manifest declares:

- System.Buffers 4.5.0
- System.Memory 4.5.3
- System.Numerics.Vectors 4.5.0
- System.Resources.Extensions 4.6.0
- System.Runtime.CompilerServices.Unsafe 4.5.2

All five runtime package metadata/license records identify the MIT license and
include the identical `LICENSE.TXT` below, referring to the dotnet/corefx license.
The application uses the .NET Framework's `System.Configuration`; the unused
`System.Configuration.ConfigurationManager` NuGet reference and output DLL have
been removed. For historical provenance, ConfigurationManager 10.0.1 declares
`MIT` and identifies
the dotnet/dotnet repository commit
`fad253f51b461736dfd3cd9c15977bb7493becef`, whose license matches this text:
https://raw.githubusercontent.com/dotnet/dotnet/fad253f51b461736dfd3cd9c15977bb7493becef/LICENSE.TXT

Original restored-package license/notice files are under
`packages\<package-id>.<version>\`. Each package includes a
`THIRD-PARTY-NOTICES.TXT` aggregate, with differing contents in some versions.
When packaging these DLLs, retain the corresponding original aggregate
notices as well as this MIT text; this overview does not replace them.
Do not assume every upstream aggregate entry is used by Spectrum, or remove
entries without establishing applicability.

### The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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

## Distribution boundary

The xUnit and Microsoft.NET.Test.Sdk packages in `Spectrum.Tests` are test
tooling, not an additional grant for the runtime application. This file
addresses the application dependencies above, not redistribution of a complete
test SDK, operating-system components or the .NET Framework installer.

Their exact published NuGet package metadata was also reviewed:

| Declared test dependency | Declared license | Official package |
| --- | --- | --- |
| Microsoft.NET.Test.Sdk 17.14.1 | MIT | https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/17.14.1 |
| xunit 2.9.3 | Apache-2.0 | https://www.nuget.org/packages/xunit/2.9.3 |
| xunit.runner.visualstudio 3.1.5 | Apache-2.0 | https://www.nuget.org/packages/xunit.runner.visualstudio/3.1.5 |

Do not redistribute test tooling using only the runtime license texts above;
retain its own applicable license and notices if it is separately packaged.

A technical dependency/license review can be completed without certifying
private commercial agreements. Missing purchase records alone are not a
source-code defect, a demonstrated license breach, or a reason to mark a
successful local build/test as failed. Conversely, build success cannot
certify unrestricted distribution rights.

Before a specific binary distribution, include verified notices in the
distributed artifact, recover the historical wrapper/add-on terms where
needed, and have the publisher confirm that its actual use meets the applicable
conditions. The Release build combines this overview and all five shipped Microsoft
packages' complete original aggregate notices into one root
`THIRD_PARTY_NOTICES.md`, preserving their bytes without separate `ThirdParty`
folders. Debug builds retain the individual notices for development. These
notices do not certify the publisher's private agreements.
