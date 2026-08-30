# ELB DockTool

**ELB DockTool** is a Windows GUI-based molecular docking workflow tool designed to simplify and organize common **AutoDock Vina** workflows.

It provides a single interface for:

- Receptor preparation and management
- Ligand preparation and conversion
- Energy minimization
- Docking-grid definition
- AutoDock Vina docking
- Live execution monitoring
- Docking-result review
- PDBQT/PDB/MOL2 result conversion

---

## Features

### 1. Dashboard

The dashboard provides the main entry point to ELB DockTool.

It includes:

- Daily CADD/pharmaceutical-science insight
- Docking workflow overview
- Tool availability indicators
- Molecular Docking launcher
- Molecule Converter launcher
- Data clearing controls

The dashboard is designed to give a quick overview before starting a docking workflow.

---

# 2. Molecular Docking

Click:

**START MOLECULAR DOCKING**

The docking workspace is divided into several sections.

---

## 2.1 Receptor Panel

The receptor panel is used to prepare and manage receptor structures.

### Select Receptor(s)

Use:

**SELECT RECEPTOR(S)**

to select one or more prepared receptor files.

The docking workflow uses **PDBQT receptor files**.

### Save Receptor

Use:

**SAVE RECEPTOR**

to save selected receptors into the application's receptor workspace.

This is useful when the same receptor will be reused in future docking runs.

### Load Saved

Use:

**LOAD SAVED**

to load previously saved receptors.

Saved receptor structures can therefore be reused without selecting the original files again.

### Clear

Use:

**CLEAR**

to remove the current receptor selection.

---

# 3. Grid Settings

Each receptor can have its own docking-grid settings.

Select:

**GRID SETTINGS**

to define the docking search space.

The grid parameters include the position and size of the docking box.

The grid should be positioned around the biologically relevant binding site.

### Grid visualization

The receptor grid interface provides a visualization option for individual receptors.

Use the **eye/view control** associated with a receptor to inspect:

- The receptor structure
- The docking-grid position
- The relationship between the protein and search box

This is useful for checking whether the selected docking region is actually located over the intended binding site.

> Important: A docking box should not be placed arbitrarily. Whenever possible, use a known active site, co-crystallized ligand position, experimentally known binding site, or another scientifically justified site.

---

# 4. Ligand Panel

The ligand panel is used to select and prepare compounds for docking.

### Select Ligand(s)

Use:

**SELECT LIGAND(S)**

to select ligand files.

The workflow can work with common ligand formats supported by the installed conversion tools, including formats such as:

- PDBQT
- SDF
- MOL2
- PDB

The exact conversion capability depends on Open Babel availability.

### Load Saved

Use:

**LOAD SAVED**

to load previously converted/saved ligand PDBQT files.

Saved ligand loading uses the application's ligand conversion workspace rather than the minimization-output directory.

### Clear

Use:

**CLEAR**

to remove the current ligand selection.

---

# 5. Ligand Conversion

The docking workflow can convert ligand structures into PDBQT format.

For example:

```text
SDF / MOL2 / PDB
        ↓
   Open Babel
        ↓
      PDBQT
