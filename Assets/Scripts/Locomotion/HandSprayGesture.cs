using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Hand-tracking replacement for the controller trigger on sprayers (fire extinguisher / hose).
///
/// Hold the sprayer with one hand, then pinch or grab with the other, free hand anywhere near it to spray; open the
/// hand, or take it away, to stop. This component is the activate input of the holding hand's Near-Far interactor,
/// so XRI raises the same activated / deactivated events the controller trigger does and <see cref="Spray"/> (and
/// its network sync) work unchanged.
///
/// Built to be too easy rather than a struggle:
///  - Near: within the sprayer's <see cref="SprayGripZone"/> (by default 25 cm of the can or its hose, any side),
///    measured from whichever of the palm and fingertips is closest. A sprayer without one gets one.
///  - Pinch or grab: the Quest's own pinch (the free hand's select input), or a loose pinch or loose grab read from
///    the hand joints (<see cref="HandGrip.IsLooselyClosed"/>), whichever sees it first.
///  - Steady: once spraying, the hand may drift to 1.3x the zone, and the spray only stops after Stop Delay of
///    the hand being open or away, or Lost Tracking Grace of it being lost (two hands close together confuse
///    tracking).
/// A hand away from the sprayer does not spray, so working a real radio with the free hand does not set it off.
///
/// While a sprayer is held, the free hand's interactors are blocked from hovering and selecting, so its gestures do
/// not also act on whatever is around the sprayer. (Taking the sprayer itself is also refused by
/// <see cref="HolsterGrab"/>'s select filter.) <see cref="IsSprayingWith"/> tells the walking stick which hand is
/// spraying, so moving it around does not walk.
///
/// One instance per hand, under that hand's object (built by the locomotion scene builder).
/// Joint poses from XR Hands are in tracking space, the XR Origin's Camera Offset.
/// </summary>
[DefaultExecutionOrder(XRInteractionUpdateOrder.k_XRInputDeviceButtonReader)]
public class HandSprayGesture : MonoBehaviour, IXRInputButtonReader
{
    [Tooltip("Hand that holds the sprayer. The opposite hand, pinching or grabbing near it, sprays.")]
    [SerializeField] private Handedness holdingHand = Handedness.Left;

    [Tooltip("The holding hand's Near-Far interactor. Its activate input is replaced by this gesture.")]
    [SerializeField] private XRBaseInputInteractor holdingInteractor;

    [Tooltip("The free hand's interactors (Near-Far, Poke), blocked while a sprayer is held. The Near-Far one's select input is the Quest's pinch.")]
    [SerializeField] private XRBaseInteractor[] freeHandInteractors;

    [Header("Steadiness (seconds)")]
    [Tooltip("How long the hand must be open or away before the spray stops.")]
    [SerializeField] private float stopDelay = 0.2f;

    [Tooltip("How long the hand may be lost by tracking, while spraying, before the spray stops.")]
    [SerializeField] private float lostTrackingGrace = 0.5f;

    // The free hand points measured to the sprayer: whichever is closest counts.
    private static readonly XRHandJointID[] s_HandPoints =
        { XRHandJointID.Palm, XRHandJointID.ThumbTip, XRHandJointID.IndexTip, XRHandJointID.MiddleTip, XRHandJointID.RingTip };

    // Indexed by Handedness: which hands are currently spraying.
    private static readonly bool[] s_Spraying = new bool[3];

    private Transform _trackingSpace;
    private XRBaseInputInteractor _freeHandSelect;
    private bool _isPerformed;
    private bool _wasPerformedThisFrame;
    private bool _wasCompletedThisFrame;
    private bool _blockingFreeHand;
    private bool _freeHandClosed;
    private float _stopFor;

    private Handedness FreeHand => holdingHand == Handedness.Left ? Handedness.Right : Handedness.Left;

    /// <summary>Whether this hand is the free hand on a held sprayer, spraying.</summary>
    public static bool IsSprayingWith(Handedness hand) => s_Spraying[(int)hand];

