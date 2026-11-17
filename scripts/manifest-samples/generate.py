#!/usr/bin/env python3
"""Generates web/src/samples/manifests/ (GOZ-0006 research R15): one envelope per file, native documents as exact
strings, and index.json with each sample's expected result. Synthetic content only: example.test hosts, no real
organizations, people, endpoints or credentials. Secret-like values are obviously fake (EXAMPLE, SYNTHETIC or
repeated characters) yet still match pattern list v1; they are listed in synthetic-secrets.json for the
no-secret scans. Run: python3 scripts/manifest-samples/generate.py"""
import json, os

ROOT = os.path.join(os.path.dirname(__file__), "..", "..", "web", "src", "samples", "manifests")
FOREIGN = "gz://registry.other.example.test/cap/0193a000-0000-7000-8000-0000000000ff"
SECRETS = {
    "github": "ghp_" + "EXAMPLE" * 6,
    "pem": "-----BEGIN PRIVATE KEY-----\nR0FMTEVNRS1TWU5USEVUSUMtS0VZ\n-----END PRIVATE KEY-----",
    "bearer": "Bearer SYNTHETICSYNTHETICSYNTHETIC",
    "aws": "AKIA" + "EXAMPLE" + "A" * 9,
    "userinfo": "https://demo:SYNTHETIC-PASSWORD@forecasts.example.test/",
    "openai": "sk-proj-" + "SYNTHETIC" * 3,
    "slack": "xoxb-" + "1" * 12 + "-SYNTHETIC",
}

def dump(o): return json.dumps(o, indent=2, ensure_ascii=False) + "\n"
def compact(o): return json.dumps(o, ensure_ascii=False, separators=(",", ":"))

