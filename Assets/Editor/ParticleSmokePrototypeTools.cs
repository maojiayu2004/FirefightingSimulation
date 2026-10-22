using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Command bridge and shared hierarchy formatting for the current geometry smoke tools.
[InitializeOnLoad]
public static class ParticleSmokePrototypeTools
{
    static double nextPoll;
    static ParticleSmokePrototypeTools(){EditorApplication.update+=Poll;}
    static void Poll()
    {
        if(EditorApplication.timeSinceStartup<nextPoll || EditorApplication.isCompiling || EditorApplication.isUpdating)return;
        nextPoll=EditorApplication.timeSinceStartup+2;
        string path=Path.GetFullPath("../particle-smoke-command.txt");
        if(!File.Exists(path))return;
        string command=File.ReadAllText(path).Trim();File.Delete(path);
        try{
            if(command=="refresh")AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            else if(command=="stop")EditorApplication.isPlaying=false;
            else if(command=="fusion-run")SmokeFusionExperiment.Run();
            else if(command=="fusion-open")SmokeFusionExperiment.Open();
            else if(command=="fusion-build")SmokeFusionExperiment.Build();
            else if(command=="fusion-preview")SmokeFusionExperiment.Preview();
            else if(command=="fusion-seed")FusionRoomSmokeSetup.Seed();
            else if(command.StartsWith("geometry"))SmokeGeometryTools.Command(command);
        }catch(Exception error){Debug.LogException(error);}
    }
    public static string Hierarchy(Transform t)=>t.parent==null?t.name:Hierarchy(t.parent)+"/"+t.name;
}
