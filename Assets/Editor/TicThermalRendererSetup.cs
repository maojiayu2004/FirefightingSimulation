#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[InitializeOnLoad]
internal static class TicThermalRendererSetup
{
    private const string RendererPath = "Assets/Resources/Materials/ThermalRendererData.asset";
    private const string FeatureName = "TIC Localized Surface Thermal Field";

    static TicThermalRendererSetup()
    {
        EditorApplication.delayCall += EnsureRendererFeature;
    }

    [MenuItem("Tools/TIC/Rebuild URP Thermal Renderer")]
    private static void EnsureRendererFeature()
    {
        UniversalRendererData rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
        if (rendererData == null)
        {
            Debug.LogError($"TIC renderer asset was not found at {RendererPath}.");
            return;
        }

        bool changed = false;
        foreach (ScriptableRendererFeature oldFeature in rendererData.rendererFeatures
                     .Where(feature => feature != null &&
                         (feature.name == "White Hot Scene Detail" || feature.name == "Thermal Hazard Overlay"))
                     .ToArray())
        {
            rendererData.rendererFeatures.Remove(oldFeature);
            Object.DestroyImmediate(oldFeature, true);
            changed = true;
        }

        TicThermalRendererFeature feature = rendererData.rendererFeatures
            .OfType<TicThermalRendererFeature>()
            .FirstOrDefault();
        if (feature == null)
        {
            feature = ScriptableObject.CreateInstance<TicThermalRendererFeature>();
            feature.name = FeatureName;
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            rendererData.rendererFeatures.Add(feature);
            changed = true;
        }
        else if (feature.name != FeatureName)
        {
            feature.name = FeatureName;
            changed = true;
        }

        RebuildFeatureMap(rendererData);
        // Always dirty the feature so newly introduced serialized presentation
        // settings are written into an older renderer-data subasset.
        EditorUtility.SetDirty(feature);
        EditorUtility.SetDirty(rendererData);
        AssetDatabase.SaveAssets();
        if (changed)
            Debug.Log("TIC renderer rebuilt with the localized URP surface thermal field.");
    }

    private static void RebuildFeatureMap(UniversalRendererData rendererData)
    {
        SerializedObject serializedRenderer = new(rendererData);
        SerializedProperty featureMap = serializedRenderer.FindProperty("m_RendererFeatureMap");
        featureMap.arraySize = rendererData.rendererFeatures.Count;

        for (int i = 0; i < rendererData.rendererFeatures.Count; i++)
        {
            ScriptableRendererFeature rendererFeature = rendererData.rendererFeatures[i];
            long localId = 0;
            if (rendererFeature != null)
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(rendererFeature, out _, out localId);

            featureMap.GetArrayElementAtIndex(i).longValue = localId;
        }

        serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
    }
}
#endif
