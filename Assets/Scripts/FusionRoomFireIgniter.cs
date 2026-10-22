using System.Collections;
using Ignis;
using UnityEngine;

/// <summary>Lights the fusion scene's per-room fires after Play starts. Smoke emitters read these same objects.</summary>
public sealed class FusionRoomFireIgniter : MonoBehaviour
{
    public FlammableObject[] fires;
    public float igniteAtSeconds = 2f;

    void Start()
    {
        StartCoroutine(Ignite());
    }

    IEnumerator Ignite()
    {
        yield return new WaitForSeconds(igniteAtSeconds);
        if (fires == null) yield break;
        foreach (var fire in fires)
        {
            if (fire == null) continue;
            fire.SetOnFireFromCenterFromNetwork();
        }
    }
}
