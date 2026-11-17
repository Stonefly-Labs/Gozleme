# Bundled schema sources

Every schema the manifest validator uses is embedded in the API assembly and resolved offline (GOZ-0006 FR-006,
research R1–R2). Upstream files are byte-for-byte copies and are never edited; Gözleme closes open upstream
objects with its own overlay files under `gozleme/`. `ManifestSchemaPinTests` recomputes every SHA-256 below
and fails when a file and this table disagree. Licence texts and notices: `THIRD-PARTY-NOTICES.md` at the
repository root.

| File | Source | Tag or version | SHA-256 | Licence |
|---|---|---|---|---|
| `upstream/asyncapi-3.0.0.json` | https://github.com/asyncapi/spec-json-schemas/blob/v6.11.1/schemas/3.0.0-without-$id.json | release `v6.11.1` | `abe96881dbfaad495ccbb6bd8d3fb43ca18a13b9d7564849ccb66f39d5e5b20f` | Apache-2.0 |
| `upstream/mcp-2025-11-25.json` | https://github.com/modelcontextprotocol/modelcontextprotocol/blob/2025-11-25/schema/2025-11-25/schema.json | tag `2025-11-25` | `1ffe4c5577974012f5fa02af14ea88df4b7146679df1abaaad497c8d9230ca8a` | MIT |
| `upstream/mcp-2026-07-28.json` | https://github.com/modelcontextprotocol/modelcontextprotocol/blob/2026-07-28/schema/2026-07-28/schema.json | tag `2026-07-28` | `ef70b61f99b6d2e5e3b46863822eab08dff6a45bedc7a08914e0e5b133f40203` | MIT |
| `upstream/mcp-registry-2025-12-11.json` | https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json | schema `2025-12-11` | `3fba09590c99f61735d234822279f4223fab9e300c0a81e81c91ab62a4114de0` | MIT |
| `upstream/oas-3.0-2024-10-18.json` | https://spec.openapis.org/oas/3.0/schema/2024-10-18 | schema `2024-10-18` | `2385f5bbb8c37878daae73baeabe7f34b2f022a4a8c049329ee61f71796f039c` | Apache-2.0 |
| `upstream/oas-3.1-2026-11-05.json` | https://spec.openapis.org/oas/3.1/schema/2026-11-05 | schema `2026-11-05` | `4751a7777357b26b09a9a61b9addd08081de2e65cf80d3ae0df2b4cd931b2d7b` | Apache-2.0 |
| `upstream/a2a-1.0.1/google.protobuf.Struct.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `f88b2bc843cd3032dbda93c54c5cdfecd4a3d3f7fdfe0552f79f1c63ad2a99d7` | Apache-2.0 |
| `upstream/a2a-1.0.1/google.protobuf.Timestamp.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `28c51aa58cab1609a2a8b2627a5ce3ed7fb15698b6616f7f211c72b11a985091` | Apache-2.0 |
| `upstream/a2a-1.0.1/google.protobuf.Value.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `980e8aea4150f5f7b50ac176bbbfeb028ba71692595efec0196ae182ecadadf5` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.APIKeySecurityScheme.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `8d9611a60146722d11564b8d8316f92b4c34e5d0a6d9ecfe4d801a70de4a9bf3` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.AgentCapabilities.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `1e8b3d8dce776971ca94dbf6eda08385a6b1d1d06ba5bc862729393af091c57e` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.AgentCard.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `79db7bc22c7e065bf88deff81a66a429964de3bc6e163fadaee6a5c7e9ec13fe` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.AgentCardSignature.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `d527926eb0123666704409261f7d234d1e7bd2d6668ca0d3bc45e15889a71b1d` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.AgentExtension.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `d2d976912b3b04d75a4816c1b5fb4e405767b4d1c8544d628804126ab4a03a0e` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.AgentInterface.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `1a55a410b23073a656b95f9f7be6f6368a637a3c354a350e9ddb1ad5d356c447` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.AgentProvider.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `f8419b379c9b2e0716983e07290451c51b19ead34e15cf9e16612e1e93c8e8bb` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.AgentSkill.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `1677c64fc0fabd09476446ca5d7331510eada4424847dc2d59d923fe7a8a6845` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.Artifact.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `037c107c21b86b385fec41a7b06380112f7daa0057f3679fcbe6360cf4a4bef8` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.AuthenticationInfo.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `16fb5a38569deff9cae98de18d2575b6edb71931fbe3aeb0a97b8ae70e3372d2` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.AuthorizationCodeOAuthFlow.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `43e5b40ca1d559d5389910b6c52af8381d1950c896d53e62724f316b36cbe8c4` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.CancelTaskRequest.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `537e995e827ff2936a45194aa4404a2e99c1ded10cf409971acda576002ea1e6` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.ClientCredentialsOAuthFlow.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `8fa48f6da3005d882f26952b18079461c6b26d3a532e9d7d436ab865c31dfbf6` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.DeleteTaskPushNotificationConfigRequest.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `0a9f9ebd35bc30df15c60b7a9f5e32aa98b45bab37ea65296dab764a36b172ba` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.DeviceCodeOAuthFlow.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `66f4b0e832ed43208f09cf75679194a7fed07072aaf95b065c18c64742fae10c` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.GetExtendedAgentCardRequest.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `77ccf5066d26a45bec7b0bcd0833c4bf9ee92237883ba3b1fea70d716b0d4599` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.GetTaskPushNotificationConfigRequest.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `0c99165dd92dd6e8549070d71d3615d33198c06f0f59830cf9286d221605c0c7` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.GetTaskRequest.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `9f9cb286b4d680e1b76a230dd6e30581fcad19040c9fa85e02988c02f302e383` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.HTTPAuthSecurityScheme.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `351e6c4a209107ce1c4c87ac009fc3923d0342d6fc40fdd2c9a1a669978346ea` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.ImplicitOAuthFlow.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `a1b24783bed116f3fd563cf1189730f5e37705a4a0e51f59757bf5991bfdaff3` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.ListTaskPushNotificationConfigsRequest.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `2fa4f8b135a80da056d5f0099702616a957ef3966ab094815b88fe98bccf17fd` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.ListTaskPushNotificationConfigsResponse.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `983cd31eee37914f1d2f5a88e4df9b4dfa5e6cc23040af3dd5a9bc4de65f6f9c` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.ListTasksRequest.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `1bf9e50d123d65029bf1101f6bdc0ad4a6a3a2fc678f10a6cf671cbb5b49b6c1` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.ListTasksResponse.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `3b6aae75b51e98105931fc78d1737cf0500c894290faf0f9961c24303230435a` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.Message.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `f652b7614e912defa95181ef0a5576e7ce537f099219edc6285bf74a1fd1b3f3` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.MutualTlsSecurityScheme.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `802ef5045094addfeebeb599c1a033f3865bfad39d593e2ba6e97b3af2d14120` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.OAuth2SecurityScheme.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `ae7bc214ac5e58113863bafeed557338872b1df9c2bb160497db66015b32d1ba` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.OAuthFlows.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `db56d0731e1b021b945c189b81f8a761dbd92102488b23049c44afc0dd516be1` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.OpenIdConnectSecurityScheme.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `bbad709045504cb0a3f2b306fc663f355740aa31aad07587cbf337bdc2ee9e68` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.Part.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `d209d63ca64282d06e416bf080b13269477779c28c1a3cd95402f240671bdbe9` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.PasswordOAuthFlow.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `7aea15936d9eb4b18bacc85650c3344448541b37e414b033cb33a1a92c8d152e` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.SecurityRequirement.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `6043a00b23165e77ce7b69613e1cc8d0d23520bf301d631a0fe9e5fe6b214f47` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.SecurityScheme.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `48980d3cc3d2e1b8183dfa36022c816725da965abfe1be2682107e18e6a84474` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.SendMessageConfiguration.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `6c75096f5490967ee39337f8d9431475706304fd765363e6ac827bcee08374f2` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.SendMessageRequest.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `9c5b43d20e923c5271e07399fbd01e814fd752ce496186f7850458ea6c560c1d` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.SendMessageResponse.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `e5590b23d077bae917e713e250a4a269e60426273fafdc59bdf9a85885e7dd90` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.StreamResponse.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `4a3a4ed0a76772314a4b7c05e0b95de6a620faeb431a24e82cd7e6f4ec80331b` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.StringList.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `2961d13daf1df57c9e0125e3e3f317ce6affcb804100ce72f58cf0bb88481a09` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.SubscribeToTaskRequest.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `51c7c3a4db929ed2670dc69c45690e01bfb3e0c85cf3f75c9ede645812a7c0e6` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.Task.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `17cec0c4fdf9beee32c309ea085552de8a5bd0098d7b7d4d4abfa04e13cc2d59` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.TaskArtifactUpdateEvent.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `30c34b9dba5cbfa3c7361e1c61a852e6648547e84fb24bc7e82c84062a610997` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.TaskPushNotificationConfig.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `717ceced65825c32ce33225c8989f05e23b370b92e9d32e5585a6dc39d8fce3d` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.TaskStatus.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `7f11f581ab56e5936015bb6d88f4026de4d9ef834b076b85abb29841116f011b` | Apache-2.0 |
| `upstream/a2a-1.0.1/lf.a2a.v1.TaskStatusUpdateEvent.jsonschema.json` | https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto | generated from tag `v1.0.1` | `3bf737cbd8042766c6694f0c3e661243c001fbdff9487b4f4c1c4e4573819395` | Apache-2.0 |
| `gozleme/a2a-strict-1.0.1.json` | Gözleme (GOZ-0006, docs/specs/006-typed-manifests/contracts/) | — | `6aadb54a85c24c7862ccb5468406f5f582e074baa9f2cad6e0f19468fd70063b` | Gözleme |
| `gozleme/agent-skills-2026-11-17.json` | Gözleme (GOZ-0006, docs/specs/006-typed-manifests/contracts/) | — | `7f9b195adc06fa2f115821496b2939fd258d6199813a9d89846508e43861f5cd` | Gözleme |
| `gozleme/envelope-1.json` | Gözleme (GOZ-0006, docs/specs/006-typed-manifests/contracts/) | — | `aa91b5b231c0fbc1764ae21de5909db9f80661ddfaf131a175ad01e8ce970887` | Gözleme |
| `gozleme/mcp-server-manifest-1.json` | Gözleme (GOZ-0006, docs/specs/006-typed-manifests/contracts/) | — | `a323e9534495363baf3e5e803ff42dfee6ddff942e3e27653a3140b5596c4138` | Gözleme |
| `gozleme/mcp-strict-2025-11-25.json` | Gözleme (GOZ-0006, docs/specs/006-typed-manifests/contracts/) | — | `57bb065e63a55a45b9c3064112f5c71a407efa4ceaf775aeacdc69a76543b766` | Gözleme |
| `gozleme/mcp-strict-2026-07-28.json` | Gözleme (GOZ-0006, docs/specs/006-typed-manifests/contracts/) | — | `60fae34e0a7df686878a966ff5fb4450389da6c4a1a3ec4047c3ba540b0b7eb1` | Gözleme |
| `gozleme/model-card-1.json` | Gözleme (GOZ-0006, docs/specs/006-typed-manifests/contracts/) | — | `f71ea4484740dfbe028c153a7fba411f6fe50e8d11a1d0f86e1c6d0baba7a8ff` | Gözleme |
| `gozleme/projection-1.json` | Gözleme (GOZ-0006, docs/specs/006-typed-manifests/contracts/) | — | `04a445153709e0533bfa7054f2bf0d8b290f51d3aef3fbdbf417b85da032c505` | Gözleme |

## Overlays (`gozleme/`)

- `envelope-1.json`, `mcp-server-manifest-1.json` and `model-card-1.json` are the contract files in
  `docs/specs/006-typed-manifests/contracts/`, copied unchanged.
- `mcp-strict-<revision>.json` reference the upstream MCP `$defs` of that revision and the `server.json`
  `definitions`, adding `unevaluatedProperties: false` (research R2). Confirmed at vendoring (T005): the
  `server.json` remote shape is `RemoteTransport` (the contract draft said `Remote`), and upstream
  `Implementation` has no `_meta`, so the overlay adds `_meta` on `serverInfo` as the contract's extension
  point.
- `a2a-strict-1.0.1.json` restates the 32 `(google.api.field_behavior) = REQUIRED` fields reachable from
  `AgentCard` in `a2a.proto` v1.0.1, which the generator drops.
- `agent-skills-2026-11-17.json` transcribes the agentskills.io front-matter rules as read on 2026-11-17.
- `projection-1.json` is the closed projection v1 shape (contracts/projection-v1.md).

## upstream/a2a-1.0.1/ (T004)

- **Source**: `specification/a2a.proto` at A2A tag `v1.0.1` (https://github.com/a2aproject/A2A), licence Apache-2.0.
- **Generator**: `protoc` with bufbuild `protoc-gen-jsonschema` v0.6.0 (github.com/bufbuild/protoschema-plugins), exactly as in A2A's `scripts/proto_to_json_schema.sh` at `v1.0.1` (the same `// --8<--` and `// protolint:` line filter, `--jsonschema_opt=target=json`), but **without the final `jq` stitching step** (maintainer decision, 2026-11-17). Run on 2026-11-17 in Docker `golang:1.25-bookworm` with `libprotoc 3.21.12`, `go1.25.14`, and googleapis at commit `c32bc6b4be2cb07ef3e06c57f2b44e2451a9b7c7`.
- **Command**: `grep -v -e "// --8<--" -e "// protolint:" specification/a2a.proto > $T/a2a.proto && protoc -I$T -Ispecification -I<googleapis> --jsonschema_out=<out> --jsonschema_opt=target=json $T/a2a.proto`
- **Output**: 47 files, unmodified; each file's `$id` equals its file name, and every relative `$ref` resolves within the set. Per-file SHA-256 values are in the table above.
- **Why unstitched**: A2A's stitched `a2a.json` (published at https://a2a-protocol.org/v1.0.1/spec/a2a.json, SHA-256 `ba4b702b5edbdcbfe972c272484eb38e1fcd72af3c0b7eaa114b50e97455d8dd`, which our stitched run reproduced byte for byte) deletes each `$id` but keeps 33 relative `$ref`s to these files, so it cannot be compiled on its own.
- **Known gap**: the generator does not emit `required` for `(google.api.field_behavior) = REQUIRED` fields. `gozleme/a2a-strict-1.0.1.json` (T005) restates them.
