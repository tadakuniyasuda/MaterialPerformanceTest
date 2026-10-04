# renderer.material vs MaterialPropertyBlock (URP)

A single-script Unity experiment that colors 10,000 cubes in two different ways and lets you measure the difference in **batching**, **CPU time**, and **material memory**.

- **`renderer.material`**: clones one Material per cube.
- **`MaterialPropertyBlock`**: one shared material, with per-cube color overrides from a single cached block.

It was built to test a common rule of thumb ("MaterialPropertyBlock fixes `renderer.material`") in the Universal Render Pipeline. In URP, the result was the opposite on CPU time. See [Results](#results) and the full write-up: **[blog post link]**.

---

## Requirements

| Item | Version used |
| --- | --- |
| Unity | 6000.0.77f1 |
| Render pipeline | URP (`Universal Render Pipeline/Lit`) |
| Dependencies | None (standard `MonoBehaviour` + `MeshRenderer`, no ECS/DOTS) |

The script also runs in the Built-in Render Pipeline (it falls back to the `_Color` property), but the results below were only measured in URP.

---

## Quick start

1. Create an empty scene.
2. Create an empty GameObject and add the `MaterialVsPropertyBlockDemo` component.
3. Place the camera so the whole grid is visible (for example, position `(0, 90, -70)`, rotation `(50, 0, 0)`).
4. Set **Use Property Block** in the Inspector.
5. Press **Play**.

Set the mode **before** pressing Play. To compare modes, run each in its own Play session.

---

## Inspector fields

| Field | Default | Purpose |
| --- | --- | --- |
| **Use Property Block** | `false` | `false` = color via `renderer.material`. `true` = color via one cached `MaterialPropertyBlock`. |
| **Destroy Cloned Materials On Rebuild** | `false` | Leak test switch. When `true`, cloned materials are destroyed before a rebuild. |
| **Grid Size** | `100` | Cubes per side. `100` = 10,000 cubes. |
| **Spacing** | `1.2` | Distance between cube centers. |
| **Base Material** | *(empty)* | Optional. If empty, the default cube material is used (URP/Lit in URP). |
| **Disable Shadows** | `true` | Turns off shadow casting/receiving so shadow draws don't skew the numbers. |

### Context menu (Play mode only)

Open it from the **⋮** button on the component header, or by right-clicking the component title.

| Command | What it does |
| --- | --- |
| **Recolor** | Recolors the existing cubes using the current mode. |
| **Rebuild (leak test)** | Destroys the whole grid and builds a new one. |

---

## How it works

### 1. Setup: `Start()`

- Creates a temporary primitive cube only to borrow its **mesh** and **default material**, then destroys it.
- Detects the color property: `_BaseColor` (URP/HDRP Lit) if present, otherwise `_Color` (Built-in Standard). The ID is cached with `Shader.PropertyToID` so no string lookups happen in the loops.
- Logs the shader, property, and mode, then builds and colors the grid.

### 2. Building the grid: `BuildGrid()`

- Creates `gridSize × gridSize` GameObjects under a `CubeGrid` parent, centered on the origin.
- Each cube gets only a `MeshFilter` and a `MeshRenderer` with the **shared** base material.
- Cubes are built manually instead of with `GameObject.CreatePrimitive`, which would add 10,000 `BoxCollider`s and pollute the measurements with physics cost.

### 3. Coloring: `ApplyColors()`

**Property block mode**

```csharp
propertyBlock ??= new MaterialPropertyBlock();
propertyBlock.SetColor(colorPropertyId, Random.ColorHSV());
renderers[i].SetPropertyBlock(propertyBlock);
```

- One `MaterialPropertyBlock` is allocated once and reused for every cube.
- This is safe because `SetPropertyBlock` **copies** the values into the renderer.
- The renderer keeps using the shared material, so no new materials are created.

**`renderer.material` mode**

```csharp
Material m = renderers[i].material;
m.color = Random.ColorHSV();
clonedMaterials.Add(m);
```

- The **first** access to `renderer.material` clones the shared material into a new instance owned by that renderer. Later accesses return the same clone.
- `material.color` maps to the shader's `[MainColor]` property (`_BaseColor` in URP Lit).
- Every clone is tracked in a `HashSet<Material>` so the leak test can destroy them, or deliberately not.

### 4. The leak test: `Rebuild()`

- Destroys the `CubeGrid` parent (and all 10,000 cubes).
- If **Destroy Cloned Materials On Rebuild** is on, calls `Destroy()` on every tracked clone.
- Clears the tracking set either way. If the clones were not destroyed, they are now orphaned: nothing references them, but they stay in memory.
- Builds and colors a fresh grid.

Destroying a GameObject does **not** destroy a material created by `renderer.material`. This is the leak the test makes visible.

### 5. Counting materials: `LogMaterialCount()`

- Counts live Material objects with `Resources.FindObjectsOfTypeAll<Material>()`.
- Runs one frame later via a coroutine, because `Destroy()` is deferred to the end of the frame.
- In the Editor the count includes editor-owned materials, so **compare the change between logs, not the absolute number**.

### 6. Cleanup: `OnDestroy()`

Destroys any remaining tracked clones. Whoever calls `renderer.material` owns the clone and is responsible for destroying it.

---

## How to measure

| Tool | Where | What to look at |
| --- | --- | --- |
| **Stats window** | Game view → **Stats** | CPU main / render thread ms, **SetPass calls**, Batches |
| **Frame Debugger** | Window → Analysis → Frame Debugger → **Enable** | Expand `DrawOpaqueObjects`. Select an event and open **Details** to read **Batch cause** and **Draw Calls**. |
| **Console** | Console window | `[Demo]` material counts after each build/rebuild |
| **Profiler** | Window → Analysis → Profiler → **Memory** | Material Count and Total Used Memory |

In URP, look at **SetPass calls** rather than Batches. The SRP Batcher doesn't reduce draw calls; it makes the setup between them cheap.

---

## Results

Measured in the Editor at 1920×1080, Unity 6000.0.77f1, URP, SSAO on, shadows off.
Hardware: Intel Core i7-8700, NVIDIA GeForce RTX 2060 (6 GB), 32 GB RAM, Windows 11 Home 25H2.

| | `renderer.material` | `MaterialPropertyBlock` |
| --- | --- | --- |
| CPU main | 20.8–25.8 ms | 130.8–137.9 ms |
| Render thread | 15.0–17.7 ms | 125.6–132.7 ms |
| SetPass calls | 46 | 20,024 |
| Opaque pass events (Frame Debugger) | 11 SRP Batches | 10,000 individual draws |
| Batch cause | SRP: First call from ScriptableRenderLoopJob | Objects have different MaterialPropertyBlock set. |
| Material objects added | ~10,100 | ~100 |

**Leak test** (`renderer.material`, 3 rebuilds):

| | Cleanup OFF | Cleanup ON |
| --- | --- | --- |
| Material objects | 10,348 → 20,348 → 30,348 → 40,348 | 10,362 → 10,362 → 10,362 → 10,362 |

Batches reads about 20,000 in both modes because SSAO adds a depth-normals prepass, so every cube is drawn twice.

---

## Takeaways

- In URP, the SRP Batcher groups draws by **shader variant**, so 10,000 different materials still batch well.
- Setting a `MaterialPropertyBlock` makes a renderer **incompatible with the SRP Batcher**, which cost about 6× CPU time here.
- `renderer.material` costs memory and **ownership**: clones must be destroyed manually.
- For a few distinct colors, prefer a handful of shared materials. For thousands of unique colors, GPU instancing is the next thing to test.

## Limitations

- Measured in the Editor only, not in a player build.
- Material memory was counted as objects, not measured in bytes.
- Built-in Render Pipeline, GPU instancing, and the GPU Resident Drawer were not tested.
- One machine, three readings per mode.

---

## License

**[choose a license, e.g. MIT]**
