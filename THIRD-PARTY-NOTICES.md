# Third-party notices

Gözleme bundles the following third-party material. Package dependencies are listed in
`Directory.Packages.props` and `web/package.json` and carry their own licences.

## Bundled specification schemas (GOZ-0006)

The files under `src/Gozleme.Api/Manifests/Schemas/upstream/` are unmodified copies of upstream schemas,
embedded in the API so manifests are validated offline. Sources, versions and SHA-256 digests are listed in
`src/Gozleme.Api/Manifests/Schemas/SOURCES.md`.

| Material | Copyright | Licence |
|---|---|---|
| Model Context Protocol schemas (`mcp-2026-07-28.json`, `mcp-2025-11-25.json`) and MCP Registry `server.schema.json` 2025-12-11 | © Model Context Protocol a Series of LF Projects, LLC, and contributors | MIT |
| OpenAPI Specification 3.1 and 3.0 JSON schemas | © The Linux Foundation, OpenAPI Initiative | Apache-2.0 |
| AsyncAPI 3.0.0 JSON schema (`asyncapi/spec-json-schemas` v6.11.1) | © The Linux Foundation, AsyncAPI Initiative | Apache-2.0 |
| A2A v1.0.1 JSON schemas, generated from `specification/a2a.proto` | © The Linux Foundation, A2A Project, and Google LLC | Apache-2.0 |

### MIT License (Model Context Protocol)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

### Apache License 2.0 (OpenAPI, AsyncAPI, A2A)

Licensed under the Apache License, Version 2.0 (the "License"); you may not use these files except in compliance
with the License. You may obtain a copy of the License at https://www.apache.org/licenses/LICENSE-2.0. Unless
required by applicable law or agreed to in writing, software distributed under the License is distributed on an
"AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License. The files are redistributed
unmodified.

## Signing libraries (GOZ-0007)

The API signs revision attestations with Ed25519 through these NuGet packages, pinned in `Directory.Packages.props`.
They are not modified.

| Package | Version | Source | Copyright | Licence |
|---|---|---|---|---|
| NSec.Cryptography | 26.4.0 | https://nsec.rocks, https://github.com/ektrah/nsec | © 2026 Klaus Hartke | MIT |
| libsodium (native binaries, a dependency of NSec) | 1.0.22 | https://libsodium.org, https://github.com/jedisct1/libsodium | © 2013-2026 Frank Denis | ISC |

### MIT License (NSec)

Copyright (c) 2026 Klaus Hartke

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

### ISC License (libsodium)

Copyright (c) 2013-2026 Frank Denis

Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby
granted, provided that the above copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN
AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
