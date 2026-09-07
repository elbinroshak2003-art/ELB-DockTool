# ELB-DockTool v1.2.0 — Integrated Independent Docking Interpretation

This package integrates a WebView2-hosted, independently authored docking interpretation interface into the ELB-DockTool WinForms application. The Results page contains **VIEW COMPLEX**, which opens the selected receptor/Vina result in the interpretation viewer.

## Integrated features
- 3D protein cartoon representation
- Docked ligand sticks/spheres
- Binding-site residue highlighting
- Optional molecular surface
- Rotate, pan, zoom, fit, protein-only and complex views
- Selected pose or all poses
- 4.0 Å strict / 5.0 Å standard / 6.0 Å relaxed nearby-residue cutoff
- Nearby residue table with residue, chain, minimum distance and contact count
- Geometry-based H-bond, hydrophobic, salt bridge, halogen-bond and pi-contact interpretation
- Interaction table with distances in Å
- Pose affinity and RMSD fields from Vina result records
- 2D interaction diagram
- PNG export controls labelled 96/150/300 DPI
- CSV interaction export
- White/dark background
- Binding-site-focused visualization

## Technology
The desktop shell remains C#/.NET 8 WinForms. The molecular rendering/interpretation surface is hosted through Microsoft WebView2 and uses 3Dmol.js. WebView2 is designed for embedding web content in WinForms applications, and 3Dmol.js supports cartoon/stick/surface molecular representations and PNG image export.

## Important build note
This source package was prepared in an environment without the Windows .NET 8 SDK, so a new Windows executable could not be compiled here. Run `BUILD_EXE.bat` on a Windows machine with the .NET 8 SDK. The script produces a **folder-based, non-single-file** self-contained build to reduce the heuristic risk associated with single-file packaging.

The viewer currently loads 3Dmol.js from the official 3Dmol CDN, so the interpretation viewer requires internet access unless `InterpretationViewer/index.html` is changed to reference a locally bundled 3Dmol.js file.

## Antivirus
This package does not disable, bypass, or instruct users to bypass antivirus protection. It uses a normal folder-based deployment rather than `PublishSingleFile=true`. If an antivirus product flags a component, keep the detection information and verify the specific file rather than blindly restoring or whitelisting it.

INTERPRETATION VIEWER UPDATE
- Protein cartoon remains visible when surface is enabled; surface uses a darker semi-transparent blue-gray material.
- Selected ligand is kept prominent and the binding-site region is used for view focusing.
- PNG export now accepts a manually entered DPI value rather than fixed 96/150/300-DPI buttons.
- RMSD is displayed as Vina RMSD lower/upper bounds (LB–UB), relative to the best-ranked mode; large values can be valid when poses are geometrically distinct.
- 2D interaction map shows every residue within the selected 4/5/6 Å cutoff, with classified interaction types and distances plus unclassified contacts.