MCP_STATELESS = {
    "protocolRevision": "2026-07-28",
    "serverInfo": {"name": "io.example.demo/forecasts", "title": "Forecasts", "version": "1.4.0", "description": "Synthetic demo server for forecasts."},
    "capabilities": {"tools": {"listChanged": False}, "resources": {}, "prompts": {}},
    "tools": [{"name": "get_forecast", "title": "Get forecast", "description": "Returns a synthetic forecast for a region.",
               "inputSchema": {"type": "object", "properties": {"region": {"type": "string"}}, "required": ["region"]},
               "annotations": {"readOnlyHint": True}, "_meta": {"io.example.demo/cost": "free"}}],
    "resources": [{"name": "regions", "uri": "https://forecasts.example.test/regions", "mimeType": "application/json"}],
    "prompts": [{"name": "summarize_week", "title": "Summarize the week", "arguments": [{"name": "region", "required": True}]}],
    "remotes": [{"type": "streamable-http", "url": "https://forecasts.example.test/mcp",
                 "headers": [{"name": "Authorization", "isSecret": True, "isRequired": True, "description": "Supplied by the client."}]}],
    "_meta": {"io.example.demo/build": "synthetic"},
}
MCP_STATEFUL = {
    "protocolRevision": "2025-11-25",
    "serverInfo": {"name": "io.example.demo/forecasts-local", "version": "1.4.0"},
    "capabilities": {"tools": {}, "logging": {}},
    "tools": [{"name": "get_forecast", "inputSchema": {"type": "object"}, "execution": {"taskSupport": "optional"}}],
    "packages": [{"registryType": "npm", "identifier": "@example/forecasts-mcp", "version": "1.4.0", "transport": {"type": "stdio"},
                  "environmentVariables": [{"name": "FORECAST_API_KEY", "isSecret": True, "description": "Supplied at install time."}]}],
    "_meta": {"io.example.demo/build": "synthetic"},
}
A2A = {
    "name": "Forecast Agent", "description": "Synthetic demo agent that forecasts weather.",
    "supportedInterfaces": [{"url": "https://agent.example.test/a2a", "protocolBinding": "JSONRPC", "protocolVersion": "1.0"}],
    "version": "1.4.0",
    "capabilities": {"streaming": True, "extensions": [{"uri": "https://example.test/ext/units", "description": "Unit preferences", "required": False, "params": {"defaultUnit": "celsius"}}]},
    "defaultInputModes": ["text/plain"], "defaultOutputModes": ["application/json"],
    "skills": [{"id": "forecast", "name": "Forecast", "description": "Forecasts a region.", "tags": ["weather"]},
               {"id": "alerts", "name": "Alerts", "description": "Lists synthetic alerts.", "tags": ["weather", "alerts"]}],
    "securitySchemes": {"apiKey": {"apiKeySecurityScheme": {"location": "header", "name": "X-Api-Key"}}},
}
OAS31_YAML = """openapi: 3.1.1
info:
  title: Forecasts API
  version: 2.0.0
  description: Synthetic demo API.
  x-audience: internal
paths:
  /forecasts/{region}:
    get:
      operationId: getForecast
      summary: Get a forecast
      deprecated: false
      parameters:
        - name: region
          in: path
          required: true
          schema: {type: string}
      responses:
        '200':
          description: A forecast.
          content:
            application/json:
              schema:
                $ref: '#/components/schemas/Forecast'
    post:
      summary: Submit an observation
      responses:
        '202':
          description: Accepted.
components:
  schemas:
    Forecast:
      type: object
      properties:
        region: {type: string}
        owner: {type: string}
"""
OAS30 = {
    "openapi": "3.0.4",
    "info": {"title": "Forecasts API (3.0)", "version": "2.0.0", "x-audience": "internal"},
    "paths": {"/forecasts": {"get": {"operationId": "listForecasts", "summary": "List forecasts",
              "parameters": [{"name": "X-Api-Key", "in": "header", "required": True, "schema": {"type": "string"}, "example": "secretref:vault/forecasts/api-key"}],
              "responses": {"200": {"description": "Forecasts."}}}}},
}
ASYNC = {
    "asyncapi": "3.0.0",
    "info": {"title": "Forecast Events", "version": "1.0.0", "description": "Synthetic demo events.", "x-audience": "internal"},
    "channels": {"forecastUpdated": {"address": "forecasts/{region}", "title": "Forecast updated",
                 "parameters": {"region": {"description": "Region code."}},
                 "messages": {"forecast": {"payload": {"type": "object", "properties": {"region": {"type": "string"}}}}}}},
    "operations": {"publishForecast": {"action": "send", "title": "Publish forecast", "channel": {"$ref": "#/channels/forecastUpdated"}}},
}
SKILL = """---
name: forecast-summary
description: Summarizes a synthetic weather forecast for a region.
license: Apache-2.0
metadata:
  author-team: demo
---
# Forecast summary

Use this skill to summarize a synthetic forecast. It calls no tools.
"""
MODEL = {
    "name": "forecast-small", "title": "Forecast Small", "description": "Synthetic demo model card.", "modelVersion": "2026.11",
    "modalities": {"input": ["text"], "output": ["text", "structured"]},
    "limits": {"contextWindowTokens": 32000, "maxOutputTokens": 4000},
    "x-benchmark": "synthetic",
}

def envelope(family, fmt, ver, enc, doc, title=None, capability="{{capability}}", **extra):
    e = {"envelopeVersion": 1, "family": family, "capability": capability, "version": "1.4.0"}
    if title: e["title"] = title
    e.update(extra.pop("top", {}))
    e["native"] = {"format": fmt, "formatVersion": ver, "encoding": enc, "document": doc}
    return e

samples = []
def add(id, family, title, env, expected):
    file = f"{family}/{id}.json"
    samples.append({"id": id, "family": family, "title": title, "file": file, "expected": expected})
    os.makedirs(os.path.join(ROOT, family), exist_ok=True)
    open(os.path.join(ROOT, file), "w").write(dump(env))

ok = {"outcome": "accepted"}
def rej(category, rule, part, at):
    loc = None if at is None else ({"part": part, "pointer": at} if isinstance(at, str) else {"part": part, "line": at[0], "column": at[1]})
    return {"outcome": "rejected", "category": category, "rule": rule, "location": loc}

