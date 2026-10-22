using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.VFX;
using Ignis;

// Opt-in comparison; production and prototype scenes are never saved by this tool.
[InitializeOnLoad]
public static class SmokeFusionExperiment
{
    public const string ScenePath = "Assets/SCENARIOS/SmokeFusion/SmokeFusion.unity";
    const string BasePath = "Assets/SCENARIOS/Smoke-VFX-Test/Scenario1-ImprovedSmoke_Test0.unity";
    static string Results => Path.GetFullPath("../particle-smoke-results/fusion");
    static ParticleRoomSmoke[] particles;
    static Camera camera;
    static float start;
    static int stage;
    static double deadline;
    static double startWall, nextFrame;
    static int frame;
    static bool preview;
    static SmokeFusionExperiment()
    {
        EditorApplication.playModeStateChanged += Changed;
        EditorApplication.update += Tick;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => {
            if (!SessionState.GetBool("FusionPending", false) || scene.path != ScenePath) return;
            foreach (var realtime in UnityEngine.Object.FindObjectsByType<Normal.Realtime.Realtime>()) {
                var so = new SerializedObject(realtime);
                var p = so.FindProperty("_joinRoomOnStartOptions._enabled") ?? so.FindProperty("_joinRoomOnStart");
                if (p == null) throw new Exception("Offline auto-connect property missing");
                p.boolValue = false; so.ApplyModifiedPropertiesWithoutUndo();
            }
        };
    }
    [MenuItem("Tools/Smoke Fusion/Build Latest Repository Scene With Particle Smoke")]
    public static void Build()
    {
        if (EditorApplication.isPlaying || EditorSceneManager.GetActiveScene().isDirty) throw new Exception("Preserve scene edits and exit Play Mode first");
        Directory.CreateDirectory("Assets/SCENARIOS/SmokeFusion");
        Directory.CreateDirectory(Results);
        AssetDatabase.Refresh();
        var target = EditorSceneManager.OpenScene(BasePath);
        EditorSceneManager.SaveScene(target, ScenePath);
        var room = target.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<RoomSmokeController>(true)).Single();
        var map = target.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
            .GroupBy(ParticleSmokePrototypeTools.Hierarchy).ToDictionary(g => g.Key, g => g.First());
        var prototype = EditorSceneManager.OpenScene(SmokeGeometryTools.ScenePath, OpenSceneMode.Additive);
        var original = prototype.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<ParticleRoomSmoke>(true)).ToArray();
        foreach (var old in original) {
            var source = map[ParticleSmokePrototypeTools.Hierarchy(old.source.transform)].GetComponent<FlammableObject>();
            var go = UnityEngine.Object.Instantiate(old.gameObject);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, target);
            go.transform.SetParent(source.transform, true);
            var smoke = go.GetComponent<ParticleRoomSmoke>();
            smoke.source = source;
            smoke.lifecycleFireSource = source.name == "stove" ? room.fireSource : null;
            smoke.emissionSurface = old.emissionSurface == null ? null : map[ParticleSmokePrototypeTools.Hierarchy(old.emissionSurface.transform)].GetComponent<BoxCollider>();
            smoke.emissionOrigin = null;
            smoke.replaceLegacySmoke = false;
            smoke.legacyRoomSmoke = null;
            smoke.collisionField = null;
        }
        foreach (var c in prototype.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Camera>(true)).Where(c => c.name == "Smoke Integration Observation Camera" || c.name == "Smoke Door Observation Camera")) {
            var copy = UnityEngine.Object.Instantiate(c.gameObject);
            copy.name = c.name;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(copy, target);
        }
        EditorSceneManager.CloseScene(prototype, true);
        UnityEngine.SceneManagement.SceneManager.SetActiveScene(target);
        var house = GameObject.Find("RoomGenerator/House").transform;
        foreach (var t in house.GetComponentsInChildren<Transform>(true)) if (t.name == "Ceilings") t.gameObject.SetActive(true);
        foreach (var r in house.GetComponentsInChildren<MeshRenderer>()) {
            var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) continue;
            bool solid = false;
            for (var t = r.transform; t != null && t != house.parent; t = t.parent)
                if (t.GetComponents<Collider>().Any(c => c.enabled && !c.isTrigger && c.name != "Hitbox")) { solid = true; break; }
            if (!solid) r.gameObject.AddComponent<MeshCollider>().sharedMesh = mesh;
        }
        var field = SmokeCollisionBaker.Bake(house, "Assets/SCENARIOS/SmokeFusion/SmokeFusionField.asset", Results);
        foreach (var smoke in UnityEngine.Object.FindObjectsByType<ParticleRoomSmoke>(FindObjectsSortMode.None)) {
            smoke.collisionField = field; smoke.Configure(); smoke.effect.SetFloat("EmissionRate", 0);
            if (smoke.replaceLegacySmoke || smoke.source == null) throw new Exception("Fusion setup invalid");
        }
        EditorSceneManager.SaveScene(target);
        File.WriteAllText(Path.Combine(Results, "build.txt"), "Base: " + BasePath + "\nParticle emitters: " + original.Length + "\nLegacy smoke preserved at full authored strength. Geometry rebaked for copied latest scene.\n");
    }
    [MenuItem("Tools/Smoke Fusion/Open Fusion Scene")]
    public static void Open() => OpenSaved(ScenePath);
    [MenuItem("Tools/Smoke Fusion/Open Repository Smoke Scene")]
    public static void OpenRepository() => OpenSaved(BasePath);
    [MenuItem("Tools/Smoke Fusion/Open Particle Prototype Scene")]
    public static void OpenPrototype() => OpenSaved(SmokeGeometryTools.ScenePath);
    [MenuItem("Tools/Smoke Fusion/Stop Preview Or Comparison")]
    public static void Stop() => EditorApplication.isPlaying = false;
    static void OpenSaved(string path)
    {
        if(EditorApplication.isPlaying || EditorSceneManager.GetActiveScene().isDirty) throw new Exception("Preserve scene edits and exit Play Mode first");
        EditorSceneManager.OpenScene(path);
    }
    [MenuItem("Tools/Smoke Fusion/Preview Fusion (No Recording)")]
    public static void Preview() { SessionState.SetBool("FusionPreview", true); BeginRun(); }
    [MenuItem("Tools/Smoke Fusion/Run Offline Comparison")]
    public static void Run()
    {
        SessionState.SetBool("FusionPreview", false); BeginRun();
    }
    static void BeginRun()
    {
        if(Application.isBatchMode) throw new Exception("Use the normal graphical editor for smoke preview and recording; batch mode is not a visual verification path.");
        if (!File.Exists(ScenePath)) Build();
        else {
            if (EditorApplication.isPlaying || EditorSceneManager.GetActiveScene().isDirty) throw new Exception("Preserve scene edits and exit Play Mode first");
            EditorSceneManager.OpenScene(ScenePath);
        }
        SessionState.SetBool("FusionPending", true);
        EditorApplication.isPlaying = true;
    }
    static void Changed(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode) {
            if(particles != null && !preview && File.Exists(Path.Combine(Results, "status.txt")) && File.ReadAllText(Path.Combine(Results, "status.txt")).Trim() == "RUNNING")
                File.WriteAllText(Path.Combine(Results, "status.txt"), "INTERRUPTED: comparison stopped before completion.\n");
            particles = null;
        }
        if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool("FusionPending", false)) return;
        SessionState.SetBool("FusionPending", false);
        particles = UnityEngine.Object.FindObjectsByType<ParticleRoomSmoke>().OrderBy(s => s.lifecycleFireSource != null ? 0 : 1).ToArray();
        if(particles.Length != 2 || particles[0].lifecycleFireSource == null) throw new Exception("Fusion scene needs the shared stove lifecycle. Rebuild using Tools/Smoke Fusion.");
        preview = SessionState.GetBool("FusionPreview", false);
        camera = GameObject.Find("Smoke Integration Observation Camera").GetComponent<Camera>();
        foreach (var c in UnityEngine.Object.FindObjectsByType<Camera>()) c.enabled = false;
        camera.enabled = true; camera.tag = "MainCamera";
        EditorApplication.ExecuteMenuItem("Window/General/Game");
        camera.rect = new Rect(0, 0, 1, 1);
        start = Time.time; stage = 0; deadline = EditorApplication.timeSinceStartup + 600;
        startWall = nextFrame = EditorApplication.timeSinceStartup; frame = 0;
        if(preview) return;
        Directory.CreateDirectory(Results);
        File.WriteAllText(Path.Combine(Results, "frames.csv"), "index,sim_seconds,wall_seconds\n");
        File.WriteAllText(Path.Combine(Results, "samples.txt"), "");
        File.WriteAllText(Path.Combine(Results, "status.txt"), "RUNNING\n");
    }
    static void Tick()
    {
        if (particles == null || !EditorApplication.isPlaying) return;
        try {
            var t = Time.time - start;
            if(preview) {
                if(stage==0 && t>=2) { Fire(particles[0]).SetOnFireFromCenterFromNetwork(); stage++; }
                if(stage==1 && t>=45) { Fire(particles[1]).SetOnFireFromCenterFromNetwork(); stage++; }
                return;
            }
            if (EditorApplication.timeSinceStartup >= nextFrame) {
                nextFrame = EditorApplication.timeSinceStartup + 1;
                Render(camera, $"overlay-{frame:D5}.png");
                File.AppendAllText(Path.Combine(Results, "frames.csv"), FormattableString.Invariant($"{frame},{t:F3},{EditorApplication.timeSinceStartup-startWall:F3}\n")); frame++;
            }
            if (EditorApplication.timeSinceStartup > deadline) throw new Exception("Fusion run timed out");
            if (stage == 0 && t >= 2) { Fire(particles[0]).SetOnFireFromCenterFromNetwork(); stage++; }
            if (stage == 1 && t >= 20) { Capture("single-020"); stage++; }
            if (stage == 2 && t >= 45) { Capture("single-045"); Fire(particles[1]).SetOnFireFromCenterFromNetwork(); stage++; }
            if (stage == 3 && t >= 75) { Capture("dual-075"); Fire(particles[0]).ApplyNetworkInactiveState(true, false); stage++; }
            if (stage == 4 && t >= 110) { Capture("residual-110"); stage++; }
            if (stage == 5 && t >= 155) {
                Capture("final-155");
                File.WriteAllText(Path.Combine(Results, "status.txt"), "COMPLETE\nActual Unity camera comparisons: legacy only, particles only, full overlay; same simulation instant per triplet.\n");
                EditorApplication.isPlaying = false;
            }
        } catch (Exception e) {
            File.WriteAllText(Path.Combine(Results, "status.txt"), e.ToString());
            Debug.LogException(e); particles = null;
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }
    static FlammableObject Fire(ParticleRoomSmoke smoke) => smoke.lifecycleFireSource != null ? smoke.lifecycleFireSource.GetComponent<FlammableObject>() : smoke.source;
    static void Capture(string label)
    {
        // Hide contributions without restarting either simulation; restore in finally.
        var emitters = UnityEngine.Object.FindObjectsByType<FlammableObject>();
        var alphas = emitters.Select(e => e.smokeAlpha).ToArray();
        var renderers = particles.Select(p => p.effect.GetComponent<Renderer>()).ToArray();
        var enabled = renderers.Select(r => r.enabled).ToArray();
        var views = new[] { camera, GameObject.Find("Smoke Door Observation Camera").GetComponent<Camera>() };
        try {
            for (int mode = 0; mode < 3; mode++) {
                for (int i = 0; i < emitters.Length; i++) { emitters[i].smokeAlpha = mode == 1 ? 0 : alphas[i]; emitters[i].RefreshSmokeAppearance(); }
                for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = mode != 0 && enabled[i];
                for (int v = 0; v < views.Length; v++) Render(views[v], $"{label}-view{v}-{new[] { "legacy", "particles", "overlay" }[mode]}.png");
            }
            File.AppendAllText(Path.Combine(Results, "samples.txt"), label + " time=" + (Time.time-start) + " rates=" + string.Join(",", particles.Select(p => p.CurrentRate.ToString("F1"))) + " alive=" + string.Join(",", particles.Select(p => p.effect.aliveParticleCount)) + "\n");
            foreach (var room in UnityEngine.Object.FindObjectsByType<RoomSmokeController>()) {
                File.AppendAllText(Path.Combine(Results, "samples.txt"), "ROOM enabled="+room.enabled+" source="+ParticleSmokePrototypeTools.Hierarchy(room.fireSource.transform)+" sameStove="+(room.fireSource.gameObject==Fire(particles[0]).gameObject)+"\n");
                foreach (var zone in room.smokeZones) {
                    var z = zone.smokeSource;
                    var effects = (System.Collections.Generic.List<VisualEffect>)typeof(FlammableObject).GetField("fires", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(z);
                    File.AppendAllText(Path.Combine(Results, "samples.txt"), "ZONE "+z.name+" burning="+z.onFire+" alpha="+z.smokeAlpha+" multiplier="+z.smokeVFXMultiplier+" effects="+effects.Count+" alive="+string.Join(",",effects.Where(f=>f!=null).Select(f=>f.aliveParticleCount))+"\n");
                }
            }
        } finally {
            for (int i = 0; i < emitters.Length; i++) { emitters[i].smokeAlpha = alphas[i]; emitters[i].RefreshSmokeAppearance(); }
            for (int i = 0; i < renderers.Length; i++) renderers[i].enabled = enabled[i];
        }
    }
    static void Render(Camera c, string name)
    {
        var rt = RenderTexture.GetTemporary(960, 540, 24); var prior = c.targetTexture; var active = RenderTexture.active;
        Texture2D texture = null;
        try { c.targetTexture = rt; c.Render(); RenderTexture.active = rt; texture = new Texture2D(960,540,TextureFormat.RGB24,false); texture.ReadPixels(new Rect(0,0,960,540),0,0); texture.Apply(); File.WriteAllBytes(Path.Combine(Results,name),texture.EncodeToPNG()); }
        finally { c.targetTexture = prior; RenderTexture.active = active; RenderTexture.ReleaseTemporary(rt); if(texture!=null) UnityEngine.Object.DestroyImmediate(texture); }
    }
}
