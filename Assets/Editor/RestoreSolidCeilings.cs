using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class RestoreSolidCeilings
{
    const string ScenePath = "Assets/SCENARIOS/Scenario 1 (controlled; 1 story; daytime) 1.unity";

    [MenuItem("Tools/Kitchen Smoke Tour/Restore Solid Ceilings")]
    public static void Restore()
    {
        if (EditorApplication.isPlaying)
            throw new System.Exception("Exit Play Mode before restoring ceilings.");

        SceneAssetGuard();
        Material transparent = Resources.Load<Material>("Materials/transparent");
        int restored = 0;
        int activated = 0;
        int collidersAdded = 0;
        int flags = 0;

        foreach (RoomGeneration generator in Object.FindObjectsByType<RoomGeneration>(FindObjectsInactive.Include))
        {
            if (!generator.roomRoofsTransparent)
                continue;

            Undo.RecordObject(generator, "Restore solid ceilings");
            generator.roomRoofsTransparent = false;
            EditorUtility.SetDirty(generator);
            flags++;
        }

        foreach (Transform transform in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
        {
            if (transform.name != "Ceilings")
                continue;

            GameObject ceiling = transform.gameObject;
            if (!ceiling.activeSelf)
            {
                Undo.RecordObject(ceiling, "Restore solid ceilings");
                ceiling.SetActive(true);
                activated++;
            }

            MeshRenderer renderer = ceiling.GetComponent<MeshRenderer>();
            RoofData roof = ceiling.GetComponent<RoofData>();
            if (renderer != null && transparent != null && renderer.sharedMaterial == transparent && roof != null && roof.originalMaterial != null)
            {
                Undo.RecordObject(renderer, "Restore solid ceilings");
                renderer.sharedMaterial = roof.originalMaterial;
                EditorUtility.SetDirty(renderer);
                restored++;
            }

            if (HasSolidCollider(ceiling))
                continue;

            MeshFilter filter = ceiling.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                continue;

            MeshCollider collider = Undo.AddComponent<MeshCollider>(ceiling);
            collider.sharedMesh = filter.sharedMesh;
            collider.convex = false;
            collidersAdded++;
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        Debug.Log("CEILINGS_RESTORED materials=" + restored + " activated=" + activated + " colliders=" + collidersAdded + " transparencyOff=" + flags);
    }

    [MenuItem("Tools/Kitchen Smoke Tour/Add Roof Colliders")]
    public static void AddRoofColliders()
    {
        if (EditorApplication.isPlaying)
            throw new System.Exception("Exit Play Mode before adding roof colliders.");

        SceneAssetGuard();
        int added = 0;
        foreach (Transform transform in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
        {
            if (transform.name != "Ceilings")
                continue;

            if (transform.Find("RoofCollider") != null)
                continue;

            MeshFilter filter = transform.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                continue;

            var lid = new GameObject("RoofCollider");
            Undo.RegisterCreatedObjectUndo(lid, "Add roof colliders");
            lid.transform.SetParent(transform, false);
            BoxCollider box = lid.AddComponent<BoxCollider>();
            Bounds bounds = filter.sharedMesh.bounds;
            Vector3 size = bounds.size;
            if (size.y < 0.2f)
                size.y = 0.2f;
            box.center = bounds.center;
            box.size = size;
            box.isTrigger = false;
            added++;
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        Debug.Log("ROOF_COLLIDERS_ADDED count=" + added);
    }

    static void SceneAssetGuard()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path == ScenePath)
            return;

        if (scene.isDirty)
            throw new System.Exception("The open scene has unsaved edits. Open the scenario scene before restoring ceilings.");

        EditorSceneManager.OpenScene(ScenePath);
    }

    static bool HasSolidCollider(GameObject ceiling)
    {
        foreach (Collider collider in ceiling.GetComponents<Collider>())
        {
            if (collider != null && collider.enabled && !collider.isTrigger)
                return true;
        }

        return false;
    }
}
