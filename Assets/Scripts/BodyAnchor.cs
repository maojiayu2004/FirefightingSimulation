using Normal.Realtime;
using UnityEngine;

/// <summary>
/// Holsters the fire extinguisher and its socket on the avatar's body, at the front of the right hip, and keeps them
/// there when the player physically turns, crouches, kneels or crawls. <see cref="HolsterGrab"/> takes the
/// extinguisher out when the player reaches for it.
///
/// The avatar root only follows the XR Origin, and the XR Origin does not rotate when the player turns their
/// body (there is no smooth or snap turn), so anything fixed to the avatar root stays behind as the player
/// turns away from it. This transform follows the VRIK body instead. It sits on the Mount bone (the pelvis),
/// faces where the VRIK root faces, and leans with the torso, the line from the pelvis up to the neck. VRIK only
/// turns its root once the head is more than Max Root Angle away from it, so glancing around does not swing it.
///
/// Carried objects stay where they are placed in the avatar prefab, relative to the mount: to move the
/// extinguisher, move it (and its socket) in Prefab Mode and save. In Awake, before VRIK moves anything, this
/// transform lines itself up with the bones and records each object's offset, so the prefab's rest pose is what
/// counts. Grabbing unparents the extinguisher (XRGrabInteractable), so a held extinguisher is left alone; letting
/// go puts it back under its parent (Retain Transform Parent), and with it straight back in the holster. The
/// carried objects come from the base avatar prefab, which a prefab variant cannot reparent, so they are moved here
/// rather than parented to this transform.
///
/// Only the client that owns the avatar moves things. Everyone else sees the extinguisher where its
/// RealtimeTransform puts it.
///
/// Runs after VRIK solves in LateUpdate, so it uses this frame's body pose.
/// </summary>
[DefaultExecutionOrder(10000)]
public class BodyAnchor : MonoBehaviour
{
    [Tooltip("Bone the anchor sits on. The pelvis, for a hip holster.")]
    [SerializeField] private Transform mount;

    [Tooltip("Avatar pelvis. With the neck, gives the torso's lean.")]
    [SerializeField] private Transform pelvis;

    [Tooltip("Avatar neck. The anchor leans with the torso, the line from the pelvis up to here.")]
    [SerializeField] private Transform neck;

    [Tooltip("VRIK root. The anchor faces where it faces.")]
    [SerializeField] private Transform bodyRoot;

    [Tooltip("Objects carried on the body (the extinguisher and its socket). Each stays where it is placed in the prefab, relative to the mount.")]
    [SerializeField] private Transform[] carried;

    private Transform[] _parents = new Transform[0];
    private Vector3[] _localPositions = new Vector3[0];
    private Quaternion[] _localRotations = new Quaternion[0];
    private RealtimeView _avatarView;

    private void Awake()
    {
        _avatarView = GetComponentInParent<RealtimeView>();

        if (TryGetBodyFrame(out Vector3 position, out Quaternion rotation))
            transform.SetPositionAndRotation(position, rotation);

        if (carried == null)
            return;
        _parents = new Transform[carried.Length];
        _localPositions = new Vector3[carried.Length];
        _localRotations = new Quaternion[carried.Length];
        for (int i = 0; i < carried.Length; i++)
        {
            if (carried[i] == null)
                continue;
            _parents[i] = carried[i].parent;
            _localPositions[i] = transform.InverseTransformPoint(carried[i].position);
            _localRotations[i] = Quaternion.Inverse(transform.rotation) * carried[i].rotation;
        }
    }

    private void LateUpdate()
    {
        if ((_avatarView != null && _avatarView.isOwnedRemotelyInHierarchy) || !TryGetBodyFrame(out Vector3 position, out Quaternion rotation))
            return;

        transform.SetPositionAndRotation(position, rotation);

        for (int i = 0; i < _parents.Length; i++)
        {
            if (IsHolstered(i))
                carried[i].SetPositionAndRotation(transform.TransformPoint(_localPositions[i]), rotation * _localRotations[i]);
        }
    }

    /// <summary>On the mount, facing where the body faces, leaning with the torso.</summary>
    private bool TryGetBodyFrame(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        if (mount == null || pelvis == null || neck == null || bodyRoot == null)
            return false;

        Vector3 up = neck.position - pelvis.position;
        up = up.sqrMagnitude > 0.0001f ? up.normalized : Vector3.up;
        Vector3 forward = Vector3.Cross(bodyRoot.right, up);
        if (forward.sqrMagnitude < 0.0001f)
            return false;

        position = mount.position;
        rotation = Quaternion.LookRotation(forward, up);
        return true;
    }

    /// <summary>Under its starting parent, i.e. not held (grabbing unparents it).</summary>
    private bool IsHolstered(int i) => carried[i] != null && carried[i].parent == _parents[i];
}
