# ELB-DockTool independent docking interpretation viewer

The Results page opens a native WebView2-hosted HTML/WebGL viewer implemented specifically for ELB-DockTool.

Features:
- clean protein cartoon + ligand sticks/spheres
- pose selection and best-pose workflow
- 4.0, 5.0, and 6.0 Å nearby-residue cutoffs
- nearby-residue table with minimum distance and contact count
- geometry-based interaction table
- 2D interaction diagram
- dark/white background
- optional molecular surface
- 96/150/300 DPI PNG export
- CSV interaction export
- all poses / selected pose display

## Improved viewer revision
- Protein cartoon uses a spectrum colour scheme in protein/complex mode.
- Surface mode uses a distinct blue-grey VDW surface and keeps the ligand visible.
- Ligand rendering is based on a generated PDB representation from the parsed docking atoms.
- The 2D interaction-diagram panel was removed in favour of a clearer 3D interaction view and complete nearby-residue/interactions tables.
- PNG export accepts a manual DPI value from 72 to 2400.
- RMSD values are shown as AutoDock Vina's reported lower/upper bounds.
