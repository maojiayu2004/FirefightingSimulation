using Normal.Realtime;
using UnityEngine;

/// <summary>
/// Carries the fire extinguisher and its socket on the avatar's hip, facing where the body faces, so they
/// stay with the player when they physically turn, crouch or walk.
///
/// The avatar root only follows the XR Origin, and the XR Origin does not rotate when the player turns
/// their body (there is no smooth or snap turn), so anything fixed to the avatar root stays behind as the
/// player turns away from it. This transform is the hip instead: it follows the VRIK body, the pelvis bone
/// for position and the VRIK root for heading. VRIK only turns its root once the head is more than Max Root
/// Angle away from it, so glancing around does not swing the extinguisher.
///
/// Each carried object keeps the pose it has relative to the hip, but only while it is under the parent it
/// started with. Grabbing unparents the extinguisher (XRGrabInteractable), so a held extinguisher is left
/// alone; dropping puts it back (Retain Transform Parent), and from then on it is carried from wherever it
/// was let go. The carried objects come from the base avatar prefab, which a prefab variant cannot
/// reparent, so they are moved here rather than parented to the hip.
///
/// While a hand is reaching for a carried object, the heading holds still, otherwise turning the head to
/// look at the extinguisher would rotate the body and move it away from the hand.
///
/// Only the client that owns the avatar moves things. Everyone else sees the extinguisher where its
/// RealtimeTransform puts it.
///
/// Runs after VRIK solves in LateUpdate, so it uses this frame's body pose.
/// </summary>
[DefaultExecutionOrder(10000)]
public class HipAnchor : MonoBehaviour
{
    [Tooltip("Avatar pelvis bone. The hip sits on it.")]
    [SerializeField] private Transform pelvis;

    [Tooltip("VRIK root. The hip faces its horizontal forward direction.")]
    [SerializeField] private Transform bodyRoot;

    [Tooltip("Objects carried on the hip (the extinguisher and its socket). Each keeps its pose relative to this transform.")]
    [SerializeField] private Transform[] carried;

    [Tooltip("The avatar's hands. While either is within Reach Distance of a carried object, the heading holds still.")]
    [SerializeField] private Transform[] hands;

    [Tooltip("Hand distance (meters) from a carried object that counts as reaching for it.")]
    [SerializeField] private float reachDistance = 0.4f;

    [Tooltip("Fastest the hip turns (degrees per second), so it eases round after holding still for a reach instead of snapping.")]
    [SerializeField] private float maxTurnSpeed = 360f;

    private struct CarriedState
    {
        public Transform parent;
        public bool attached;
        public Vector3 localPosition;
        public Quaternion localRotation;
    }

    private CarriedState[] _carried = new CarriedState[0];
    private RealtimeView _avatarView;

    private void Awake()
    {
        _avatarView = GetComponentInParent<RealtimeView>();

        if (carried == null)
            return;
        _carried = new CarriedState[carried.Length];
        for (int i = 0; i < carried.Length; i++)
        {
            if (carried[i] == null)
                continue;
            _carried[i].parent = carried[i].parent;
            Attach(i);
        }
    }

    private void LateUpdate()
    {
        if (pelvis == null || bodyRoot == null || (_avatarView != null && _avatarView.isOwnedRemotelyInHierarchy))
            return;

        Quaternion heading = transform.rotation;
        Vector3 forward = Vector3.ProjectOnPlane(bodyRoot.forward, Vector3.up);
        if (forward.sqrMagnitude > 0.0001f && !IsReaching())
            heading = Quaternion.RotateTowards(heading, Quaternion.LookRotation(forward, Vector3.up), maxTurnSpeed * Time.deltaTime);

        transform.SetPositionAndRotation(pelvis.position, heading);

        for (int i = 0; i < _carried.Length; i++)
        {
            Transform item = carried[i];
            if (item == null)
                continue;

            if (item.parent != _carried[i].parent)
            {
                _carried[i].attached = false;
                continue;
            }

            // Just put back (dropped): carry it from where it is now.
            if (!_carried[i].attached)
                Attach(i);

            item.SetPositionAndRotation(transform.TransformPoint(_carried[i].localPosition), transform.rotation * _carried[i].localRotation);
        }
    }

    private void Attach(int i)
    {
        _carried[i].attached = true;
        _carried[i].localPosition = transform.InverseTransformPoint(carried[i].position);
        _carried[i].localRotation = Quaternion.Inverse(transform.rotation) * carried[i].rotation;
    }

    private bool IsReaching()
    {
        if (hands == null)
            return false;

        float reachSqr = reachDistance * reachDistance;
        for (int i = 0; i < _carried.Length; i++)
        {
            if (!_carried[i].attached || carried[i] == null)
                continue;
            foreach (var hand in hands)
            {
                if (hand != null && (hand.position - carried[i].position).sqrMagnitude < reachSqr)
                    return true;
            }
        }

        return false;
    }
}
