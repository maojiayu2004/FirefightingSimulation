using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Hand-tracking replacement for the controller trigger on sprayers (fire extinguisher / hose).
///
/// Hold the sprayer with one hand, then pinch or make a fist with the other, free hand to spray, wherever that hand
/// is; open it to stop. This component is the activate input of the holding hand's Near-Far interactor, so XRI
/// raises the same activated / deactivated events the controller trigger does and <see cref="Spray"/> (and its
/// network sync) work unchanged.
///
///  - Pinch or fist: the Quest's own pinch (the free hand's select input), or a pinch or fist read from the hand
///    joints (<see cref="HandGrip.IsClosed"/>), whichever sees it first. The hand does not have to be near the
///    sprayer, so the two hands need not come together, which confuses hand tracking.
///  - Fresh: a free hand that is already closed when the sprayer is taken (walking with it, say) has to open once
///    before it sprays.
///  - Steady: the spray only stops after Stop Delay of the hand being open, or Lost Tracking Grace of it being lost.
///
/// While a sprayer is held, the free hand's interactors are blocked from hovering and selecting, so its gestures do
/// not also act on whatever is around it. (Taking the sprayer itself is also refused by <see cref="HolsterGrab"/>'s
/// select filter.) <see cref="IsSprayingWith"/> tells the walking stick which hand is spraying, so a spraying fist
/// does not also walk.
///
/// One instance per hand, under that hand's object (built by the locomotion scene builder).
/// </summary>
[DefaultExecutionOrder(XRInteractionUpdateOrder.k_XRInputDeviceButtonReader)]
public class HandSprayGesture : MonoBehaviour, IXRInputButtonReader
{
    [Tooltip("Hand that holds the sprayer. The opposite hand, pinching or making a fist, sprays.")]
    [SerializeField] private Handedness holdingHand = Handedness.Left;

    [Tooltip("The holding hand's Near-Far interactor. Its activate input is replaced by this gesture.")]
    [SerializeField] private XRBaseInputInteractor holdingInteractor;

    [Tooltip("The free hand's interactors (Near-Far, Poke), blocked while a sprayer is held. The Near-Far one's select input is the Quest's pinch.")]
    [SerializeField] private XRBaseInteractor[] freeHandInteractors;

    [Header("Steadiness (seconds)")]
    [Tooltip("How long the hand must be open before the spray stops.")]
    [SerializeField] private float stopDelay = 0.2f;

    [Tooltip("How long the hand may be lost by tracking, while spraying, before the spray stops.")]
    [SerializeField] private float lostTrackingGrace = 0.5f;

    // Indexed by Handedness: which hands are currently spraying.
    private static readonly bool[] s_Spraying = new bool[3];

    private XRBaseInputInteractor _freeHandSelect;
    private bool _isPerformed;
    private bool _wasPerformedThisFrame;
    private bool _wasCompletedThisFrame;
    private bool _blockingFreeHand;
    private bool _freeHandClosed;
    private bool _freeHandReady;
    private float _stopFor;

    private Handedness FreeHand => holdingHand == Handedness.Left ? Handedness.Right : Handedness.Left;

    /// <summary>Whether this hand is the free hand on a held sprayer, spraying.</summary>
    public static bool IsSprayingWith(Handedness hand) => s_Spraying[(int)hand];

    private void Awake()
    {
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
        bool holding = HeldSprayer() != null;

        // Re-assert every frame: HandTeleportGesture also toggles these flags when the free hand aims.
        if (holding)
            BlockFreeHand(true);
        else if (_blockingFreeHand)
            BlockFreeHand(false);

        if (!holding)
            _freeHandReady = false;

        bool spraying = holding && UpdateSpraying();
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
    /// Whether to spray this frame: on as soon as the free hand pinches or makes a fist, off only once it has been
    /// open for Stop Delay (or lost by tracking for Lost Tracking Grace).
    /// </summary>
    private bool UpdateSpraying()
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

        _freeHandClosed = HandGrip.IsClosed(freeHand, _freeHandClosed);
        bool pinchOrFist = _freeHandClosed || (_freeHandSelect != null && _freeHandSelect.selectInput.ReadIsPerformed());
        if (!pinchOrFist)
            _freeHandReady = true;

        if (pinchOrFist && _freeHandReady)
        {
            _stopFor = 0f;
            return true;
        }

        _stopFor += Time.deltaTime;
        return _isPerformed && _stopFor < stopDelay;
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
