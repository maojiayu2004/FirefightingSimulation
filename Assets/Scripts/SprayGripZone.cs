using UnityEngine;

/// <summary>
/// Where the free hand has to be to spray a held sprayer by hand tracking (see <see cref="HandSprayGesture"/>):
/// within Range of any of the sprayer's colliders (the can and its hose), so anywhere around the can counts, whichever
/// way it is turned in the hand. Deliberately generous: spraying should be too easy rather than a struggle.
///
/// Leave Parts empty to use every collider on the sprayer and its children. Distances are to the colliders' surfaces,
/// so Range is how far off the can or hose a hand may be. Select the sprayer to see the zone as a gizmo.
/// </summary>
public class SprayGripZone : MonoBehaviour
{
    [Tooltip("The colliders the hand may be near. Leave empty to use every collider on the sprayer and its children.")]
    [SerializeField] private Collider[] parts;

    [Tooltip("How far off the can or hose (meters) a hand may be and still spray.")]
    [SerializeField] private float range = 0.25f;

    private Collider[] _allColliders;

    /// <summary>How far off the can or hose (meters) a hand may be and still spray.</summary>
    public float Range => range;

    private Collider[] Parts
    {
        get
        {
            if (parts != null && parts.Length > 0)
                return parts;
            return _allColliders ??= GetComponentsInChildren<Collider>(true);
        }
    }

    /// <summary>Distance (meters) from a world-space point to the nearest part's surface: 0 on or inside it.</summary>
    public float DistanceTo(Vector3 point)
    {
        float distance = float.MaxValue;
        foreach (var part in Parts)
        {
            if (part != null && part.enabled && part.gameObject.activeInHierarchy)
                distance = Mathf.Min(distance, Vector3.Distance(point, part.ClosestPoint(point)));
        }

        return distance;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.8f);
        foreach (var part in Parts)
        {
            if (part != null)
                Gizmos.DrawWireCube(part.bounds.center, part.bounds.size + Vector3.one * (2f * range));
        }
    }
}
