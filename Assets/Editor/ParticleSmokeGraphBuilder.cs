using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.VFX;
using Ignis;

// VFX Graph's authoring model is internal. Reflection is isolated to this editor-only builder.
public static class ParticleSmokeGraphBuilder
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    public const string GeometryGraphPath="Assets/VFX/ParticleRoomSmokeGeometry.vfx";
    public static Type TypeNamed(string name)=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(name)).FirstOrDefault(t=>t!=null);
    static PropertyInfo Property(Type t,string name)=>t==null?null:t.GetProperty(name,Flags|BindingFlags.DeclaredOnly)??Property(t.BaseType,name);
    static object Get(object o,string name)=>Property(o.GetType(),name)?.GetValue(o) ?? Field(o.GetType(),name)?.GetValue(o);
    static FieldInfo Field(Type t,string name)=>t==null?null:t.GetField(name,Flags|BindingFlags.DeclaredOnly)??Field(t.BaseType,name);
    static void Set(object o,string name,object value)
    {
        var p=Property(o.GetType(),name);
        if(p!=null && p.CanWrite) p.SetValue(o,value); else {var f=Field(o.GetType(),name); if(f==null)throw new Exception("No field "+name+" on "+o.GetType()); f.SetValue(o,value);}
    }
    static object Call(object o,string name,params object[] args)
    {
        var methods=o.GetType().GetMethods(Flags).Where(m=>m.Name==name && m.GetParameters().Length>=args.Length).OrderBy(m=>m.GetParameters().Length);
        foreach(var m in methods)
        {
            var ps=m.GetParameters();
            if(!args.Select((a,i)=>a==null||ps[i].ParameterType.IsInstanceOfType(a)).All(v=>v)) continue;
            if(ps.Skip(args.Length).Any(p=>!p.IsOptional))continue;
            return m.Invoke(o,args.Concat(ps.Skip(args.Length).Select(p=>p.DefaultValue)).ToArray());
        }
        throw new MissingMethodException(o.GetType().Name+"."+name);
    }
    static List<object> List(object o,string name)=>((IEnumerable)Get(o,name)).Cast<object>().ToList();
    static void Setting(object o,string name,object value)=>Call(o,"SetSettingValue",name,value);
    static ScriptableObject Create(string name)=>ScriptableObject.CreateInstance(TypeNamed(name)??throw new Exception(name));
    static long Id(object o) {AssetDatabase.TryGetGUIDAndLocalFileIdentifier((UnityEngine.Object)o,out string guid,out long id);return id;}
    static bool IsContext(object o) {for(Type t=o.GetType();t!=null;t=t.BaseType)if(t.Name=="VFXContext")return true;return false;}
    static void Clear(object model) {foreach(var child in List(model,"children"))Call(model,"RemoveChild",child);}
    public static void BuildGeometryAsset()
    {
        string graphPath=GeometryGraphPath;
        if(EditorApplication.isPlaying)throw new Exception("Exit play mode first");
        // Preserve this asset's GUID when rebuilding so existing emitters retain their reference.
        if(File.Exists(graphPath)) File.Copy("Assets/OAVA-Flame/VFX/Only_smoke.vfx",graphPath,true);
        else if(!AssetDatabase.CopyAsset("Assets/OAVA-Flame/VFX/Only_smoke.vfx",graphPath))throw new Exception("Could not copy smoke graph");
        AssetDatabase.ImportAsset(graphPath,ImportAssetOptions.ForceSynchronousImport);
        var rt=TypeNamed("UnityEditor.VFX.VisualEffectResource");
        var resource=rt.GetMethod("GetResourceAtPath",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{graphPath});
        var ext=TypeNamed("UnityEditor.VFX.VisualEffectResourceExtensions");
        var graph=ext.GetMethod("GetGraph",BindingFlags.Static|BindingFlags.Public).Invoke(null,new[]{resource});
        var nodes=List(graph,"children");
        object spawn=nodes.Single(n=>Id(n)==8926484042661614849L),init=nodes.Single(n=>Id(n)==8926484042661614914L),update=nodes.Single(n=>Id(n)==8926484042661614865L),output=nodes.Single(n=>Id(n)==8926484042661614968L);
        foreach(var n in nodes.Where(IsContext).Where(n=>n!=spawn&&n!=init&&n!=update&&n!=output)) Call(graph,"RemoveChild",n);
        Clear(spawn);Clear(init);Clear(update);
        foreach(var b in List(output,"children").Where(b=>b.GetType().Name!="Orient"))Call(output,"RemoveChild",b);
        var parameters=new Dictionary<string,object>();
        Action<object,string,Type,object> bind=(block,name,type,value)=>
        {
            string exposedName=name.TrimStart('_');
            if(!parameters.TryGetValue(exposedName,out var parameter))
            {
                parameter=Create("UnityEditor.VFX.VFXParameter"); Call(parameter,"Init",type);
                Setting(parameter,"m_ExposedName",exposedName);Setting(parameter,"m_Exposed",true);
                Set(List(parameter,"outputSlots")[0],"value",value);
                Call(graph,"AddChild",parameter);parameters.Add(exposedName,parameter);
            }
            var input=List(block,"inputSlots").First(s=>(string)Get(Get(s,"property"),"name")==name);
            Call(input,"Link",List(parameter,"outputSlots")[0]);
        };
        var rate=Create("UnityEditor.VFX.VFXSpawnerConstantRate");Call(spawn,"AddChild",rate);
        var rateParam=Create("UnityEditor.VFX.VFXParameter");Call(rateParam,"Init",typeof(float));Setting(rateParam,"m_ExposedName","EmissionRate");Setting(rateParam,"m_Exposed",true);Set(List(rateParam,"outputSlots")[0],"value",0f);Call(graph,"AddChild",rateParam);Call(List(rate,"inputSlots")[0],"Link",List(rateParam,"outputSlots")[0]);
        object AddCode(object context,string file,string label)
        {
            var block=Create("UnityEditor.VFX.Block.CustomHLSL");
            Setting(block,"m_HLSLCode",File.ReadAllText(file));Setting(block,"m_BlockName",label);
            Call(context,"AddChild",block);
            // The custom block only discovers its signature after it belongs to a graph.
            Call(block,"ResyncDependencies");
            if(List(block,"inputSlots").Count==0)throw new Exception("Custom HLSL signature did not create input slots: "+label);
            return block;
        }
        var initCode=AddCode(init,"Assets/VFX/ParticleRoomSmokeGeometryInitialize.hlsl","Spawn on real fire surface");
        var updateCode=AddCode(update,"Assets/VFX/ParticleRoomSmokeGeometryUpdate.hlsl","Real collider distance field, sliding and buoyancy");
        foreach(var b in new[]{initCode,updateCode})
            foreach(var s in List(b,"inputSlots"))
            {
                var property=Get(s,"property");var name=(string)Get(property,"name");var type=(Type)Get(property,"type");
                object value=type==typeof(Vector3)?(object)Vector3.zero:typeof(Texture).IsAssignableFrom(type)?null:(object)0f;
                string plain=name.TrimStart('_');
                if(plain=="ParticleLifetime")value=48f;if(plain=="RiseSpeed")value=1.1f;if(plain=="CeilingSpeed")value=0.55f;if(plain=="TurbulenceStrength")value=0.18f;if(plain=="SmokeOpacity")value=0.24f;if(plain=="StepSeconds")value=1f/60f;
                if(plain=="SpreadAngle")value=32f;if(plain=="LateralSpread")value=0.3f;
                if(plain=="StartSize")value=0.35f;if(plain=="EndSize")value=1.05f;if(plain=="GrowthSeconds")value=7f;
                if(plain=="SmokeColor")value=new Vector3(0.32f,0.33f,0.34f);
                if(plain=="MixingStrength")value=1f;if(plain=="WallReturnSpeed")value=0.4f;
                if(plain=="WallAvoidanceDistance")value=0.45f;if(plain=="MaxLayerDepth")value=1.65f;
                if(plain=="CollisionRadius")value=0.08f;if(plain=="FieldVoxel")value=0.12f;
                bind(b,name,type,value);
            }
        var data=Call(init,"GetData");
        Setting(data,"capacity",8192u);
        var space=Get(init,"space");Set(init,"space",Enum.Parse(space.GetType(),"World"));
        var integration=Call(update,"GetSettingValue","integration");Setting(update,"integration",Enum.Parse(integration.GetType(),"None"));
        var boundsMode=Call(data,"GetSettingValue","boundsMode");Setting(data,"boundsMode",Enum.Parse(boundsMode.GetType(),"Automatic"));
        Call(graph,"UpdateSubAssets");Call(resource,"WriteAsset");
        AssetDatabase.ImportAsset(graphPath,ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("Latest geometry smoke graph updated");
    }
}
