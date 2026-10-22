using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.VFX;

// Opt-in, offline editor verification. No ignition timer is saved into production scenes.
[InitializeOnLoad]
public static class SmokeGeometryVerification
{
    static ParticleRoomSmoke[] smoke;
    static Camera[] views;
    static Camera[] diagnostics;
    static bool validation;
    static bool live;
    static string folder;
    static double startWall,nextCapture;
    static float startTime,nextLog,lastTime;
    static int phase,index,frames;
    static readonly List<string> names=new List<string>();
    static SmokeGeometryVerification()
    {
        EditorApplication.update+=Update;
        EditorApplication.playModeStateChanged+=Changed;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded+=(scene,mode)=>{
            if(!SessionState.GetBool("GeometryPending",false) || scene.path!=SmokeGeometryTools.ScenePath)return;
            foreach(var realtime in UnityEngine.Object.FindObjectsByType<Normal.Realtime.Realtime>()){
                var serialized=new SerializedObject(realtime);
                var property=serialized.FindProperty("_joinRoomOnStartOptions._enabled")??serialized.FindProperty("_joinRoomOnStart");
                if(property==null)throw new Exception("Cannot suppress offline test auto-connect");
                property.boolValue=false;serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        };
    }
    public static void Run()
    {
        if(EditorApplication.isPlaying)throw new Exception("Stop the previous run first");
        EditorSceneManager.OpenScene(SmokeGeometryTools.ScenePath);
        SessionState.SetBool("GeometryLive",false);SessionState.SetBool("GeometryValidation",false);SessionState.SetBool("GeometryPending",true);EditorApplication.isPlaying=true;
    }
    [MenuItem("Tools/Smoke Geometry/Run Live Preview (No Recording)")]
    public static void RunLive(){Run();SessionState.SetBool("GeometryLive",true);}
    [MenuItem("Tools/Smoke Geometry/Run Geometry Recording")]
    public static void RunAfter()=>Run();
    [MenuItem("Tools/Smoke Geometry/Run Penetration Diagnostics")]
    public static void RunValidation(){Run();SessionState.SetBool("GeometryValidation",true);}
    static uint Alive(ParticleRoomSmoke s){uint total=0;names.Clear();s.effect.GetParticleSystemNames(names);foreach(var n in names)total+=s.effect.GetParticleSystemInfo(n).aliveCount;return total;}
    static void Changed(PlayModeStateChange state)
    {
        if(state==PlayModeStateChange.ExitingPlayMode){smoke=null;return;}
        if(state!=PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool("GeometryPending",false))return;
        SessionState.SetBool("GeometryPending",false);
        validation=SessionState.GetBool("GeometryValidation",false);
        live=SessionState.GetBool("GeometryLive",false);
        folder=Path.Combine(SmokeGeometryTools.Results,validation?"validation":"after");if(!live)Directory.CreateDirectory(folder);
        smoke=UnityEngine.Object.FindObjectsByType<ParticleRoomSmoke>().OrderBy(s=>s.source.name=="stove"?0:1).ToArray();
        for(int i=0;i<smoke.Length;i++){
            smoke[i].gameObject.layer=30+i;
            smoke[i].Configure();smoke[i].effect.Reinit();smoke[i].effect.Play();
        }
        views=new[]{GameObject.Find("Smoke Integration Observation Camera").GetComponent<Camera>(),GameObject.Find("Smoke Door Observation Camera").GetComponent<Camera>(),GameObject.Find("Smoke Hallway Particle Slice").GetComponent<Camera>()};
        // Keep the entire real house; suppress unrelated outdoor drawing in this transient test only.
        var house=GameObject.Find("RoomGenerator/House").transform;
        foreach(var r in UnityEngine.Object.FindObjectsByType<Renderer>())if(!r.transform.IsChildOf(house) && !(r is VFXRenderer))r.enabled=false;
        foreach(var c in UnityEngine.Object.FindObjectsByType<Camera>())c.enabled=false;
        views[0].enabled=true;
        views[0].tag="MainCamera";
        if(live){
            views[0].rect=new Rect(0,0,.5f,1);views[1].rect=new Rect(.5f,0,.5f,1);views[1].enabled=true;
            startTime=Time.time;phase=0;
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            Debug.Log("SMOKE_LIVE_PREVIEW_STARTED: left kitchen, right doorway; no recording; Stop Play to finish.");return;
        }
        diagnostics=new[]{Diagnostic("Above ceiling",new Vector3(5,3.45f,8),Vector3.up,12.5f,15),Diagnostic("Below floor",new Vector3(5,.05f,8),Vector3.down,12.5f,15),Diagnostic("Upper smoke footprint",new Vector3(.5f,3.5f,4.5f),Vector3.down,5.5f,1.9f)};
        var sourceB=new GameObject("Source B diagnostic camera").AddComponent<Camera>();sourceB.enabled=false;sourceB.nearClipPlane=.05f;sourceB.fieldOfView=65;
        sourceB.transform.position=smoke[1].SpawnCenter+new Vector3(1.8f,1.2f,1.8f);sourceB.transform.LookAt(smoke[1].SpawnCenter+Vector3.up*.8f);diagnostics=diagnostics.Concat(new[]{sourceB}).ToArray();
        startTime=Time.time;startWall=EditorApplication.timeSinceStartup;nextCapture=startWall;nextLog=0;phase=index=frames=0;
        File.WriteAllText(Path.Combine(folder,"runtime.csv"),"sim_seconds,wall_seconds,frames,a_rate,b_rate,a_alive,b_alive\n");
        File.WriteAllText(Path.Combine(folder,"frames.csv"),"index,sim_seconds,wall_seconds\n");
        File.WriteAllText(Path.Combine(folder,"status.txt"),"RUNNING\n");
        using(var hash=System.Security.Cryptography.SHA256.Create()){
            string[] paths={"Assets/Scripts/ParticleRoomSmoke.cs","Assets/VFX/ParticleRoomSmokeGeometryInitialize.hlsl","Assets/VFX/ParticleRoomSmokeGeometryUpdate.hlsl",ParticleSmokeGraphBuilder.GeometryGraphPath,SmokeGeometryTools.ScenePath};
            File.WriteAllLines(Path.Combine(folder,"tested-files-sha256.txt"),paths.Select(path=>BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-","")+"  "+path));
        }
    }
    static void Update()
    {
        if(smoke==null || !EditorApplication.isPlaying || EditorApplication.isPaused)return;
        float t=Time.time-startTime;double wall=EditorApplication.timeSinceStartup;
        if(live){
            if(phase==0 && t>=2){smoke[0].source.SetOnFireFromCenterFromNetwork();phase=1;}
            if(phase==1 && t>=45){smoke[1].source.SetOnFireFromCenterFromNetwork();phase=2;}
            return;
        }
        if(Time.time!=lastTime){frames++;lastTime=Time.time;}
        if(phase==0 && t>=2){smoke[0].source.SetOnFireFromCenterFromNetwork();phase=1;}
        if(validation && phase==1 && t>=20){Snapshot("single-020");smoke[1].source.SetOnFireFromCenterFromNetwork();phase=2;}
        if(validation && t>=45){Snapshot("dual-045");File.WriteAllText(Path.Combine(folder,"status.txt"),"VALIDATION COMPLETE\nActual GPU particles captured above ceiling, below floor, upper kitchen and source B. Inspect diagnostic images and pixel measurements.\n");smoke=null;EditorApplication.isPlaying=false;return;}
        if(phase==1 && t>=45){Snapshot("single-045");smoke[1].source.SetOnFireFromCenterFromNetwork();phase=2;}
        if(phase==2 && t>=90){Snapshot("dual-090");smoke[0].source.ApplyNetworkInactiveState(true,false);phase=3;}
        if(phase==3 && t>=94){Snapshot("extinguished-094");phase=4;}
        if(t>=nextLog){nextLog=t+1;File.AppendAllText(Path.Combine(folder,"runtime.csv"),FormattableString.Invariant($"{t:F3},{wall-startWall:F3},{frames},{smoke[0].CurrentRate:F2},{smoke[1].CurrentRate:F2},{Alive(smoke[0])},{Alive(smoke[1])}\n"));frames=0;}
        if(wall>=nextCapture){nextCapture=wall+.5;for(int i=0;i<views.Length;i++)Render(views[i],$"view{i}-{index:D5}.jpg",640,360,false);File.AppendAllText(Path.Combine(folder,"frames.csv"),FormattableString.Invariant($"{index},{t:F3},{wall-startWall:F3}\n"));index++;}
        if(t>=130 && (Alive(smoke[0])==0 || t>=190)){
            Snapshot("final");File.WriteAllText(Path.Combine(folder,"status.txt"),$"COMPLETE\nA emission stopped={smoke[0].CurrentRate<.01f}\nA residual cleared={Alive(smoke[0])==0}\nB independently emitting={smoke[1].CurrentRate>1}\nActual offline Unity camera recording; multiplayer and Vision Pro unverified.\n");smoke=null;EditorApplication.isPlaying=false;
        }
    }
    static Camera Diagnostic(string name,Vector3 position,Vector3 direction,float size,float far)
    {
        var c=new GameObject(name).AddComponent<Camera>();c.enabled=false;c.orthographic=true;c.orthographicSize=size;c.nearClipPlane=.05f;c.farClipPlane=far;c.cullingMask=(1<<30)|(1<<31);c.clearFlags=CameraClearFlags.SolidColor;c.backgroundColor=Color.black;c.transform.position=position;c.transform.rotation=Quaternion.LookRotation(direction,Vector3.forward);return c;
    }
    static void Snapshot(string name){for(int i=0;i<views.Length;i++)Render(views[i],$"view{i}-{name}.png",1280,720,true);if(diagnostics!=null)for(int i=0;i<diagnostics.Length;i++)Render(diagnostics[i],$"diagnostic{i}-{name}.png",960,540,true);}
    static void Render(Camera c,string name,int width,int height,bool png)
    {
        var rt=RenderTexture.GetTemporary(width,height,24);var prior=c.targetTexture;var active=RenderTexture.active;Texture2D texture=null;
        try{c.targetTexture=rt;c.Render();RenderTexture.active=rt;texture=new Texture2D(width,height,TextureFormat.RGB24,false);texture.ReadPixels(new Rect(0,0,width,height),0,0);texture.Apply();File.WriteAllBytes(Path.Combine(folder,name),png?texture.EncodeToPNG():texture.EncodeToJPG(85));}
        finally{c.targetTexture=prior;RenderTexture.active=active;RenderTexture.ReleaseTemporary(rt);if(texture!=null)UnityEngine.Object.DestroyImmediate(texture);}
    }
}

