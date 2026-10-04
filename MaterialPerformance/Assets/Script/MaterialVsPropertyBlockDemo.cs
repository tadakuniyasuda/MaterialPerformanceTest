using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Spawns a gridSize x gridSize grid of cubes and colors each one randomly, either via
/// renderer.material (clones one Material per cube) or via a single cached MaterialPropertyBlock.
///
/// Usage:
///   1. Set UsePropertyBlock in the Inspector BEFORE pressing Play.
///   2. Press Play, then measure (Stats / Frame Debugger / Profiler > Memory).
///   3. During Play, use the component's ⋮ menu:
///        "Recolor"              -> recolors the existing cubes with the current mode
///        "Rebuild (leak test)"  -> destroys the grid and builds a fresh one
///   Watch the console: material count is logged after every build.
/// </summary>
public class MaterialVsPropertyBlockDemo : MonoBehaviour
{
    [Header("Experiment")]
    [Tooltip("OFF: renderer.material (clones a Material per cube)\nON: one cached MaterialPropertyBlock")]
    public bool UsePropertyBlock = false;

    [Tooltip("Leak test: when rebuilding, Destroy() the cloned materials we created.\nLeave OFF to watch them pile up.")]
    public bool DestroyClonedMaterialsOnRebuild = false;

    [Header("Grid")]
    [SerializeField] int gridSize = 100;
    [SerializeField] float spacing = 1.2f;

    [Tooltip("Optional. If empty, the default cube material is used (URP/Lit in URP, Standard in Built-in).")]
    [SerializeField] Material baseMaterial;

    [Tooltip("Turn shadows off so shadow-caster draws don't pollute the numbers.")]
    [SerializeField] bool disableShadows = true;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"); // URP / HDRP Lit
    static readonly int ColorId = Shader.PropertyToID("_Color");     // Built-in Standard

    Mesh cubeMesh;
    int colorPropertyId;
    Transform gridRoot;
    MeshRenderer[] renderers;

    // Cached and reused for all 10,000 cubes.
    MaterialPropertyBlock propertyBlock;

    // Only filled in renderer.material mode, so we can prove (and fix) the leak.
    readonly HashSet<Material> clonedMaterials = new HashSet<Material>();

    void Start()
    {
        // Borrow the built-in cube mesh + default material, then throw the temp object away.
        // (Building cubes manually avoids 10,000 BoxColliders from CreatePrimitive.)
        var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cubeMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        if (baseMaterial == null) baseMaterial = temp.GetComponent<MeshRenderer>().sharedMaterial;
        Destroy(temp);

        colorPropertyId = baseMaterial.HasProperty(BaseColorId) ? BaseColorId : ColorId;
        Debug.Log($"[Demo] Shader: {baseMaterial.shader.name}, color property: " +
                  (colorPropertyId == BaseColorId ? "_BaseColor" : "_Color") +
                  $", mode: {(UsePropertyBlock ? "MaterialPropertyBlock" : "renderer.material")}");

        LogMaterialCount("Before build");
        BuildGrid();
        ApplyColors();
        StartCoroutine(LogMaterialCountNextFrame("After first build"));
    }

    void BuildGrid()
    {
        gridRoot = new GameObject("CubeGrid").transform;
        renderers = new MeshRenderer[gridSize * gridSize];
        float offset = (gridSize - 1) * spacing * 0.5f; // center the grid on the origin

        for (int z = 0; z < gridSize; z++)
        {
            for (int x = 0; x < gridSize; x++)
            {
                var go = new GameObject($"Cube_{x}_{z}");
                go.transform.SetParent(gridRoot, false);
                go.transform.localPosition = new Vector3(x * spacing - offset, 0f, z * spacing - offset);

                go.AddComponent<MeshFilter>().sharedMesh = cubeMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = baseMaterial;

                if (disableShadows)
                {
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }

                renderers[z * gridSize + x] = mr;
            }
        }
    }

    void ApplyColors()
    {
        if (UsePropertyBlock)
        {
            propertyBlock ??= new MaterialPropertyBlock();

            for (int i = 0; i < renderers.Length; i++)
            {
                propertyBlock.SetColor(colorPropertyId, Random.ColorHSV());
                // SetPropertyBlock COPIES the values into the renderer,
                // so reusing one block for every cube is safe (and allocation-free).
                renderers[i].SetPropertyBlock(propertyBlock);
            }
        }
        else
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                // First access to .material clones sharedMaterial into a brand-new Material
                // that is NOT destroyed when the GameObject is destroyed.
                // Later accesses return the same clone (no new allocation).
                Material m = renderers[i].material;
                m.color = Random.ColorHSV(); // .color maps to the [MainColor] property (_BaseColor in URP Lit)
                clonedMaterials.Add(m);
            }
        }
    }

    [ContextMenu("Recolor")]
    void Recolor()
    {
        if (!Application.isPlaying || renderers == null) return;
        ApplyColors();
        StartCoroutine(LogMaterialCountNextFrame("After recolor"));
    }

    [ContextMenu("Rebuild (leak test)")]
    void Rebuild()
    {
        if (!Application.isPlaying || gridRoot == null) return;

        // Destroying the GameObjects does NOT destroy materials created by renderer.material.
        Destroy(gridRoot.gameObject);

        if (DestroyClonedMaterialsOnRebuild)
        {
            foreach (var m in clonedMaterials) Destroy(m);
        }
        clonedMaterials.Clear(); // forget them either way -> if not destroyed, they are now orphaned (leaked)

        BuildGrid();
        ApplyColors();
        StartCoroutine(LogMaterialCountNextFrame("After rebuild"));
    }

    // Destroy() is deferred to end of frame, so count on the next frame.
    IEnumerator LogMaterialCountNextFrame(string label)
    {
        yield return null;
        LogMaterialCount(label);
    }

    static void LogMaterialCount(string label)
    {
        // In the Editor this includes editor-owned materials too: compare the DELTA, not the absolute number.
        int count = Resources.FindObjectsOfTypeAll<Material>().Length;
        Debug.Log($"[Demo] {label}: {count} Material objects in memory");
    }

    void OnDestroy()
    {
        // Good hygiene: whoever calls renderer.material owns the clone and must destroy it.
        foreach (var m in clonedMaterials)
        {
            if (m != null) Destroy(m);
        }
        clonedMaterials.Clear();
    }
}