    private void Awake()
    {
        var origin = GetComponentInParent<XROrigin>();
        if (origin != null)
            _trackingSpace = origin.CameraFloorOffsetObject.transform;

        if (freeHandInteractors != null)
        {
            foreach (var interactor in freeHandInteractors)
            {
                if (interactor is NearFarInteractor nearFar)
                    _freeHandSelect = nearFar;
            }
        }

        if (holdingInteractor == null)
            return;
        holdingInteractor.activateInput.inputSourceMode = XRInputButtonReader.InputSourceMode.ObjectReference;
        holdingInteractor.activateInput.SetObjectReference(this);
    }

    private void OnDisable()
    {
        SetPerformed(false);
        BlockFreeHand(false);
        s_Spraying[(int)FreeHand] = false;
    }

    private void Update()
    {
        Spray sprayer = HeldSprayer();

        // Re-assert every frame: HandTeleportGesture also toggles these flags when the free hand aims.
        if (sprayer != null)
            BlockFreeHand(true);
        else if (_blockingFreeHand)
            BlockFreeHand(false);

        bool spraying = sprayer != null && UpdateSpraying(sprayer);
        if (!spraying)
            _stopFor = 0f;
        SetPerformed(spraying);
        s_Spraying[(int)FreeHand] = spraying;
    }

    private Spray HeldSprayer()
    {
        if (holdingInteractor == null || !holdingInteractor.isActiveAndEnabled || !holdingInteractor.hasSelection)
            return null;
        var held = holdingInteractor.firstInteractableSelected;
        return held != null ? held.transform.GetComponentInParent<Spray>() : null;
    }

    /// <summary>
    /// Whether to spray this frame: on as soon as the free hand pinches or grabs near the sprayer, off only once it
    /// has been open or away for Stop Delay (or lost by tracking for Lost Tracking Grace).
    /// </summary>
    private bool UpdateSpraying(Spray sprayer)
    {
        var subsystem = HandGrip.GetSubsystem();
        XRHand freeHand = default;
        if (subsystem != null)
            freeHand = holdingHand == Handedness.Left ? subsystem.rightHand : subsystem.leftHand;

        if (subsystem == null || !freeHand.isTracked)
        {
            _freeHandClosed = false;
            _stopFor += Time.deltaTime;
            return _isPerformed && _stopFor < lostTrackingGrace;
        }

        _freeHandClosed = HandGrip.IsLooselyClosed(freeHand, _freeHandClosed);
        bool pinchOrGrab = _freeHandClosed || (_freeHandSelect != null && _freeHandSelect.selectInput.ReadIsPerformed());

        var zone = sprayer.GetComponent<SprayGripZone>();
        if (zone == null)
            zone = sprayer.gameObject.AddComponent<SprayGripZone>();
        bool near = DistanceToSprayer(zone, freeHand) < zone.Range * (_isPerformed ? 1.3f : 1f);

        if (pinchOrGrab && near)
        {
            _stopFor = 0f;
            return true;
        }

        _stopFor += Time.deltaTime;
        return _isPerformed && _stopFor < stopDelay;
    }

    /// <summary>Distance from the closest of the free hand's palm and fingertips to the sprayer.</summary>
    private float DistanceToSprayer(SprayGripZone zone, XRHand hand)
    {
        float best = float.MaxValue;
        foreach (var id in s_HandPoints)
        {
            if (HandGrip.TryGetJoint(hand, id, _trackingSpace, out Vector3 point))
                best = Mathf.Min(best, zone.DistanceTo(point));
        }

        return best;
    }

    private void SetPerformed(bool performed)
    {
        _wasPerformedThisFrame = !_isPerformed && performed;
        _wasCompletedThisFrame = _isPerformed && !performed;
        _isPerformed = performed;
    }

    private void BlockFreeHand(bool block)
    {
        _blockingFreeHand = block;
        if (freeHandInteractors == null)
            return;
        foreach (var interactor in freeHandInteractors)
        {
            if (interactor == null)
                continue;
            interactor.allowHover = !block;
            interactor.allowSelect = !block;
        }
    }

    public bool ReadIsPerformed() => _isPerformed;
    public bool ReadWasPerformedThisFrame() => _wasPerformedThisFrame;
    public bool ReadWasCompletedThisFrame() => _wasCompletedThisFrame;
    public float ReadValue() => _isPerformed ? 1f : 0f;

    public bool TryReadValue(out float value)
    {
        value = ReadValue();
        return _isPerformed;
    }
}
