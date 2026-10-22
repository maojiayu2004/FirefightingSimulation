using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.VFX;

public static class SmokeGeometryTools
{
    public const string ScenePath="Assets/SCENARIOS/ParticleSmokePrototype/ParticleSmokeGeometry.unity";
    public static string Results=>Path.GetFullPath("../particle-smoke-results/geometry");
    public static void Command(string command)
    {
        if(command=="geometry-live")SmokeGeometryVerification.RunLive();
        if(command=="geometry-audit")Audit();
        if(command=="geometry-build")Build();
        if(command=="geometry-rebake")Rebake();
        if(command=="geometry-after")SmokeGeometryVerification.Run();
        if(command=="geometry-probes")ProbePortals(GameObject.Find("RoomGenerator/House").transform,UnityEngine.Object.FindAnyObjectByType<SmokeCollisionField>());
        if(command=="geometry-gpu-probes")SmokeGeometryProbe.Run();
        if(command=="geometry-validation")SmokeGeometryVerification.RunValidation();
        if(command=="geometry-graph")UpdateGraph();
    }
    [MenuItem("Tools/Smoke Geometry/Create Geometry Test Scene")]
    public static void Build()
    {
        if(EditorApplication.isPlaying)throw new Exception("Exit Play Mode first");
        if(EditorSceneManager.GetActiveScene().isDirty){if(EditorSceneManager.GetActiveScene().path==ScenePath)EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());else throw new Exception("Preserve unsaved edits first");}
        Directory.CreateDirectory(Results);ParticleSmokeGraphBuilder.BuildGeometryAsset();
        if(!File.Exists(ScenePath))throw new Exception("Latest geometry scene is missing");
        EditorSceneManager.OpenScene(ScenePath);
        var house=GameObject.Find("RoomGenerator/House");var repairs=new StringBuilder();
        foreach(Transform transform in house.GetComponentsInChildren<Transform>(true))
            if(transform.name=="Ceilings" && !transform.gameObject.activeSelf){transform.gameObject.SetActive(true);repairs.AppendLine("Enabled existing ceiling: "+ParticleSmokePrototypeTools.Hierarchy(transform));}
        foreach(var renderer in house.GetComponentsInChildren<MeshRenderer>())
        {
            if(renderer.GetComponent<MeshFilter>()?.sharedMesh==null)continue;
            bool solid=false;
            for(Transform parent=renderer.transform;parent!=null && parent!=house.transform.parent;parent=parent.parent)
                if(parent.GetComponents<Collider>().Any(c=>c.enabled && !c.isTrigger && c.name!="Hitbox")){solid=true;break;}
            if(solid)continue;
            var collider=renderer.gameObject.AddComponent<MeshCollider>();collider.sharedMesh=renderer.GetComponent<MeshFilter>().sharedMesh;
            repairs.AppendLine("Added matching non-convex mesh collider: "+ParticleSmokePrototypeTools.Hierarchy(renderer.transform));
        }
        var field=SmokeCollisionBaker.Bake(house.transform);
        foreach(var smoke in UnityEngine.Object.FindObjectsByType<ParticleRoomSmoke>(FindObjectsSortMode.None))
        {
            smoke.collisionField=field;smoke.effect.visualEffectAsset=AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(ParticleSmokeGraphBuilder.GeometryGraphPath);
            smoke.particlesPerSecond=192;smoke.maxParticles=8192;smoke.Configure();smoke.effect.SetFloat("EmissionRate",0);
        }
        File.WriteAllText(Path.Combine(Results,"repairs-test-copy-only.txt"),repairs.ToString());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());ProbePortals(house.transform,field);
        Debug.Log("GEOMETRY_SCENE_BUILT");
    }
    [MenuItem("Tools/Smoke Geometry/Rebake Static Scene Collisions")]
    static void Rebake()
    {
        if(EditorApplication.isPlaying)throw new Exception("Exit Play Mode first");
        ParticleSmokeGraphBuilder.BuildGeometryAsset();EditorSceneManager.OpenScene(ScenePath);
        var field=SmokeCollisionBaker.Bake(GameObject.Find("RoomGenerator/House").transform);
        foreach(var smoke in UnityEngine.Object.FindObjectsByType<ParticleRoomSmoke>(FindObjectsSortMode.None)){smoke.collisionField=field;smoke.Configure();}
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());ProbePortals(GameObject.Find("RoomGenerator/House").transform,field);
    }
    [MenuItem("Tools/Smoke Geometry/Update Geometry Graph Only")]
    public static void UpdateGraph()
    {
        if(EditorApplication.isPlaying || EditorSceneManager.GetActiveScene().isDirty)throw new Exception("Exit Play Mode and preserve scene edits first");
        ParticleSmokeGraphBuilder.BuildGeometryAsset();EditorSceneManager.OpenScene(ScenePath);
    }
    static void ProbePortals(Transform house,SmokeCollisionField field)
    {
        var walls=house.GetComponentsInChildren<MeshCollider>().Where(c=>c.name=="Walls").ToArray();
        var report=new StringBuilder("z,y,wall_hit,field_distance_at_partition\n");
        float longest=0,run=0,start=0,best=7;
        for(float z=3.8f;z<=9.2f;z+=0.1f)
        {
            bool blocked=walls.Any(w=>w.Raycast(new Ray(new Vector3(3.0f,1.4f,z),Vector3.right),out var hit,0.9f));
            Vector3 uv=(new Vector3(3.45f,1.4f,z)-field.minimum);uv=new Vector3(uv.x/field.size.x,uv.y/field.size.y,uv.z/field.size.z);
            var value=field.texture.GetPixelBilinear(uv.x,uv.y,uv.z);
            report.AppendLine(z.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)+",1.4,"+blocked+","+value.a.ToString("F3",System.Globalization.CultureInfo.InvariantCulture));
            if(!blocked){if(run==0)start=z;run+=0.1f;if(run>longest){longest=run;best=start+run*.5f;}}else run=0;
        }
        File.WriteAllText(Path.Combine(Results,"door-probes.csv"),report.ToString());
        File.WriteAllText(Path.Combine(Results,"portal.txt"),"Partition x=3.45; longest open width="+longest+"; center z="+best+"\n");
        var observer=GameObject.Find("Smoke Integration Observation Camera");
        var door=GameObject.Find("Smoke Door Observation Camera")??new GameObject("Smoke Door Observation Camera");
        var camera=door.GetComponent<Camera>();if(camera==null)camera=door.AddComponent<Camera>();camera.enabled=false;camera.fieldOfView=65;camera.nearClipPlane=.05f;camera.farClipPlane=35;
        door.transform.position=new Vector3(4.8f,1.5f,best);door.transform.LookAt(new Vector3(2.4f,2.0f,best));
        var slice=GameObject.Find("Smoke Hallway Particle Slice")??new GameObject("Smoke Hallway Particle Slice");
        var sliceCamera=slice.GetComponent<Camera>();if(sliceCamera==null)sliceCamera=slice.AddComponent<Camera>();sliceCamera.enabled=false;sliceCamera.orthographic=true;sliceCamera.orthographicSize=1.7f;sliceCamera.nearClipPlane=.05f;sliceCamera.farClipPlane=1.7f;
        sliceCamera.cullingMask=1<<30;sliceCamera.clearFlags=CameraClearFlags.SolidColor;sliceCamera.backgroundColor=Color.black;
        slice.transform.position=new Vector3(3.65f,1.75f,best);slice.transform.rotation=Quaternion.LookRotation(Vector3.right,Vector3.up);
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
    }
    public static void Audit()
    {
        if(EditorApplication.isPlaying)throw new Exception("Exit Play Mode before geometry audit");
        if(EditorSceneManager.GetActiveScene().isDirty)throw new Exception("Preserve current unsaved scene edits first");
        if(EditorSceneManager.GetActiveScene().path!=ScenePath)EditorSceneManager.OpenScene(ScenePath);
        Directory.CreateDirectory(Results);var text=new StringBuilder();
        var house=GameObject.Find("RoomGenerator/House");
        foreach(var collider in house.GetComponentsInChildren<Collider>(true))
        {
            text.AppendLine(ParticleSmokePrototypeTools.Hierarchy(collider.transform)+" | "+collider.GetType().Name+" active="+collider.gameObject.activeInHierarchy+" enabled="+collider.enabled+" trigger="+collider.isTrigger+" bounds="+collider.bounds);
            if(collider is MeshCollider mesh)text.AppendLine(" mesh="+mesh.sharedMesh+" convex="+mesh.convex+" triangles="+(mesh.sharedMesh!=null?mesh.sharedMesh.triangles.Length/3:0));
        }
        foreach(var renderer in house.GetComponentsInChildren<MeshRenderer>(true).Where(r=>r.GetComponent<Collider>()==null))
            text.AppendLine("NO_COLLIDER "+ParticleSmokePrototypeTools.Hierarchy(renderer.transform)+" bounds="+renderer.bounds);
        File.WriteAllText(Path.Combine(Results,"collider-audit.txt"),text.ToString());Debug.Log("GEOMETRY_AUDIT_COMPLETE");
    }
}
