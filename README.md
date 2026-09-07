# ELB-DockTool v1.2.0 — Integrated Independent Docking Interpretation

This package integrates a WebView2-hosted, independently authored docking interpretation interface into the ELB-DockTool WinForms application. The Results page contains **VIEW COMPLEX**, which opens the selected receptor/Vina result in the interpretation viewer.

## Download

Download the latest Windows x64 release from the
[ELB-DockTool Releases](https://github.com/elbinroshak2003-art/ELB-DockTool/releases) page.

Under **Assets**, download the Windows release ZIP:

**ELB-DockTool-v1.2.0-win-x64.zip**

Extract the ZIP and run:

**ELB_DockTool.exe**

### Installation

1. Download the Windows x64 release ZIP from the Releases page.
2. Extract the ZIP to a suitable folder.
3. Run `ELB_DockTool.exe`.
4. Open **Settings** and verify the required external tools.
5. Configure Open Babel if it is not automatically detected.
6. Verify Python/Meeko if receptor preparation is required.
7. Verify or install the PDBFixer/OpenMM environment when receptor repair is required.

No source-code or development files are required for normal use. 

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



