using UnityEditor;
using UnityEngine;
using Ignis;

[CustomEditor(typeof(ParticleRoomSmoke))]
public sealed class ParticleRoomSmokeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var smoke=(ParticleRoomSmoke)target;
        EditorGUILayout.HelpBox("One emitter reads one fire lifecycle. Room bounds are a closed box, not furniture or door collision. Emission is capped against the 4096-particle graph budget.",MessageType.Info);
        EditorGUILayout.LabelField("Budgeted emission / second",smoke.EffectiveRate.ToString("F1"));
        if(smoke.particlesPerSecond*smoke.density>smoke.EffectiveRate+0.01f)
            EditorGUILayout.HelpBox("Requested density exceeds lifetime budget and is clamped. Reduce lifetime or density.",MessageType.Warning);
        if(GUILayout.Button("Focus smoke origin and cone")) Focus(smoke);
        if(GUILayout.Button("Select associated fire") && smoke.source!=null) Selection.activeGameObject=smoke.source.gameObject;
    }
    public static void Focus(ParticleRoomSmoke smoke)
    {
        smoke.Configure();
        if(SceneView.lastActiveSceneView!=null)
            SceneView.lastActiveSceneView.Frame(new Bounds(smoke.SpawnCenter+Vector3.up,Vector3.one*3.5f),false);
    }
    [DrawGizmo(GizmoType.Selected|GizmoType.Active)]
    static void DrawFireLink(FlammableObject fire,GizmoType type)
    {
        foreach(var smoke in fire.GetComponentsInChildren<ParticleRoomSmoke>(true))
        {
            if(smoke.source!=fire)continue;
            smoke.Configure();Gizmos.color=Color.yellow;
            Gizmos.DrawLine(fire.transform.position,smoke.SpawnCenter);
            Gizmos.DrawWireSphere(smoke.SpawnCenter,0.14f);
            Handles.Label(smoke.SpawnCenter,smoke.name);
        }
    }
}
