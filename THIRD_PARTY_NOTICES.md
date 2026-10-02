# Third-party notices

The self-contained binaries include .NET runtime 9.0.20. The Linux server uses Microsoft.NETCore.App; the Windows app and CLI also use Microsoft.WindowsDesktop.App. Each package includes their license and third-party notice files under `licenses/`. CoreApp files come from the matching 9.0.20 runtime packages. The WindowsDesktop license comes from its 9.0.20 runtime package; its third-party notices come from the matching .NET SDK 9.0.318 because the runtime package does not include that file. These licenses cover the bundled runtime.

AudioLink uses NAudio 3.1.0 and its components under the MIT license reproduced below. AudioLink's own license status is described in [README.md](README.md#license).

NAudio source: [naudio/NAudio](https://github.com/naudio/NAudio).
License source: [upstream LICENSE](https://raw.githubusercontent.com/naudio/NAudio/main/LICENSE).

## Original NAudio license

Copyright 2008-2026 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

