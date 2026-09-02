# ELB-DockTool

**ELB-DockTool** is a GUI-based molecular docking application designed to simplify and organize common **AutoDock Vina** workflows.

It provides an integrated workflow for receptor preparation, ligand preparation, docking-grid setup, Vina execution, result inspection, and structure conversion.

## Version

**ELB-DockTool v1.2.0**

## Main Features

### Molecular Docking
- Receptor preparation from PDB/mmCIF structures
- Receptor structure inspection
- Residue, HETATM, water, and chain inspection
- Selective deletion of unwanted receptor components
- Optional polar-hydrogen addition using Open Babel
- Meeko-based receptor preparation and PDBQT generation
- Optional Meeko `--delete_bad_res` handling
- Receptor PDBQT selection and management

### Ligand Preparation
- Add and manage ligand structures
- Direct ligand conversion to PDBQT
- Energy minimization
- Optional hydrogen addition
- Optional 3D structure generation
- Optional Gasteiger charge assignment during preparation
- Meeko-based ligand parameterization
- Save and reload ligand selections

### Docking
- Docking grid setup
- 3D grid visualization
- AutoDock Vina search parameter configuration
- Exhaustiveness, modes, energy range, and CPU settings
- Dock multiple receptor–ligand combinations
- Live Vina execution monitoring
- Detailed program output and error monitoring

### Results
- Docking result review
- Docking score inspection
- Output file management
- Auto Split
- PDB conversion
- MOL2 conversion

### Phytochemical Library
- IMPPAT 2.0 plant search
- Compound browsing and filtering
- Physicochemical properties
- Drug-likeness filters
- ADMET information
- 2D structure preview
- Download SDF
- Download PDBQT
- Send compounds directly to the Ligands workflow
- Batch export of filtered compounds

### Project Management
- Save and reload ligand panels
- Receptor and ligand management
- Project data clearing
- Integrated logs and workspace management

## Workflow

A typical docking workflow is:

1. **Start ELB-DockTool** and complete password verification.
2. Go to **Molecular Docking → Receptor Preparation**.
3. Load a **PDB or mmCIF receptor** and inspect its structure.
4. Review residues, HETATM groups, water molecules, and chains.
5. Make any required structural selections or deletions.
6. Prepare the receptor using **Meeko** and generate the receptor PDBQT.
7. Go to **Receptors** and select the prepared receptor.
8. Configure the **Docking Grid** and verify the box using the 3D visualization.
9. Go to **Ligands** and add or load ligand structures.
10. Use **DIRECT CONVERT** or **ENERGY** when ligand preparation or conversion is required.
11. Configure the **Vina Search** parameters.
12. Start docking from **Run**.
13. Monitor execution in **Vina Execution Monitor**.
14. Review docking results in **Results**.
15. Use **Auto Split**, **Convert → PDB**, or **Convert → MOL2** when required.

## Requirements

ELB-DockTool uses external scientific software and libraries for specific workflow operations, including:

- **AutoDock Vina**
- **vina_split**
- **Open Babel**
- **Meeko**
- **RDKit**
- **Python**

Additional third-party components and their respective licenses are documented in the distributed package.

## Receptor Preparation

ELB-DockTool provides an integrated receptor-preparation workflow.

For PDB receptors, the application can optionally use Open Babel to add polar hydrogens before passing the temporary prepared structure to Meeko.

The workflow is:

```text
PDB/mmCIF
    ↓
Receptor Structure Inspection
    ↓
Optional polar-hydrogen preparation
    ↓
Meeko receptor preparation
    ↓
Atom typing / partial-charge assignment
    ↓
PDBQT
Prepare receptors and ligands correctly before docking. Verify the docking grid around the biologically relevant binding site.

Docking scores are computational predictions and should not be considered experimental binding affinities.


