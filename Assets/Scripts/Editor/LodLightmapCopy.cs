using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Makes LOD1+ renderers use LOD0's baked lightmap, so lighting doesn't change when LODs swap.
///
/// Unity static-batches a scene before IProcessSceneWithReport callbacks run, and a static batch
/// has each renderer's lightmap scale/offset burnt into its UVs, so copying the values onto an
/// already-batched renderer has no effect. To keep batching, LOD1+ renderers are therefore kept
/// out of Unity's automatic static batching (see the Tools menu item below), and this processor
/// copies LOD0's lightmap values onto them and then static-batches them itself.
/// A LOD1+ renderer is batched here when its LOD0 counterpart is marked Batching Static.
///
/// IProcessSceneWithReport is obsolete in Unity 7 in favour of AssetPostprocessor.OnProcessScene, but
/// as of 7000.0.0a20 that replacement is not called when entering Play mode, so this keeps the old hook.
/// </summary>
#pragma warning disable CS0618
[InitializeOnLoad]
class LodLightmapCopy : IProcessSceneWithReport
{
    public int callbackOrder => -1;

    const string FixMenuPath = "Tools/Lighting/Exclude LOD1+ From Automatic Static Batching";

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        var lodGroups = GetLodGroups(scene);
        CopyLightmapValues(lodGroups, out var alreadyBatched);
        if (alreadyBatched > 0)
            Debug.LogWarning($"[LodLightmapCopy] {alreadyBatched} LOD1+ renderers in '{scene.path}' were already static-batched by Unity, so they can't use LOD0's lightmap and will light differently. Run '{FixMenuPath}'.");

        if (StaticBatchingEnabled(report))
            CombineLods(lodGroups, scene);
    }

    static LodLightmapCopy()
    {
        Lightmapping.bakeCompleted += CopyInLoadedScenes;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredEditMode) CopyInLoadedScenes();
        };
    }

    static void CopyInLoadedScenes()
    {
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded) CopyLightmapValues(GetLodGroups(scene), out _);
        }
    }

    static List<LODGroup> GetLodGroups(Scene scene)
    {
        var result = new List<LODGroup>();
        foreach (var root in scene.GetRootGameObjects())
            result.AddRange(root.GetComponentsInChildren<LODGroup>(true));
        return result;
    }

    // Calls action(lod0Renderer, lodNRenderer) for every matching pair of renderers in a LOD group.
    static void ForEachLodPair(LODGroup lodGroup, System.Action<Renderer, Renderer> action)
    {
        var lods = lodGroup.GetLODs();
        if (lods.Length < 2) return;
        var lod0 = lods[0].renderers;
        for (var i = 1; i < lods.Length; i++)
        {
            var lodN = lods[i].renderers;
            var count = Mathf.Min(lod0.Length, lodN.Length);
            for (var j = 0; j < count; j++)
            {
                if (lod0[j] != null && lodN[j] != null)
                    action(lod0[j], lodN[j]);
            }
        }
    }

    static void CopyLightmapValues(List<LODGroup> lodGroups, out int alreadyBatched)
    {
        var batched = 0;
        foreach (var lodGroup in lodGroups)
        {
            ForEachLodPair(lodGroup, (lod0, lodN) =>
            {
                // Values set on a batched renderer are ignored and log a warning per renderer.
                if (lodN.isPartOfStaticBatch)
                {
                    batched++;
                    return;
                }
                lodN.lightmapIndex = lod0.lightmapIndex;
                lodN.lightmapScaleOffset = lod0.lightmapScaleOffset;
            });
        }
        alreadyBatched = batched;
    }

    static void CombineLods(List<LODGroup> lodGroups, Scene scene)
    {
        var toCombine = new List<GameObject>();
        foreach (var lodGroup in lodGroups)
        {
            ForEachLodPair(lodGroup, (lod0, lodN) =>
            {
                if (lodN.isPartOfStaticBatch || !lodN.gameObject.activeInHierarchy) return;
                if (!GameObjectUtility.AnyStaticEditorFlagsSet(lod0.gameObject, StaticEditorFlags.BatchingStatic)) return;
                if (!toCombine.Contains(lodN.gameObject)) toCombine.Add(lodN.gameObject);
            });
        }

        if (toCombine.Count > 0)
            StaticBatchingUtility.Combine(toCombine.ToArray(), null);
    }

    static bool StaticBatchingEnabled(BuildReport report)
    {
        var target = report != null ? report.summary.platform : EditorUserBuildSettings.activeBuildTarget;
        return PlayerSettings.GetStaticBatchingForPlatform(target);
    }

    /// <summary>
    /// Clears Batching Static on LOD1+ renderers in every prefab and scene, so Unity leaves them for
    /// OnProcessScene to batch after the lightmap copy. LOD0 keeps its flag. Saves the changed assets.
    /// </summary>
    [MenuItem(FixMenuPath)]
    static void ExcludeLodsFromAutomaticBatching()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var setup = EditorSceneManager.GetSceneManagerSetup();
        var changed = new List<string>();

        foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null || asset.GetComponentInChildren<LODGroup>(true) == null) continue;

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (ClearLodBatchingFlags(root.GetComponentsInChildren<LODGroup>(true)) > 0)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    changed.Add(path);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // Scenes can override the flag on prefab instances.
        foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            if (ClearLodBatchingFlags(GetLodGroups(scene)) > 0)
            {
                EditorSceneManager.SaveScene(scene);
                changed.Add(path);
            }
        }

        if (setup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(setup);
        Debug.Log($"[LodLightmapCopy] Cleared Batching Static on LOD1+ renderers in {changed.Count} assets:\n" + string.Join("\n", changed));
    }

    static int ClearLodBatchingFlags(IEnumerable<LODGroup> lodGroups)
    {
        var cleared = 0;
        foreach (var lodGroup in lodGroups)
        {
            ForEachLodPair(lodGroup, (lod0, lodN) =>
            {
                var go = lodN.gameObject;
                var flags = GameObjectUtility.GetStaticEditorFlags(go);
                if ((flags & StaticEditorFlags.BatchingStatic) == 0) return;
                GameObjectUtility.SetStaticEditorFlags(go, flags & ~StaticEditorFlags.BatchingStatic);
                cleared++;
            });
        }
        return cleared;
    }
}
#pragma warning restore CS0618
