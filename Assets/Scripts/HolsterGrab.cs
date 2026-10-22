using System.Collections.Generic;
using Normal.Realtime;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Lets the player take the extinguisher out of its hip holster by reaching to it and closing their hand (a fist or
/// a pinch, whichever comes naturally, see <see cref="HandGrip"/>), and keeps it in that hand until they clearly open
/// it. Letting go puts it straight back in the holster (see <see cref="BodyAnchor"/>), so it is never lost.
///
/// Built for hand tracking used by people new to VR:
///  - Grab zone: a sphere of Zone Radius around the top of the holstered extinguisher, so a rough reach is enough.
///    The extinguisher lights up while an open hand is in the zone, to show it can be taken.
///  - Grab: an open hand has to close in the zone (or within Close Window of arriving there) while moving slowly.
///    A walking-stick fist swinging past the hip does not take it: it was never open, a fist that flickers open or
///    comes back after a tracking dropout is not open for long enough, and the swing is too fast.
///  - Hold: sticky. A relaxed grip keeps it, and short tracking dropouts are ignored. It is let go only after the
///    hand has been clearly open for Release Time, or lost by tracking for Lost Tracking Grace.
///  - Only the holding hand can have it: this is a select filter on the extinguisher that turns away every other
///    grab, so the free hand pinching or grabbing near it to spray (HandSprayGesture) cannot pull it across.
/// Controllers work too: grip in the zone takes it, releasing grip lets go.
///
/// The grab is an XRI manual interaction on that hand's Near-Far Interactor, so NetworkedGrabInteractable takes
/// ownership and Spray / HandSprayGesture work as with any grab. Only the client that owns the avatar grabs; on
/// everyone else's copy of the avatar the filter turns all grabs away.
/// </summary>
public class HolsterGrab : MonoBehaviour, IXRSelectFilter
{
    [Tooltip("The holstered extinguisher.")]
    [SerializeField] private XRGrabInteractable extinguisher;

    [Header("Grab Zone")]
    [Tooltip("Centre of the grab zone, in the extinguisher's own space while it is holstered. The default is the top of the can.")]
    [SerializeField] private Vector3 zoneOffset = new Vector3(0f, 0.27f, 0f);

    [Tooltip("Radius of the grab zone in meters.")]
    [SerializeField] private float zoneRadius = 0.18f;

    [Tooltip("Seconds a hand may have been closed before reaching the zone and still grab, since people close their hand as they arrive.")]
    [SerializeField] private float closeWindow = 0.35f;

    [Tooltip("Seconds the hand must have been open, shortly before it closes, for the close to grab. Stops a walking-stick fist that flickers open, or that tracking loses and finds again, from grabbing.")]
    [SerializeField] private float openBeforeGrab = 0.25f;

    [Tooltip("Fastest the hand may be moving relative to the head when it grabs, in m/s. A reaching hand slows down to grab, while a walking-stick swing passes the hip at speed.")]
    [SerializeField] private float maxGrabSpeed = 0.8f;

    [Header("Hold")]
    [Tooltip("Seconds the holding hand must be clearly open before the extinguisher goes back in the holster.")]
    [SerializeField] private float releaseTime = 0.25f;

    [Tooltip("Seconds the holding hand may be lost by tracking before the extinguisher goes back in the holster.")]
    [SerializeField] private float lostTrackingGrace = 1.5f;

    [Header("Highlight")]
    [Tooltip("Tint applied to the extinguisher while an open hand is in the grab zone.")]
    [SerializeField] private Color highlightColor = new Color(1f, 0.8f, 0.2f);

    private static readonly List<IXRInteractor> s_Interactors = new List<IXRInteractor>();
    private static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int s_Color = Shader.PropertyToID("_Color");

    // Longest a hand may take to close once it stops being open, for that open hand to count towards a grab.
    private const float k_ClosingTime = 1f;

    // Seconds to smooth the hand speed over, against tracking jitter.
    private const float k_SpeedSmoothing = 0.08f;

    private struct HandState
    {
        public bool isClosed;
        public float closedAt;
        public bool openedFirst;

        public bool isOpen;
        public float openSince;
        public float openFor;
        public float openUntil;

        public bool hasOffset;
        public Vector3 offset;
        public float speed;
    }

    private RealtimeView _avatarView;
    private Transform _trackingSpace;
    private Transform _head;
    private Renderer[] _renderers;
    private MaterialPropertyBlock _highlightBlock;
    private bool _highlighted;

    private HandState _left;
    private HandState _right;

    private NearFarInteractor _holder;
    private bool _holderIsHand;
    private float _openFor;
    private float _lostFor;

    public bool canProcess => isActiveAndEnabled;

    private void Awake()
    {
        _avatarView = GetComponentInParent<RealtimeView>();
        if (extinguisher != null)
            _renderers = extinguisher.GetComponentsInChildren<Renderer>(true);
        _highlightBlock = new MaterialPropertyBlock();
        _highlightBlock.SetColor(s_BaseColor, highlightColor);
        _highlightBlock.SetColor(s_Color, highlightColor);
    }

    private void OnEnable()
    {
        if (extinguisher != null)
            extinguisher.selectFilters.Add(this);
    }

    private void OnDisable()
    {
        if (extinguisher != null)
            extinguisher.selectFilters.Remove(this);
        SetHighlight(false);
    }

    /// <summary>Select filter: only the hand this component handed the extinguisher to may hold it.</summary>
    public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable interactable)
    {
        return _holder != null && interactor == (IXRSelectInteractor)_holder;
    }

    private void Update()
    {
        if (extinguisher == null || extinguisher.interactionManager == null || (_avatarView != null && _avatarView.isOwnedRemotelyInHierarchy))
            return;

        if (_trackingSpace == null)
        {
            var origin = FindAnyObjectByType<XROrigin>();
            if (origin != null)
            {
                _trackingSpace = origin.CameraFloorOffsetObject.transform;
                _head = origin.Camera != null ? origin.Camera.transform : null;
            }
        }

        var subsystem = HandGrip.GetSubsystem();
        if (_holder != null)
        {
            SetHighlight(false);
            _left = default;
            _right = default;
            UpdateHold(subsystem);
            return;
        }

        if (extinguisher.isSelected)
        {
            SetHighlight(false);
            _left = default;
            _right = default;
            return;
        }

        Vector3 zone = extinguisher.transform.TransformPoint(zoneOffset);
        bool handInZone = false;
        if (TryHandGrab(subsystem, InteractorHandedness.Left, ref _left, zone, ref handInZone) ||
            TryHandGrab(subsystem, InteractorHandedness.Right, ref _right, zone, ref handInZone) ||
            TryControllerGrab(subsystem, zone, ref handInZone))
        {
            SetHighlight(false);
            return;
        }

        SetHighlight(handInZone);
    }

    private bool TryHandGrab(XRHandSubsystem subsystem, InteractorHandedness side, ref HandState state, Vector3 zone, ref bool handInZone)
    {
        if (subsystem == null || !TryGetHand(subsystem, side, out XRHand hand) || !HandGrip.TryGetPalm(hand, _trackingSpace, out Vector3 palm))
        {
            // Lost: whatever the hand is doing when tracking finds it again, it has not been seen open yet.
            state = default;
            return false;
        }

        UpdateSpeed(ref state, palm);
        UpdateOpenAndClosed(hand, ref state);

        bool inZone = (palm - zone).sqrMagnitude < zoneRadius * zoneRadius;
        if (!inZone)
            return false;
        if (!state.isClosed)
        {
            handInZone = true;
            return false;
        }

        if (!state.openedFirst || Time.time - state.closedAt > closeWindow || state.speed > maxGrabSpeed ||
            !TryFindInteractor(side, palm, out NearFarInteractor interactor))
            return false;

        Grab(interactor, true);
        return true;
    }

    /// <summary>
    /// Tracks when the hand is open and when it closes, and whether it was open for Open Before Grab just before it
    /// closed. Open is a properly unclenched hand (not even loosely closed), so a loose walking-stick fist is not open.
    /// </summary>
    private void UpdateOpenAndClosed(XRHand hand, ref HandState state)
    {
        float now = Time.time;
        bool open = !HandGrip.IsLooselyClosed(hand, false);
        if (open && !state.isOpen)
        {
            state.openSince = now;
        }
        else if (!open && state.isOpen)
        {
            state.openFor = now - state.openSince;
            state.openUntil = now;
        }
        state.isOpen = open;

        bool closed = HandGrip.IsClosed(hand, state.isClosed);
        if (closed && !state.isClosed)
        {
            state.closedAt = now;
            state.openedFirst = state.openFor >= openBeforeGrab && now - state.openUntil <= k_ClosingTime;
        }
        state.isClosed = closed;
    }

    /// <summary>
    /// Smoothed palm speed relative to the head, so the rig moving (walking-stick locomotion) or the player physically
    /// walking does not count. It starts from zero when tracking finds the hand, but Open Before Grab means a hand has
    /// been tracked for longer than the smoothing takes to catch up before it can grab.
    /// </summary>
    private void UpdateSpeed(ref HandState state, Vector3 palm)
    {
        Vector3 offset = _head != null ? palm - _head.position : palm;
        float deltaTime = Time.deltaTime;
        if (state.hasOffset && deltaTime > 0f)
            state.speed = Mathf.Lerp(state.speed, (offset - state.offset).magnitude / deltaTime, 1f - Mathf.Exp(-deltaTime / k_SpeedSmoothing));
        state.offset = offset;
        state.hasOffset = true;
    }

    private bool TryControllerGrab(XRHandSubsystem subsystem, Vector3 zone, ref bool handInZone)
    {
        extinguisher.interactionManager.GetRegisteredInteractors(s_Interactors);
        foreach (var registered in s_Interactors)
        {
            // Tracked hands are handled by TryHandGrab; this is for controllers.
            if (!(registered is NearFarInteractor interactor) || !interactor.isActiveAndEnabled || interactor.hasSelection ||
                (subsystem != null && TryGetHand(subsystem, interactor.handedness, out _)))
                continue;

            if ((GrabPoint(interactor) - zone).sqrMagnitude >= zoneRadius * zoneRadius)
                continue;
            handInZone = true;

            if (interactor.logicalSelectState.wasPerformedThisFrame)
            {
                Grab(interactor, false);
                return true;
            }
        }

        return false;
    }

    private void UpdateHold(XRHandSubsystem subsystem)
    {
        if (!extinguisher.isSelected || !_holder.IsSelecting(extinguisher))
        {
            // Lost some other way (the interactor was disabled, say).
            _holder = null;
            return;
        }

        if (!_holderIsHand)
        {
            if (!_holder.selectInput.ReadIsPerformed())
                Release();
            return;
        }

        if (subsystem == null || !TryGetHand(subsystem, _holder.handedness, out XRHand hand))
        {
            _openFor = 0f;
            _lostFor += Time.deltaTime;
            if (_lostFor >= lostTrackingGrace)
                Release();
            return;
        }

        _lostFor = 0f;
        _openFor = HandGrip.IsOpen(hand) ? _openFor + Time.deltaTime : 0f;
        if (_openFor >= releaseTime)
            Release();
    }

    private void Grab(NearFarInteractor interactor, bool isHand)
    {
        // Set before selecting: the select filter only lets the holder through.
        _holder = interactor;
        _holderIsHand = isHand;
        _openFor = 0f;
        _lostFor = 0f;
        interactor.StartManualInteraction((IXRSelectInteractable)extinguisher);
    }

    private void Release()
    {
        var holder = _holder;
        _holder = null;
        if (holder != null && holder.isPerformingManualInteraction)
            holder.EndManualInteraction();
    }

    /// <summary>The tracked hand's Near-Far Interactor: the empty one of that hand whose grab point is nearest the palm.</summary>
    private bool TryFindInteractor(InteractorHandedness side, Vector3 palm, out NearFarInteractor found)
    {
        found = null;
        float best = 0.3f * 0.3f;
        extinguisher.interactionManager.GetRegisteredInteractors(s_Interactors);
        foreach (var registered in s_Interactors)
        {
            if (registered is NearFarInteractor interactor && interactor.isActiveAndEnabled && !interactor.hasSelection &&
                interactor.handedness == side)
            {
                float distance = (GrabPoint(interactor) - palm).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    found = interactor;
                }
            }
        }

        return found != null;
    }

    private static bool TryGetHand(XRHandSubsystem subsystem, InteractorHandedness side, out XRHand hand)
    {
        hand = side == InteractorHandedness.Left ? subsystem.leftHand : subsystem.rightHand;
        return side != InteractorHandedness.None && hand.isTracked;
    }

    // The near caster's raw origin: the Pinch Grab Pose for a tracked hand, the controller for a controller.
    // (A hand's Near-Far Interactor object itself does not move with the hand; only its casters do.)
    private static Vector3 GrabPoint(NearFarInteractor interactor)
    {
        Transform origin = interactor.nearInteractionCaster?.castOrigin;
        return origin != null ? origin.position : interactor.transform.position;
    }

    private void SetHighlight(bool on)
    {
        if (on == _highlighted || _renderers == null)
            return;
        _highlighted = on;
        foreach (var r in _renderers)
        {
            if (r != null && !(r is ParticleSystemRenderer))
                r.SetPropertyBlock(on ? _highlightBlock : null);
        }
    }
}