# Valid samples (US1)
add("valid-stateless-2026-07-28", "mcp", "MCP server, stateless (2026-07-28)", envelope("mcp", "mcp-server-manifest", "1", "json", dump(MCP_STATELESS), "Forecasts MCP"), ok)
add("valid-stateful-2025-11-25", "mcp", "MCP server, stateful (2025-11-25)", envelope("mcp", "mcp-server-manifest", "1", "json", dump(MCP_STATEFUL)), ok)
add("valid-agent-card", "a2a", "A2A Agent Card 1.0", envelope("a2a", "a2a-agent-card", "1.0", "json", dump(A2A)), ok)
add("valid-openapi-3.1", "api", "OpenAPI 3.1 (YAML)", envelope("api", "openapi", "3.1", "yaml", OAS31_YAML, "Forecasts API"), ok)
add("valid-openapi-3.0", "api", "OpenAPI 3.0 (JSON)", envelope("api", "openapi", "3.0", "json", dump(OAS30)), ok)
add("valid-asyncapi-3.0.0", "event", "AsyncAPI 3.0.0", envelope("event", "asyncapi", "3.0.0", "json", dump(ASYNC)), ok)
add("valid-skill", "skill", "Agent Skill (SKILL.md)", envelope("skill", "agent-skills", "2026-11-17", "markdown", SKILL), ok)
add("valid-model-card", "model", "Model card v1", envelope("model", "gozleme-model-card", "1", "json", dump(MODEL)), ok)

import copy
def with_(base, path, value):
    d = copy.deepcopy(base); cur = d
    for k in path[:-1]: cur = cur[k]
    cur[path[-1]] = value
    return d

# Invalid samples (US2): at least one per category and per family; exact expected problem.
add("invalid-envelope-version", "model", "Unsupported envelope version",
    {**envelope("model", "gozleme-model-card", "1", "json", dump(MODEL)), "envelopeVersion": 2}, rej("unsupported_version", "envelope.version", "envelope", "/envelopeVersion"))
add("invalid-family", "model", "Unsupported family",
    envelope("workflow", "gozleme-model-card", "1", "json", dump(MODEL)), rej("unsupported_family", "envelope.family", "envelope", "/family"))
add("invalid-format-version", "api", "Unsupported OpenAPI line",
    envelope("api", "openapi", "3.2", "json", dump(OAS30)), rej("unsupported_format", "envelope.format", "envelope", "/native/formatVersion"))
add("invalid-version-mismatch", "event", "AsyncAPI version differs from the declared format",
    envelope("event", "asyncapi", "3.0.0", "json", dump(with_(ASYNC, ["asyncapi"], "3.1.0"))), rej("unsupported_format", "native.version-mismatch", "native", "/asyncapi"))
add("invalid-yaml-for-a2a", "a2a", "YAML declared for an Agent Card",
    envelope("a2a", "a2a-agent-card", "1.0", "yaml", "name: Forecast Agent\n"), rej("malformed", "parse.syntax", "envelope", "/native/encoding"))
add("invalid-yaml-alias", "api", "YAML alias",
    envelope("api", "openapi", "3.1", "yaml", OAS31_YAML.replace("  version: 2.0.0\n", "  version: &v 2.0.0\n")), rej("malformed", "parse.yaml-feature", "native", (4, 12)))
add("invalid-too-deep", "model", "Native document nested too deeply",
    envelope("model", "gozleme-model-card", "1", "json", '{"x-deep":' + "[" * 70 + "]" * 70 + "}"), rej("too_large", "bound.depth", None, None))
add("invalid-unknown-field", "mcp", "Unknown member on a stateless tool",
    envelope("mcp", "mcp-server-manifest", "1", "json", dump(with_(MCP_STATELESS, ["tools", 0, "execution"], {"taskSupport": "optional"}))),
    rej("unknown_field", "native.unknown-field", "native", "/tools/0/execution"))
add("invalid-reserved-openapi", "api", "Reserved trust claim in an OpenAPI extension",
    envelope("api", "openapi", "3.0", "json", dump(with_(OAS30, ["info", "x-verified"], True))), rej("reserved_claim", "reserved.vocabulary", "native", "/info/x-verified"))
add("invalid-a2a-signatures", "a2a", "Agent Card signatures",
    envelope("a2a", "a2a-agent-card", "1.0", "json", dump({**A2A, "signatures": [{"protected": "eyJhbGciOiJFUzI1NiJ9", "signature": "SYNTHETIC"}]})),
    rej("reserved_claim", "reserved.a2a-signatures", "native", "/signatures"))
add("invalid-reserved-envelope", "model", "Reserved claim in the envelope",
    envelope("model", "gozleme-model-card", "1", "json", dump(MODEL), top={"verified": True}), rej("reserved_claim", "reserved.vocabulary", "envelope", "/verified"))
