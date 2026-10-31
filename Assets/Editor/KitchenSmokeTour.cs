using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ignis;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Records the Play camera: kitchen, then the adjoining corridor, then outside the exterior door.
[InitializeOnLoad]
public static class KitchenSmokeTour
{
    const string ScenePath = "Assets/SCENARIOS/Scenario 1 (controlled; 1 story; daytime) 1.unity";
    const int Width = 960;
    const int Height = 540;
    const float HoldSeconds = 10f;
    const float MoveSeconds = 2f;
    const float FramesPerSecond = 8f;

    static string Results => Path.GetFullPath("../particle-smoke-results/kitchen-tour");
    static Camera camera;
    static FlammableObject[] stoves;
    static Pose[] poses;
    static RenderTexture target;
    static Texture2D texture;
    static float startTime;
    static int frameIndex;
    static int lastCapturedFrame = -1;
    static bool recording;
    static double deadline;

    struct Pose
    {
        public Vector3 position;
        public Vector3 focus;
        public string label;
    }

    static KitchenSmokeTour()
    {
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.update += Tick;
    }

    [MenuItem("Tools/Kitchen Smoke Tour/Record Play View")]
    public static void Launch()
    {
        if (Application.isBatchMode)
            throw new Exception("Open the graphical editor. Batch mode does not render the smoke.");
        if (EditorApplication.isPlaying)
            throw new Exception("Exit Play Mode before starting the kitchen tour.");

        Directory.CreateDirectory(Results);
        File.WriteAllText(Path.Combine(Results, "status.txt"), "OPENING\n");
        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath)
        {
            if (scene.isDirty)
                EditorSceneManager.SaveOpenScenes();
            EditorSceneManager.OpenScene(ScenePath);
        }

        foreach (var realtime in UnityEngine.Object.FindObjectsByType<Normal.Realtime.Realtime>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var serialized = new SerializedObject(realtime);
            var join = serialized.FindProperty("_joinRoomOnStartOptions._enabled") ?? serialized.FindProperty("_joinRoomOnStart");
            if (join == null)
                continue;
            join.boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        SessionState.SetBool("KitchenTourPending", true);
        SessionState.SetBool("KitchenTourReload", true);
        File.WriteAllText(Path.Combine(Results, "status.txt"), "PLAYING\n");
        EditorApplication.isPlaying = true;
    }

