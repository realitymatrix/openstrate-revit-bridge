// MCP stdio proxy for the OpenStrate Revit Bridge.
// Speaks Model Context Protocol on stdio; forwards every tool call to the
// bridge server running INSIDE Revit (127.0.0.1:8090), which marshals it onto
// Revit's UI thread via an ExternalEvent. Connect any MCP client (Claude Code,
// Claude Desktop, Cursor) and converse with a live Revit session.
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const BRIDGE = process.env.REVIT_BRIDGE_URL || "http://127.0.0.1:8090";

async function callBridge(tool, args = {}) {
  const resp = await fetch(`${BRIDGE}/call`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ tool, args }),
    signal: AbortSignal.timeout(tool === "ingest" || tool === "open_host" ? 900_000 : 65_000),
  });
  const body = await resp.json();
  if (!body.ok) throw new Error(body.error || `Bridge error (HTTP ${resp.status})`);
  return body.result;
}

const server = new McpServer({ name: "revit-bridge", version: "0.1.0" });

// Static registrations with real schemas (the bridge's catalog is the source
// of the tool list; schemas here make the tools self-describing to the model).
const TOOLS = [
  {
    name: "open_host",
    description: "Ensure an active host project exists in Revit (creates and activates one if needed). Run before ingest if Revit was just started.",
    schema: {},
  },
  {
    name: "ingest",
    description: "Fetch the latest scan-to-BIM IFC from the OpenStrate service and link it into the Revit host project. Takes minutes for large scans.",
    schema: {},
  },
  {
    name: "model_stats",
    description: "Element census of the ingested scan (or active document): counts by category and by level.",
    schema: {},
  },
  {
    name: "query_elements",
    description: "List elements in the model, optionally filtered.",
    schema: {
      category: z.string().optional().describe("Revit category name, e.g. 'Walls', 'Furniture'"),
      name_contains: z.string().optional().describe("Substring filter on element name"),
      limit: z.number().int().min(1).max(500).optional().describe("Max results (default 50)"),
    },
  },
  {
    name: "get_element",
    description: "Full parameter dump for one element by its stable UniqueId. Doubles are Revit internal units (feet).",
    schema: { unique_id: z.string().describe("Element UniqueId (from query_elements)") },
  },
];

for (const t of TOOLS) {
  server.tool(t.name, t.description, t.schema, async (args) => {
    try {
      const result = await callBridge(t.name, args ?? {});
      return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
    } catch (e) {
      return { content: [{ type: "text", text: `ERROR: ${e.message}` }], isError: true };
    }
  });
}

await server.connect(new StdioServerTransport());