add("invalid-secret-header", "mcp", "Inline credential in a secret header",
    envelope("mcp", "mcp-server-manifest", "1", "json", dump(with_(MCP_STATELESS, ["remotes", 0, "headers", 0, "value"], SECRETS["bearer"]))),
    rej("secret_value", "secret.field", "native", "/remotes/0/headers/0/value"))
add("invalid-secret-pattern", "model", "Credential-shaped text in a model card",
    envelope("model", "gozleme-model-card", "1", "json", dump(with_(MODEL, ["limitations"], "Rotate " + SECRETS["github"] + " monthly."))),
    rej("secret_value", "secret.pattern", "native", "/limitations"))
add("invalid-secret-skill-body", "skill", "Private key in a SKILL.md body",
    envelope("skill", "agent-skills", "2026-11-17", "markdown", SKILL + "\n" + SECRETS["pem"] + "\n"), rej("secret_value", "secret.pattern", "native", (12, 1)))
add("invalid-secret-openapi-example", "api", "Credential example on an Authorization header",
    envelope("api", "openapi", "3.0", "json", dump(with_(OAS30, ["paths", "/forecasts", "get", "parameters", 0], {"name": "Authorization", "in": "header", "schema": {"type": "string"}, "example": SECRETS["aws"]}))),
    rej("secret_value", "secret.field", "native", "/paths/~1forecasts/get/parameters/0/example"))
add("invalid-identity", "event", "Foreign capability claim",
    envelope("event", "asyncapi", "3.0.0", "json", dump(ASYNC), capability=FOREIGN), rej("identity_mismatch", "identity.capability", "envelope", "/capability"))
add("invalid-external-ref", "event", "External $ref",
    envelope("event", "asyncapi", "3.0.0", "json", dump(with_(ASYNC, ["operations", "publishForecast", "channel", "$ref"], "https://schemas.example.test/channel.json"))),
    rej("external_reference", "ref.external", "native", "/operations/publishForecast/channel/$ref"))
add("invalid-skill-name", "skill", "Skill name with upper case",
    envelope("skill", "agent-skills", "2026-11-17", "markdown", SKILL.replace("name: forecast-summary", "name: Forecast-Summary")), rej("invalid_value", "native.schema", "native", "/name"))
add("invalid-model-modality", "model", "Unknown model modality",
    envelope("model", "gozleme-model-card", "1", "json", dump(with_(MODEL, ["modalities", "input"], ["text", "smell"]))), rej("invalid_value", "native.schema", "native", "/modalities/input/1"))
add("invalid-mcp-consistency", "mcp", "Prompts without the prompts capability",
    envelope("mcp", "mcp-server-manifest", "1", "json", dump(with_(MCP_STATELESS, ["capabilities"], {"tools": {}, "resources": {}}))), rej("invalid_value", "native.consistency", "native", "/prompts"))
add("invalid-a2a-required", "a2a", "Agent Card without skills",
    envelope("a2a", "a2a-agent-card", "1.0", "json", dump({k: v for k, v in A2A.items() if k != "skills"})), rej("invalid_value", "native.schema", "native", "/skills"))

open(os.path.join(ROOT, "index.json"), "w").write(dump({"version": 1, "placeholder": "{{capability}}", "samples": samples}))
open(os.path.join(ROOT, "synthetic-secrets.json"), "w").write(dump(sorted(set(SECRETS.values()) | {"R0FMTEVNRS1TWU5USEVUSUMtS0VZ", "SYNTHETIC-PASSWORD"})))
# The web bundle: static imports, so Next includes every sample at build time (T042).
lines = ["// Generated by scripts/manifest-samples/generate.py. Do not edit.", 'import index from "./index.json";']
for i, sm in enumerate(samples):
    lines.append(f'import s{i} from "./{sm["file"]}";')
lines.append("")
lines.append("export const sampleIndex = index;")
lines.append("export const sampleFiles: Record<string, unknown> = {")
for i, sm in enumerate(samples):
    lines.append(f'  "{sm["file"]}": s{i},')
lines.append("};")
open(os.path.join(ROOT, "samples.generated.ts"), "w").write("\n".join(lines) + "\n")
print(len(samples), "samples")
