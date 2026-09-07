# ELB-DockTool v1.2.0

Windows desktop application developed in C# using .NET 8 and Windows Forms.

## Workflow
- Receptor sanity/normalization, PDBFixer/OpenMM repair, cleanup, optional polar-hydrogen addition, and Meeko PDBQT preparation.
- Ligand inspection/preparation and Meeko PDBQT preparation.
- AutoDock Vina docking, result review, pose separation, and PDB/MOL2 conversion.
- IMPPAT 2.0 phytochemical retrieval and compound selection.
- WebView2 + 3Dmol.js molecular interpretation viewer with protein/ligand visualization, pose comparison, nearby-residue analysis, H-bond display, CSV/PNG export, and PDB complex export.

## Requirements
- Windows x64.
- `dist/` contains the bundled AutoDock Vina executables.
- Receptor preparation requires a compatible Python/Meeko environment when that workflow is used.
- IMPPAT functions may require internet access.
- The interpretation viewer currently loads 3Dmol.js from its official CDN.

See `LICENSE.txt` and `THIRD-PARTY-NOTICES/` for licensing.