    static void OnPlayMode(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool("KitchenTourReload", false))
        {
            SessionState.SetBool("KitchenTourReload", false);
            if (!EditorApplication.isPlaying && File.Exists(ScenePath))
                EditorSceneManager.OpenScene(ScenePath);
            return;
        }

        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            recording = false;
            Time.captureFramerate = 0;
            if (target != null)
                target.Release();
            target = null;
            return;
        }

        if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool("KitchenTourPending", false))
            return;

        SessionState.SetBool("KitchenTourPending", false);
        try
        {
            BeginRecording();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(Results, "status.txt"), "FAILED\n" + error);
            Debug.LogException(error);
            EditorApplication.isPlaying = false;
        }
    }

    static void BeginRecording()
    {
        Directory.CreateDirectory(Results);
        foreach (var old in Directory.GetFiles(Results, "frame-*.jpg"))
            File.Delete(old);

        Transform kitchen = ChooseKitchen(out Transform house);
        Transform hallway = ChooseHallway(house, kitchen);
        Transform door = ChooseExteriorDoor(house, hallway != null ? hallway : kitchen);
        stoves = kitchen.GetComponentsInChildren<Transform>(true)
            .Where(t => t.name.StartsWith("Stove"))
            .Select(t => t.GetComponent<FlammableObject>() ?? t.GetComponentInChildren<FlammableObject>(true))
            .Where(f => f != null)
            .Distinct()
            .ToArray();
        if (stoves.Length == 0)
            throw new Exception("The chosen kitchen has no stove fire.");

        Bounds stoveBounds = VisibleBounds(stoves[0].transform);
        poses = new[]
        {
            KitchenPose(kitchen, stoveBounds),
            HallwayPose(hallway, kitchen, house, stoveBounds.center),
            OutsidePose(door, house, hallway != null ? hallway : kitchen)
        };
        poses[0].label = "kitchen";
        poses[1].label = "corridor";
        poses[2].label = "outside";

        foreach (var existing in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            existing.enabled = false;
        foreach (var listener in UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            listener.enabled = false;

        var cameraObject = new GameObject("Kitchen Smoke Tour Camera");
        camera = cameraObject.AddComponent<Camera>();
        cameraObject.AddComponent<AudioListener>();
        camera.tag = "MainCamera";
        camera.fieldOfView = 68f;
        camera.nearClipPlane = 0.08f;
        camera.farClipPlane = 80f;
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.enabled = true;
        ApplyPose(poses[0], 0f);

        foreach (var stove in stoves)
            stove.SetOnFireFromCenterFromNetwork();

        Time.captureFramerate = Mathf.RoundToInt(FramesPerSecond);
        startTime = Time.time;
        frameIndex = 0;
        lastCapturedFrame = -1;
        recording = true;
        deadline = EditorApplication.timeSinceStartup + 900;
        File.WriteAllText(Path.Combine(Results, "shots.txt"),
            "kitchen=" + kitchen.name + "\nhallway=" + (hallway != null ? hallway.name : "fallback") +
            "\ndoor=" + (door != null ? door.name : "fallback") + "\nstoves=" + stoves.Length + "\n" +
            string.Join("\n", poses.Select(p => p.label + " " + p.position + " look " + p.focus)));
        File.WriteAllText(Path.Combine(Results, "status.txt"), "RECORDING\n");
        Debug.Log("Kitchen smoke tour recording " + kitchen.name);
    }

    static void Tick()
    {
        if (!recording || !EditorApplication.isPlaying || camera == null)
            return;

        try
        {
            if (EditorApplication.timeSinceStartup > deadline)
                throw new Exception("Recording exceeded 15 minutes.");

            float elapsed = Time.time - startTime;
            float sweep = Sweep(elapsed);
            Pose pose = PoseAt(elapsed);
            ApplyPose(pose, sweep);

            if (elapsed < 1.5f)
            {
                foreach (var stove in stoves)
                {
                    if (stove != null && !stove.onFire)
                        stove.SetOnFireFromCenterFromNetwork();
                }
            }

            if (Time.frameCount == lastCapturedFrame)
                return;
            lastCapturedFrame = Time.frameCount;
            Capture();
            if (elapsed >= HoldSeconds * 3f + MoveSeconds * 2f)
                Finish();
        }
        catch (Exception error)
        {
            recording = false;
            File.WriteAllText(Path.Combine(Results, "status.txt"), "FAILED\n" + error);
            Debug.LogException(error);
            EditorApplication.isPlaying = false;
        }
    }

    static float Sweep(float elapsed)
    {
        float local = elapsed;
        if (elapsed > HoldSeconds)
            local = elapsed - HoldSeconds - MoveSeconds;
        if (elapsed > HoldSeconds * 2f + MoveSeconds)
            local = elapsed - (HoldSeconds * 2f + MoveSeconds * 2f);
        local = Mathf.Clamp(local, 0f, HoldSeconds);
        return Mathf.Lerp(-42f, 42f, local / HoldSeconds);
    }

    static Pose PoseAt(float elapsed)
    {
        float kitchenEnd = HoldSeconds;
        float hallStart = kitchenEnd + MoveSeconds;
        float hallEnd = hallStart + HoldSeconds;
        float outsideStart = hallEnd + MoveSeconds;
        if (elapsed < kitchenEnd)
            return poses[0];
        if (elapsed < hallStart)
            return Lerp(poses[0], poses[1], (elapsed - kitchenEnd) / MoveSeconds);
        if (elapsed < hallEnd)
            return poses[1];
        if (elapsed < outsideStart)
            return Lerp(poses[1], poses[2], (elapsed - hallEnd) / MoveSeconds);
        return poses[2];
    }

    static Pose Lerp(Pose from, Pose to, float t)
    {
        t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
        return new Pose
        {
            position = Vector3.Lerp(from.position, to.position, t),
            focus = Vector3.Lerp(from.focus, to.focus, t),
            label = to.label
        };
    }

    static void ApplyPose(Pose pose, float sweepDegrees)
    {
        Vector3 look = pose.focus - pose.position;
        look.y = 0f;
        if (look.sqrMagnitude < 0.01f)
            look = Vector3.forward;
        Quaternion yaw = Quaternion.AngleAxis(sweepDegrees, Vector3.up);
        Vector3 direction = yaw * look.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, direction);
        Vector3 position = pose.position + right * Mathf.Sin(sweepDegrees * Mathf.Deg2Rad) * 0.55f;
        camera.transform.position = position;
        Vector3 focus = pose.focus + Vector3.up * 0.45f;
        camera.transform.rotation = Quaternion.LookRotation(focus - position, Vector3.up);
    }

    static void Capture()
    {
        if (target == null)
            target = new RenderTexture(Width, Height, 24);
        camera.targetTexture = target;
        camera.Render();
        RenderTexture.active = target;
        if (texture == null)
            texture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
        texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
        texture.Apply();
        File.WriteAllBytes(Path.Combine(Results, "frame-" + frameIndex.ToString("D4") + ".jpg"), texture.EncodeToJPG(82));
        camera.targetTexture = null;
        RenderTexture.active = null;
        frameIndex++;
    }

    static void Finish()
    {
        recording = false;
        Time.captureFramerate = 0;
        File.WriteAllText(Path.Combine(Results, "status.txt"), "DONE\nframes=" + frameIndex + "\n");
        Debug.Log("Kitchen smoke tour wrote " + frameIndex + " frames.");
        EditorApplication.isPlaying = false;
    }

    static Transform ChooseKitchen(out Transform house)
    {
        Transform[] rooms = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .Where(t => t.name.Contains("[Kitchen]"))
            .ToArray();
        Transform best = null;
        float bestArea = -1f;
        house = null;
        foreach (Transform room in rooms)
        {
            bool hasStove = room.GetComponentsInChildren<Transform>(true).Any(t => t.name.StartsWith("Stove"));
            if (!hasStove)
                continue;
            Bounds bounds = BoundsOf(room);
            float area = bounds.size.x * bounds.size.z;
            if (area <= bestArea)
                continue;
            bestArea = area;
            best = room;
            house = HouseOf(room);
        }

        if (best == null)
            throw new Exception("No kitchen with a stove was found.");
        return best;
    }

    static Transform ChooseHallway(Transform house, Transform kitchen)
    {
        Bounds kitchenBounds = BoundsOf(kitchen);
        Transform best = null;
        float bestDistance = float.MaxValue;
        foreach (Transform room in RoomsNamed(house, "[Hallway]"))
        {
            Bounds bounds = BoundsOf(room);
            float distance = Vector3.Distance(kitchenBounds.ClosestPoint(bounds.center), bounds.ClosestPoint(kitchenBounds.center));
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = room;
        }

        return best;
    }

    static Transform ChooseExteriorDoor(Transform house, Transform near)
    {
        Transform best = null;
        float bestDistance = float.MaxValue;
        Vector3 nearPoint = near != null ? near.position : Vector3.zero;
        foreach (Transform door in house.GetComponentsInChildren<Transform>(true))
        {
            if (!door.name.StartsWith("Door"))
                continue;
            if (!TryOutdoorDirection(door, out _))
                continue;
            float distance = Vector3.Distance(door.position, nearPoint);
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = door;
        }

        return best;
    }

    static Pose KitchenPose(Transform kitchen, Bounds stoveBounds)
    {
        Collider floor = FloorCollider(kitchen);
        Vector3 eye = ClearEye(stoveBounds, floor, 5.2f, 3.2f);
        Vector3 focus = stoveBounds.center;
        focus.y = Mathf.Min(focus.y + 0.35f, eye.y + 0.2f);
        return new Pose { position = eye, focus = focus, label = "kitchen" };
    }

    static Pose HallwayPose(Transform hallway, Transform kitchen, Transform house, Vector3 stovePosition)
    {
        Transform link = LinkDoor(house, kitchen, hallway);
        Vector3 focus = link != null ? link.position : stovePosition;
        if (hallway == null)
        {
            Vector3 eye = ClearEye(new Bounds(focus, Vector3.one), FloorCollider(kitchen), 3.5f, 2.2f);
            focus.y = eye.y;
            return new Pose { position = eye, focus = focus, label = "corridor" };
        }

        Vector3 standing = ClearEye(new Bounds(focus, Vector3.one * 0.4f), FloorCollider(hallway), 3.2f, 1.8f);
        focus.y = standing.y - 0.15f;
        return new Pose { position = standing, focus = focus, label = "corridor" };
    }

    static Pose OutsidePose(Transform door, Transform house, Transform approach)
    {
        float eyeHeight = GroundY(approach.position) + 1.6f;
        if (door != null && TryOutdoorDirection(door, out Vector3 outside))
        {
            Vector3 eye = door.position + outside * 3.6f;
            for (float extra = 0f; extra <= 4f; extra += 0.8f)
            {
                Vector3 candidate = door.position + outside * (3.6f + extra);
                candidate.y = eyeHeight;
                if (!IsOpenAir(candidate))
                    continue;
                eye = candidate;
                break;
            }
            eye.y = eyeHeight;
            Vector3 focus = door.position;
            focus.y = eyeHeight - 0.15f;
            return new Pose { position = eye, focus = focus, label = "outside" };
        }

        Bounds houseBounds = BoundsOf(house);
        Vector3 away = houseBounds.center - approach.position;
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f)
            away = Vector3.back;
        Vector3 fallback = houseBounds.center - away.normalized * (Mathf.Max(houseBounds.extents.x, houseBounds.extents.z) + 3.2f);
        fallback.y = eyeHeight;
        Vector3 look = approach.position;
        look.y = eyeHeight;
        return new Pose { position = fallback, focus = look, label = "outside" };
    }

    static Vector3 ClearEye(Bounds target, Collider floor, float preferredDistance, float minimumDistance)
    {
        Vector3 aim = target.center;
        Vector3 best = aim + Vector3.back * preferredDistance;
        best.y = (floor != null ? GroundY(best) : aim.y) + 1.6f;
        float bestScore = float.NegativeInfinity;
        float[] minimumClearance = { 0.95f, 0.4f };
        for (int pass = 0; pass < minimumClearance.Length && bestScore < 0f; pass++)
        {
        for (int ring = 0; ring < 2; ring++)
        {
            float radius = ring == 0 ? preferredDistance : minimumDistance;
            for (int step = 0; step < 16; step++)
            {
                float angle = step * Mathf.PI * 2f / 16f;
                Vector3 flat = aim + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                if (floor != null && !AboveFloor(flat, floor))
                    continue;
                float ground = GroundY(flat);
                Vector3 eye = new Vector3(flat.x, ground + 1.6f, flat.z);
                if (!IsOpenAir(eye) || !SeesTarget(eye, target))
                    continue;
                float clearance = Clearance(eye);
                if (clearance < minimumClearance[pass])
                    continue;
                float score = clearance * 3f - Mathf.Abs(radius - preferredDistance);
                if (score <= bestScore)
                    continue;
                bestScore = score;
                best = eye;
            }
        }
        }

        return best;
    }

    static bool IsOpenAir(Vector3 eye)
    {
        Collider[] overlaps = Physics.OverlapSphere(eye, 0.28f, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider collider = overlaps[i];
            if (collider == null || collider.isTrigger || collider.name == "Hitbox")
                continue;
            string name = collider.name;
            if (name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (name.IndexOf("ceiling", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            return false;
        }

        return true;
    }

    static float Clearance(Vector3 eye)
    {
        float nearest = 4f;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * Mathf.PI * 0.25f;
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            if (!Physics.Raycast(eye, direction, out RaycastHit hit, 4f, ~0, QueryTriggerInteraction.Ignore))
                continue;
            if (hit.collider == null)
                continue;
            string name = hit.collider.name;
            if (name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("ceiling", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (hit.distance < nearest)
                nearest = hit.distance;
        }

        return nearest;
    }

    static bool SeesTarget(Vector3 eye, Bounds target)
    {
        Vector3 aim = target.center;
        aim.y = Mathf.Clamp(eye.y * 0.45f + target.center.y * 0.55f, target.min.y + 0.05f, target.max.y);
        Vector3 direction = aim - eye;
        float distance = direction.magnitude;
        if (distance < 0.4f)
            return false;
        if (!Physics.Raycast(eye, direction.normalized, out RaycastHit hit, distance - 0.15f, ~0, QueryTriggerInteraction.Ignore))
            return true;
        return hit.collider != null && (hit.collider.bounds.Intersects(target) || hit.collider.name.StartsWith("Stove") || hit.collider.name.StartsWith("Door"));
    }

    static Bounds VisibleBounds(Transform source)
    {
        Renderer[] renderers = source.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return new Bounds(source.position + Vector3.up * 0.5f, new Vector3(0.8f, 1f, 0.8f));
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }

    static Transform LinkDoor(Transform house, Transform kitchen, Transform hallway)
    {
        if (hallway == null)
            return null;
        Bounds kitchenBounds = BoundsOf(kitchen);
        Bounds hallBounds = BoundsOf(hallway);
        Transform best = null;
        float bestDistance = float.MaxValue;
        foreach (Transform door in house.GetComponentsInChildren<Transform>(true))
        {
            if (!door.name.StartsWith("Door"))
                continue;
            float distance = Vector3.Distance(door.position, kitchenBounds.ClosestPoint(door.position))
                + Vector3.Distance(door.position, hallBounds.ClosestPoint(door.position));
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = door;
        }

        return best;
    }

    static bool TryOutdoorDirection(Transform door, out Vector3 outside)
    {
        Vector3[] directions = { door.forward, -door.forward, door.right, -door.right };
        foreach (Vector3 raw in directions)
        {
            Vector3 direction = raw;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f)
                continue;
            direction.Normalize();
            bool aheadHasFloor = HasFloor(door.position + direction * 1.35f);
            bool behindHasFloor = HasFloor(door.position - direction * 1.35f);
            if (aheadHasFloor == behindHasFloor)
                continue;
            outside = aheadHasFloor ? -direction : direction;
            return true;
        }

        outside = Vector3.zero;
        return false;
    }

    static Vector3 Standing(Vector3 approximate, Transform room)
    {
        Collider floor = FloorCollider(room);
        Vector3 candidate = new Vector3(approximate.x, 0f, approximate.z);
        if (floor != null && !AboveFloor(candidate, floor))
            candidate = new Vector3(BoundsOf(room).center.x, 0f, BoundsOf(room).center.z);
        candidate.y = GroundY(candidate) + 1.6f;
        return candidate;
    }

    static bool AboveFloor(Vector3 xz, Collider floor)
    {
        foreach (RaycastHit hit in CastDown(xz))
        {
            if (hit.collider == floor)
                return true;
        }

        return false;
    }

    static bool HasFloor(Vector3 xz)
    {
        foreach (RaycastHit hit in CastDown(xz))
        {
            if (hit.collider != null && hit.collider.name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    static float GroundY(Vector3 xz)
    {
        float floorY = float.NegativeInfinity;
        float lowest = float.PositiveInfinity;
        bool any = false;
        foreach (RaycastHit hit in CastDown(xz))
        {
            any = true;
            if (hit.point.y < lowest)
                lowest = hit.point.y;
            if (hit.collider.name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0 && hit.point.y > floorY)
                floorY = hit.point.y;
        }

        if (!float.IsNegativeInfinity(floorY))
            return floorY;
        return any ? lowest : xz.y;
    }

    static IEnumerable<RaycastHit> CastDown(Vector3 xz)
    {
        return Physics.RaycastAll(new Vector3(xz.x, 80f, xz.z), Vector3.down, 160f, ~0, QueryTriggerInteraction.Ignore)
            .Where(hit => hit.collider != null && !hit.collider.isTrigger && hit.collider.name != "Hitbox");
    }

    static Collider FloorCollider(Transform room)
    {
        Transform floors = room.Find("Floors");
        return floors != null ? floors.GetComponent<Collider>() : null;
    }

    static Bounds BoundsOf(Transform room)
    {
        Transform floors = room.Find("Floors");
        if (floors != null)
        {
            Collider collider = floors.GetComponent<Collider>();
            if (collider != null)
                return collider.bounds;
            Renderer renderer = floors.GetComponent<Renderer>();
            if (renderer != null)
                return renderer.bounds;
        }

        Renderer[] renderers = room.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Bounds(room.position, Vector3.one * 4f);
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }

    static Transform HouseOf(Transform room)
    {
        for (Transform current = room; current != null; current = current.parent)
        {
            if (current.name == "House" || current.name.StartsWith("House"))
                return current;
        }

        return room.root;
    }

    static IEnumerable<Transform> RoomsNamed(Transform house, string token)
    {
        return house.GetComponentsInChildren<Transform>(true).Where(t => t.name.Contains(token));
    }
}
