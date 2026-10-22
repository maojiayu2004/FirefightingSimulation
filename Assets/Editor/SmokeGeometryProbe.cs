using System.IO;
using System.Text;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class SmokeGeometryProbe
{
    [MenuItem("Tools/Smoke Geometry/Run GPU Contact Probes")]
    public static void Run()
    {
        var field=Object.FindAnyObjectByType<SmokeCollisionField>();
        var shader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/VFX/SmokeGeometryProbe.compute");
        Vector4[] starts={new Vector4(3,1.4f,5,0),new Vector4(3,1.4f,7,0),new Vector4(0,1,7,0),new Vector4(0,2,7,0),new Vector4(2.7f,1.5f,1.16f,0),new Vector4(3,1.4f,7,0)};
        Vector4[] velocities={new Vector4(.65f,0,0,0),new Vector4(.65f,0,0,0),new Vector4(0,-1,0,0),new Vector4(0,1,0,0),new Vector4(0,-1,0,0),new Vector4(.65f,0,.3f,0)};
        string[] labels={"open_door","closed_wall","floor","ceiling","dining_table","wall_tangent"};
        var result=new Vector4[6];
        using(var a=new ComputeBuffer(6,16))using(var b=new ComputeBuffer(6,16))using(var output=new ComputeBuffer(6,16)){
            a.SetData(starts);b.SetData(velocities);shader.SetBuffer(0,"Starts",a);shader.SetBuffer(0,"Velocities",b);shader.SetBuffer(0,"Results",output);
            shader.SetTexture(0,"ProbeField",field.texture);shader.SetVector("FieldMin",field.minimum);shader.SetVector("FieldSize",field.size);shader.SetFloat("Clearance",.08f+field.voxelSize*.9f);
            shader.Dispatch(0,1,1,1);output.GetData(result);
        }
        var text=new StringBuilder("Actual GPU field/contact probe; 40 x 0.05s, four substeps. Not a full smoke dynamics or visual-penetration guarantee.\n");
        var colliders=GameObject.Find("RoomGenerator/House").GetComponentsInChildren<Collider>().Where(c=>c.enabled && !c.isTrigger && c.name!="Hitbox").ToArray();
        Physics.SyncTransforms();
        for(int i=0;i<6;i++){
            Vector3 start=starts[i],velocity=velocities[i];float nearest=velocity.magnitude*2;string obstacle="none";
            foreach(var c in colliders)if(c.Raycast(new Ray(start,velocity.normalized),out var hit,nearest)){nearest=hit.distance;obstacle=ParticleSmokePrototypeTools.Hierarchy(c.transform);}
            text.AppendLine(labels[i]+" start="+start+" GPU_end="+result[i]+" CPU_first_obstacle="+obstacle+" ray_distance="+nearest);
        }
        File.WriteAllText(Path.Combine(SmokeGeometryTools.Results,"gpu-contact-probes.txt"),text.ToString());
    }
}
