# OpenStrate ⇄ Revit Bridge

A C# Revit add-in that closes the last mile of the OpenStrate scan-to-BIM pipeline:
it pulls the pipeline's IFC deliverable straight from the live service, opens it in
Revit, and **audits what Revit actually ingested against the pipeline's own element
manifest** (`/ifc.json`) — the authoritative source of truth.

Pipeline: D455 capture → reconstruction → PTv3/SAM3 segmentation → `pointcloud_to_ifc`
→ IFC → **this add-in (Revit-side QA gate)**.

## Commands

| Command | What it does |
|---|---|
| **OpenStrate: Ingest Scan** | `GET /download` from the OpenStrate service → opens the IFC as a Revit document (`OpenIFCDocument`) |
| **OpenStrate: Audit vs Manifest** | `GET /ifc.json` → `FilteredElementCollector` walk → diffs IFC classes / wall counts / element census → TaskDialog summary + `revit_audit_*.json` |

Design rules baked in:
- **No network inside a transaction.** All HTTP completes before any document work.
- **Identity = `UniqueId`**, never `ElementId` (which is not stable across sessions).
- Audit command is `TransactionMode.ReadOnly` — a QA gate mutates nothing.

## Build

Requires: .NET 8 SDK, Revit 2025 installed (references `RevitAPI.dll` in place, `Private=false`).

```powershell
cd openstrate-revit-bridge
dotnet build -c Release
```

The build auto-deploys `OpenStrateBridge.dll` + the `.addin` manifest into
`%AppData%\Autodesk\Revit\Addins\2025\`. Start Revit → Add-Ins → External Tools.

## Roadmap

- [ ] v2: link via `RevitLinkType` instead of open-as-document (workshared-friendly)
- [ ] Enrichment command: map segmentation labels → Revit categories/parameters/schedules
- [ ] Round-trip: Revit selection → `POST /relabel` back into the pipeline
- [ ] Headless runs via Autodesk Platform Services Design Automation (CI gate on GCP)
