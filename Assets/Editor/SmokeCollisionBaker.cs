using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class SmokeCollisionBaker
{
    const float Band=0.8f,Bin=0.6f,Voxel=0.12f;
    public const string AssetPath="Assets/SCENARIOS/ParticleSmokePrototype/SmokeGeometryField.asset";
    public static SmokeCollisionField Bake(Transform house, string assetPath = AssetPath, string resultsDirectory = null)
    {
        string results = resultsDirectory ?? SmokeGeometryTools.Results;
        Physics.SyncTransforms();var vertices=new List<Vector4>();var report=new List<string>();
        Bounds coverage=new Bounds();bool first=true;
        foreach(var collider in house.GetComponentsInChildren<Collider>())
        {
            if(!collider.enabled || collider.isTrigger || collider.name=="Hitbox")continue;
            if(first){coverage=collider.bounds;first=false;}else coverage.Encapsulate(collider.bounds);
            int old=vertices.Count;
            if(collider is MeshCollider mesh && mesh.sharedMesh!=null)
            {
                var v=mesh.sharedMesh.vertices;var indices=mesh.sharedMesh.triangles;
                for(int i=0;i<indices.Length;i+=3)Add(vertices,mesh.transform.TransformPoint(v[indices[i]]),mesh.transform.TransformPoint(v[indices[i+1]]),mesh.transform.TransformPoint(v[indices[i+2]]));
            }
            else if(collider is BoxCollider box)
            {
                Vector3[] v=new Vector3[8];for(int i=0;i<8;i++)v[i]=box.transform.TransformPoint(box.center+Vector3.Scale(box.size*.5f,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1)));
                int[] faces={0,2,3,0,3,1,4,5,7,4,7,6,0,1,5,0,5,4,2,6,7,2,7,3,0,4,6,0,6,2,1,3,7,1,7,5};
                for(int i=0;i<faces.Length;i+=3)Add(vertices,v[faces[i]],v[faces[i+1]],v[faces[i+2]]);
            }
            else report.Add("UNSUPPORTED_COLLIDER "+ParticleSmokePrototypeTools.Hierarchy(collider.transform)+" "+collider.GetType().Name);
            report.Add("BAKED "+ParticleSmokePrototypeTools.Hierarchy(collider.transform)+" triangles="+(vertices.Count-old)/3+" bounds="+collider.bounds);
        }
        if(vertices.Count==0)throw new Exception("No real collision triangles");
        // This is a finite texture lookup domain, not a collision surface or movement cage.
        Directory.CreateDirectory(results);File.WriteAllLines(Path.Combine(results,"baked-colliders.txt"),report);
        // Limit storage to the architectural meshes, not decorative ground planes extending outside the house.
        var architecture=house.GetComponentsInChildren<MeshCollider>().Where(c=>c.enabled && (c.name=="Walls" || c.name=="Floors" || c.name=="Ceilings")).ToArray();
        if(architecture.Length>0){coverage=architecture[0].bounds;foreach(var c in architecture)coverage.Encapsulate(c.bounds);}
        coverage.Expand(6);Vector3 min=coverage.min;
        var dims=new Vector3Int(Mathf.CeilToInt(coverage.size.x/Voxel),Mathf.CeilToInt(coverage.size.y/Voxel),Mathf.CeilToInt(coverage.size.z/Voxel));
        Vector3 size=(Vector3)dims*Voxel;
        var bins=new Vector3Int(Mathf.CeilToInt(size.x/Bin),Mathf.CeilToInt(size.y/Bin),Mathf.CeilToInt(size.z/Bin));
        var buckets=new List<int>[bins.x*bins.y*bins.z];
        for(int tri=0;tri<vertices.Count/3;tri++)
        {
            Vector3 a=vertices[tri*3],b=vertices[tri*3+1],c=vertices[tri*3+2];
            Vector3 low=Vector3.Min(a,Vector3.Min(b,c))-Vector3.one*Band,high=Vector3.Max(a,Vector3.Max(b,c))+Vector3.one*Band;
            var start=Cell(low-min,bins);var end=Cell(high-min,bins);
            for(int z=start.z;z<=end.z;z++)for(int y=start.y;y<=end.y;y++)for(int x=start.x;x<=end.x;x++)
            {int index=x+bins.x*(y+bins.y*z);(buckets[index]??(buckets[index]=new List<int>())).Add(tri);}
        }
        var ranges=new Vector2Int[buckets.Length];var all=new List<int>();
        for(int i=0;i<buckets.Length;i++){ranges[i]=new Vector2Int(all.Count,buckets[i]?.Count??0);if(buckets[i]!=null)all.AddRange(buckets[i]);}
        var compute=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/VFX/SmokeColliderBake.compute");
        var render=new RenderTexture(dims.x,dims.y,0,RenderTextureFormat.ARGBHalf){dimension=TextureDimension.Tex3D,volumeDepth=dims.z,enableRandomWrite=true};render.Create();
        using(var vb=new ComputeBuffer(vertices.Count,16))using(var rb=new ComputeBuffer(ranges.Length,8))using(var ib=new ComputeBuffer(Mathf.Max(1,all.Count),4))
        {
            try
            {
                vb.SetData(vertices);rb.SetData(ranges);ib.SetData(all.ToArray());
                compute.SetBuffer(0,"Vertices",vb);compute.SetBuffer(0,"Ranges",rb);compute.SetBuffer(0,"Indices",ib);compute.SetTexture(0,"Field",render);
                compute.SetVector("FieldMin",min);compute.SetFloat("VoxelSize",Voxel);compute.SetFloat("BinSize",Bin);compute.SetFloat("MaxDistance",Band);
                compute.SetInts("Dimensions",dims.x,dims.y,dims.z);compute.SetInts("BinDimensions",bins.x,bins.y,bins.z);
                compute.Dispatch(0,(dims.x+3)/4,(dims.y+3)/4,(dims.z+3)/4);
                var request=AsyncGPUReadback.Request(render,0,TextureFormat.RGBAHalf);request.WaitForCompletion();
                if(request.hasError)throw new Exception("GPU field readback failed");
                var texture=new Texture3D(dims.x,dims.y,dims.z,TextureFormat.RGBAHalf,false){name="Real collider unsigned distance and normal",filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};
                var raw=new byte[request.layerDataSize*request.layerCount];
                for(int layer=0;layer<request.layerCount;layer++)request.GetData<byte>(layer).ToArray().CopyTo(raw,layer*request.layerDataSize);
                texture.SetPixelData(raw,0);texture.Apply(false,false);
                if(File.Exists(assetPath)){var prior=AssetDatabase.LoadAssetAtPath<Texture3D>(assetPath);EditorUtility.CopySerialized(texture,prior);UnityEngine.Object.DestroyImmediate(texture);texture=prior;}
                else AssetDatabase.CreateAsset(texture,assetPath);
                var field=UnityEngine.Object.FindFirstObjectByType<SmokeCollisionField>()??new GameObject("Smoke real geometry field (lookup only)").AddComponent<SmokeCollisionField>();
                field.texture=texture;field.minimum=min;field.size=size;field.voxelSize=Voxel;field.triangleCount=vertices.Count/3;
                report.Insert(0,"FIELD dimensions="+dims+" triangles="+field.triangleCount+" binIndices="+all.Count+" voxel="+Voxel+" outside=free (no containment)");
                Directory.CreateDirectory(results);File.WriteAllLines(Path.Combine(results,"baked-colliders.txt"),report);
                AssetDatabase.SaveAssets();return field;
            }
            finally{render.Release();UnityEngine.Object.DestroyImmediate(render);}
        }
    }
    static Vector3Int Cell(Vector3 p,Vector3Int d)=>new Vector3Int(Mathf.Clamp(Mathf.FloorToInt(p.x/Bin),0,d.x-1),Mathf.Clamp(Mathf.FloorToInt(p.y/Bin),0,d.y-1),Mathf.Clamp(Mathf.FloorToInt(p.z/Bin),0,d.z-1));
    static void Add(List<Vector4> v,Vector3 a,Vector3 b,Vector3 c)
    {if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-12f)return;v.Add(a);v.Add(b);v.Add(c);}
}
