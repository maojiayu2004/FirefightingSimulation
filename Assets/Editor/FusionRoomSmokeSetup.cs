using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ignis;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FusionRoomSmokeSetup
{
    const string ScenePath = "Assets/SCENARIOS/SmokeFusion/SmokeFusion.unity";
    const string FieldPath = "Assets/SCENARIOS/SmokeFusion/SmokeFusionField.asset";
    const string ChairPath = "Assets/Prefabs For Decor/Dining Chair.prefab";

    [MenuItem("Tools/Smoke Fusion/Seed Room Fires And Open Doorways")]
    public static void Seed()
    {
        if (EditorApplication.isPlaying) throw new System.Exception("Exit Play Mode first");
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.isDirty && scene.path != ScenePath) throw new System.Exception("Save the current scene before seeding room fires");
        if (scene.path != ScenePath) EditorSceneManager.OpenScene(ScenePath);

        var house = GameObject.Find("RoomGenerator")?.transform.Find("House");
        if (house == null) throw new System.Exception("RoomGenerator/House was not found");
        var floor = house.Find("Floor_0");
        if (floor == null) throw new System.Exception("Floor_0 was not found");

        Physics.SyncTransforms();
        var report = new StringBuilder();
        var rooms = floor.Cast<Transform>().Where(t => t.name.StartsWith("Room_")).Select(t => new Room(t)).Where(r => r.Bounds.size != Vector3.zero).ToList();
        var doors = floor.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Door(Clone)").ToList();
        foreach (var door in doors)
        {
            foreach (var collider in door.GetComponents<Collider>())
            {
                collider.enabled = false;
                collider.isTrigger = true;
            }
        }
        report.AppendLine("Structural doors found: " + doors.Count + ". Their colliders are disabled, so the door slab is not a smoke barrier.");

        var portals = new List<Bounds>();
        foreach (var door in doors)
        {
            var renderer = door.GetComponent<Renderer>();
            if (renderer == null) continue;
            portals.Add(DoorPortal(door, renderer.bounds));
        }
        for (int i = 0; i < rooms.Count; i++)
        for (int j = i + 1; j < rooms.Count; j++)
        {
            if (!SharedFace(rooms[i].Bounds, rooms[j].Bounds, out var origin, out var tangent, out var normal, out float length)) continue;
            int clear = 0, blocked = 0;
            int samples = Mathf.Max(3, Mathf.RoundToInt(length));
            for (int s = 0; s < samples; s++)
            {
                float along = length * ((s + 0.5f) / samples - 0.5f);
                foreach (float height in new[] { 1.5f, 3.1f })
                {
                    var point = origin + tangent * along + Vector3.up * height;
                    var start = point - normal * 0.45f;
                    if (Physics.Raycast(start, normal, out _, 0.9f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) blocked++;
                    else clear++;
                }
            }
            report.AppendLine(rooms[i].Name + " <-> " + rooms[j].Name + " samplesClear=" + clear + " samplesBlocked=" + blocked);
            bool doorNearby = doors.Any(d => Mathf.Abs(Vector3.Dot(d.position - origin, normal)) < 0.8f && Vector3.ProjectOnPlane(d.position - origin, normal).magnitude < length * 0.5f + 0.5f);
            if (clear == 0 && !doorNearby)
            {
                var portal = new Bounds(origin + Vector3.up * 1.45f, new Vector3(Mathf.Abs(normal.x) * 0.8f + Mathf.Abs(tangent.x) * 1.1f, 2.6f, Mathf.Abs(normal.z) * 0.8f + Mathf.Abs(tangent.z) * 1.1f));
                portals.Add(portal);
                report.AppendLine("CARVED smoke portal at " + portal.center + " because the shared wall has no doorway.");
            }
        }

        var legacy = GameObject.Find("AdditionalKitchenSmoke");
        if (legacy != null) legacy.SetActive(false);

        var field = SmokeCollisionBaker.Bake(house, FieldPath, Path.GetFullPath("../particle-smoke-results/fusion"));
        int opened = OpenPortals(field, portals);
        report.AppendLine("Doorway voxels cleared in the smoke field: " + opened);

        var template = Object.FindObjectsByType<ParticleRoomSmoke>(FindObjectsSortMode.None).FirstOrDefault(s => s.lifecycleFireSource != null)
            ?? Object.FindObjectsByType<ParticleRoomSmoke>(FindObjectsSortMode.None).FirstOrDefault();
        if (template == null) throw new System.Exception("No particle smoke emitter exists to copy");
        var chairPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ChairPath);
        if (chairPrefab == null) throw new System.Exception("Dining chair prefab is missing");

        foreach (var old in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.name.StartsWith("RoomFire_")).ToArray())
            Object.DestroyImmediate(old.gameObject);

        var created = new List<FlammableObject>();
        foreach (var room in rooms)
        {
            bool already = Object.FindObjectsByType<ParticleRoomSmoke>(FindObjectsSortMode.None)
                .Any(s => s.source != null && s.source.transform.IsChildOf(room.Transform));
            if (already)
            {
                report.AppendLine(room.Name + " already has a particle fire.");
                continue;
            }
            var chair = (GameObject)PrefabUtility.InstantiatePrefab(chairPrefab, room.Transform);
            chair.name = "RoomFire_" + room.Name;
            chair.transform.position = new Vector3(room.Bounds.center.x, 0.05f, room.Bounds.center.z);
            var source = chair.GetComponent<FlammableObject>();
            if (source == null) throw new System.Exception("Dining chair has no flammable object");
            source.maxSpread = 0f;
            source.fireCrawlSpeed = 0f;
            source.spreadLayerMask = 0;
            var emitter = Object.Instantiate(template.gameObject, chair.transform, false);
            emitter.name = "RoomFireSmoke";
            emitter.transform.localPosition = Vector3.zero;
            emitter.transform.localRotation = Quaternion.identity;
            emitter.transform.localScale = Vector3.one;
            var smoke = emitter.GetComponent<ParticleRoomSmoke>();
            smoke.source = source;
            smoke.lifecycleFireSource = null;
            smoke.emissionOrigin = null;
            smoke.emissionSurface = null;
            smoke.useMeasuredSurface = false;
            smoke.replaceLegacySmoke = false;
            smoke.legacyRoomSmoke = null;
            smoke.collisionField = field;
            smoke.particlesPerSecond = 15f;
            smoke.lifetime = 180f;
            smoke.startSize = 1f;
            smoke.endSize = 3.5f;
            smoke.opacity = 0.4f;
            smoke.turbulence = 0.1f;
            smoke.ceilingSpeed = 0.7f;
            created.Add(source);
            report.AppendLine("Added fire and smoke emitter in " + room.Name + " at " + chair.transform.position);
        }

        foreach (var smoke in Object.FindObjectsByType<ParticleRoomSmoke>(FindObjectsSortMode.None))
        {
            smoke.collisionField = field;
            var fire = smoke.lifecycleFireSource != null ? smoke.lifecycleFireSource.GetComponent<FlammableObject>() : smoke.source;
            if (fire != null && !created.Contains(fire)) created.Add(fire);
        }

        var igniterObject = GameObject.Find("Fusion Room Fire Igniter") ?? new GameObject("Fusion Room Fire Igniter");
        var igniter = igniterObject.GetComponent<FusionRoomFireIgniter>() ?? igniterObject.AddComponent<FusionRoomFireIgniter>();
        igniter.fires = created.Distinct().ToArray();
        igniter.igniteAtSeconds = 2f;
        report.AppendLine("Play ignites " + igniter.fires.Length + " fires at 2 seconds.");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();
        string resultPath = Path.GetFullPath("../particle-smoke-results/fusion/room-seed.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
        File.WriteAllText(resultPath, report.ToString());
        Debug.Log(report.ToString());
    }

    static Bounds DoorPortal(Transform door, Bounds visual)
    {
        Vector3 thin = door.TransformVector(Vector3.forward);
        thin = new Vector3(Mathf.Abs(thin.x), 0f, Mathf.Abs(thin.z));
        if (thin.sqrMagnitude < 0.0001f) thin = Vector3.forward;
        thin.Normalize();
        visual.Encapsulate(visual.center + thin * 0.4f);
        visual.Encapsulate(visual.center - thin * 0.4f);
        visual.Expand(0.08f);
        return visual;
    }

    static int OpenPortals(SmokeCollisionField field, List<Bounds> portals)
    {
        var texture = field.texture as Texture3D;
        if (texture == null || portals.Count == 0) return 0;
        var pixels = texture.GetPixels();
        int width = texture.width, height = texture.height, opened = 0;
        var free = new Color(0f, 1f, 0f, 0.8f);
        for (int z = 0; z < texture.depth; z++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var point = field.minimum + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * field.voxelSize;
            if (!portals.Any(p => p.Contains(point))) continue;
            pixels[x + width * (y + height * z)] = free;
            opened++;
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        EditorUtility.SetDirty(texture);
        return opened;
    }

    static bool SharedFace(Bounds a, Bounds b, out Vector3 origin, out Vector3 tangent, out Vector3 normal, out float length)
    {
        origin = tangent = normal = Vector3.zero;
        length = 0f;
        float gapX = Mathf.Abs(a.center.x - b.center.x) - a.extents.x - b.extents.x;
        float gapZ = Mathf.Abs(a.center.z - b.center.z) - a.extents.z - b.extents.z;
        if (gapX < 0.4f && gapZ < -1f)
        {
            normal = a.center.x < b.center.x ? Vector3.right : Vector3.left;
            float x = normal.x > 0f ? a.max.x : a.min.x;
            float z0 = Mathf.Max(a.min.z, b.min.z), z1 = Mathf.Min(a.max.z, b.max.z);
            length = z1 - z0;
            origin = new Vector3(x, 0f, (z0 + z1) * 0.5f);
            tangent = Vector3.forward;
            return length > 1f;
        }
        if (gapZ < 0.4f && gapX < -1f)
        {
            normal = a.center.z < b.center.z ? Vector3.forward : Vector3.back;
            float z = normal.z > 0f ? a.max.z : a.min.z;
            float x0 = Mathf.Max(a.min.x, b.min.x), x1 = Mathf.Min(a.max.x, b.max.x);
            length = x1 - x0;
            origin = new Vector3((x0 + x1) * 0.5f, 0f, z);
            tangent = Vector3.right;
            return length > 1f;
        }
        return false;
    }

    sealed class Room
    {
        public readonly string Name;
        public readonly Transform Transform;
        public readonly Bounds Bounds;
        public Room(Transform transform)
        {
            Name = transform.name;
            Transform = transform;
            var walls = transform.GetComponentsInChildren<MeshCollider>(true).FirstOrDefault(c => c.name == "Walls");
            Bounds = walls != null ? walls.bounds : new Bounds();
        }
    }
}
