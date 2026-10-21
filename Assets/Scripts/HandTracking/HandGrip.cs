using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Whether a tracked hand is closed or clearly open, shared by the extinguisher holster grab and the spray gesture
/// so that "close your hand" and "open your hand" mean the same thing everywhere.
///
///  Closed: a fist (index, middle and ring curled) or a pinch (thumb and index tips together), whichever comes
///  naturally. Hysteresis makes it easier to stay closed than to close, so a relaxed grip does not flicker open.
///  Loosely closed: a looser version for spraying, where triggering should be too easy rather than hard: a pinch
///  with the tips a little apart, or the fingers curled on average (a loose grab around the hose), not a tight fist.
///  Clearly open: index, middle and ring all mostly straight and the thumb away from the index. Anything in
///  between counts as still holding, which keeps a carried object in a relaxed hand.
///
/// Joint poses from XR Hands are in tracking space, i.e. relative to the XR Origin's Camera Offset.
/// </summary>
public static class HandGrip
{
    // Finger straightness: knuckle-to-tip distance / finger length, about 1 straight and 0.5 in a fist.
    private const float k_FistEnter = 0.65f;
    private const float k_FistExit = 0.8f;
    private const float k_OpenFinger = 0.85f;

    // Average of index, middle and ring straightness for a loose grab.
    private const float k_LooseGrabEnter = 0.8f;
    private const float k_LooseGrabExit = 0.88f;

    // Thumb tip to index tip, in meters.
    private const float k_PinchEnter = 0.025f;
    private const float k_PinchExit = 0.05f;
    private const float k_LoosePinchEnter = 0.035f;
    private const float k_LoosePinchExit = 0.06f;
    private const float k_OpenThumb = 0.04f;

    private static readonly List<XRHandSubsystem> s_Subsystems = new List<XRHandSubsystem>();

    /// <summary>The running hand subsystem, or null when hand tracking is not running.</summary>
    public static XRHandSubsystem GetSubsystem()
    {
        SubsystemManager.GetSubsystems(s_Subsystems);
        foreach (var subsystem in s_Subsystems)
        {
            if (subsystem.running)
                return subsystem;
        }

        return null;
    }

    /// <summary>Fist or pinch. Pass the previous result so a closed hand stays closed until it clearly loosens.</summary>
    public static bool IsClosed(XRHand hand, bool wasClosed)
    {
        float fist = wasClosed ? k_FistExit : k_FistEnter;
        float pinch = wasClosed ? k_PinchExit : k_PinchEnter;
        return (Straightness(hand, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip) <= fist &&
                Straightness(hand, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip) <= fist &&
                Straightness(hand, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip) <= fist) ||
               ThumbToIndex(hand) <= pinch;
    }

    /// <summary>A loose pinch or a loose grab. Pass the previous result so it stays closed until the hand clearly loosens.</summary>
    public static bool IsLooselyClosed(XRHand hand, bool wasClosed)
    {
        float grab = wasClosed ? k_LooseGrabExit : k_LooseGrabEnter;
        float pinch = wasClosed ? k_LoosePinchExit : k_LoosePinchEnter;
        float curl = (Straightness(hand, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip) +
                      Straightness(hand, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip) +
                      Straightness(hand, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip)) / 3f;
        return curl <= grab || ThumbToIndex(hand) <= pinch;
    }

    /// <summary>Fingers mostly straight and the thumb away from the index: a deliberate let-go, not a relaxed grip.</summary>
    public static bool IsOpen(XRHand hand)
    {
        return Straightness(hand, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip) >= k_OpenFinger &&
               Straightness(hand, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip) >= k_OpenFinger &&
               Straightness(hand, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip) >= k_OpenFinger &&
               ThumbToIndex(hand) > k_OpenThumb;
    }

    /// <summary>World position of the palm, given the tracking space (the XR Origin's Camera Offset).</summary>
    public static bool TryGetPalm(XRHand hand, Transform trackingSpace, out Vector3 position) =>
        TryGetJoint(hand, XRHandJointID.Palm, trackingSpace, out position);

    /// <summary>World position of a joint, given the tracking space (the XR Origin's Camera Offset).</summary>
    public static bool TryGetJoint(XRHand hand, XRHandJointID id, Transform trackingSpace, out Vector3 position)
    {
        bool ok = TryGetPosition(hand, id, out position);
        if (ok && trackingSpace != null)
            position = trackingSpace.TransformPoint(position);
        return ok;
    }

    private static float Straightness(XRHand hand, XRHandJointID proximal, XRHandJointID intermediate, XRHandJointID distal, XRHandJointID tip)
    {
        if (!TryGetPosition(hand, proximal, out Vector3 p) ||
            !TryGetPosition(hand, intermediate, out Vector3 i) ||
            !TryGetPosition(hand, distal, out Vector3 d) ||
            !TryGetPosition(hand, tip, out Vector3 t))
            return 1f;

        float length = Vector3.Distance(p, i) + Vector3.Distance(i, d) + Vector3.Distance(d, t);
        return length > 0f ? Vector3.Distance(p, t) / length : 1f;
    }

    private static float ThumbToIndex(XRHand hand)
    {
        return TryGetPosition(hand, XRHandJointID.ThumbTip, out Vector3 thumb) && TryGetPosition(hand, XRHandJointID.IndexTip, out Vector3 index)
            ? Vector3.Distance(thumb, index)
            : float.MaxValue;
    }

    private static bool TryGetPosition(XRHand hand, XRHandJointID id, out Vector3 position)
    {
        bool ok = hand.GetJoint(id).TryGetPose(out Pose pose);
        position = pose.position;
        return ok;
    }
}
