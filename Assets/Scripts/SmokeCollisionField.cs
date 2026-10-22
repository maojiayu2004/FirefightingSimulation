using UnityEngine;
using UnityEngine.VFX;

/// <summary>GPU-queryable unsigned distance + outward-from-surface direction, baked from actual collider triangles.
/// The texture coverage is a lookup domain, never a container: outside it there is no collision.</summary>
public sealed class SmokeCollisionField : MonoBehaviour
{
    public Texture3D texture;
    public Vector3 minimum;
    public Vector3 size;
    public float voxelSize=0.12f;
    public int triangleCount;
    public void Bind(VisualEffect effect)
    {
        if(texture==null || effect==null)return;
        effect.SetTexture("SceneField",texture);
        effect.SetVector3("FieldMin",minimum);
        effect.SetVector3("FieldSize",size);
        effect.SetFloat("FieldVoxel",voxelSize);
    }
    void OnDrawGizmosSelected()
    {
        Gizmos.color=Color.magenta;Gizmos.DrawWireCube(minimum+size*0.5f,size);
    }
}
