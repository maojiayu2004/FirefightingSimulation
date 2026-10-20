using RootMotion.FinalIK;
using UnityEngine;

/// <summary>
/// Puts the avatar on its knees, and down on all fours, when the player gets low, instead of VRIK's
/// standing crouch (feet flat and knees bent, which is all VRIK can do with only a head and two hands).
///
/// There is no leg tracking, so kneeling is inferred from head height: once the head is below Kneel Height
/// above the floor, the player is almost certainly kneeling or crawling rather than holding a squat. The
/// knees then go down on the floor under the hips, the shins lie flat behind, and the hips sit at thigh
/// height, as far behind the head as the torso needs to reach it. Kneeling upright the hips are under the
/// head; the lower the head goes, the further the body leans forward, until it is flat on all fours. The
/// arms still follow the hands, so hands on the floor make the crawl.
///
/// This only drives VRIK's pelvis and leg targets, blended in by weight, so standing VRIK (procedural
/// stepping included) is unchanged above Kneel Height. It runs on every client from the synced head, so
/// remote players see the same pose without any extra networking.
///
/// Body proportions are measured from the bind pose in Awake. Targets are set in VRIK's OnPreUpdate, after
/// RealtimeAvatar has moved the head target for this frame.
/// </summary>
[RequireComponent(typeof(VRIK))]
public class KneelCrawlPose : MonoBehaviour
{
    [Header("When (meters of head height above the floor)")]
    [Tooltip("Head below this kneels.")]
    [SerializeField] private float kneelHeight = 1.0f;
    [Tooltip("Head back above this stands up again. Higher than Kneel Height so the pose does not flicker.")]
    [SerializeField] private float standHeight = 1.1f;
    [Tooltip("Seconds to blend between standing and kneeling.")]
    [SerializeField] private float blendTime = 0.3f;

    [Header("Pose (meters)")]
    [Tooltip("Height of the knee joints, shins and toes above the floor while kneeling.")]
    [SerializeField] private float kneeHeight = 0.05f;
    [Tooltip("Lowest the head may go above the floor while kneeling. VRIK's own Min Head Height applies when standing.")]
    [SerializeField] private float minHeadHeight = 0.3f;

    private VRIK _ik;
    private Transform _pelvisTarget;
    private Transform _leftToeTarget;
    private Transform _rightToeTarget;
    private Transform _leftKneeGoal;
    private Transform _rightKneeGoal;

    private float _thigh;
    private float _shin;
    private Vector3 _leftToeFromAnkle;
    private Vector3 _rightToeFromAnkle;
    private float _torso;
    private Vector3 _leftHipInPelvis;
    private Vector3 _rightHipInPelvis;
    private Quaternion _pelvisRelativeToRoot;
    private Quaternion _leftToeRelativeToRoot;
    private Quaternion _rightToeRelativeToRoot;
    private float _standingMinHeadHeight;

    private bool _kneeling;
    private float _weight;

    /// <summary>0 standing (plain VRIK) to 1 fully kneeling.</summary>
    public float Weight => _weight;

    private void Awake()
    {
        _ik = GetComponent<VRIK>();
        var r = _ik.references;

        // The leg targets are the toes when the rig has toe bones, otherwise the feet.
        Transform leftToe = r.leftToes != null ? r.leftToes : r.leftFoot;
        Transform rightToe = r.rightToes != null ? r.rightToes : r.rightFoot;
        Quaternion rootInverse = Quaternion.Inverse(transform.rotation);
        Quaternion pelvisInverse = Quaternion.Inverse(r.pelvis.rotation);

        _thigh = Vector3.Distance(r.leftThigh.position, r.leftCalf.position);
        _shin = Vector3.Distance(r.leftCalf.position, r.leftFoot.position);
        _leftToeFromAnkle = rootInverse * (leftToe.position - r.leftFoot.position);
        _rightToeFromAnkle = rootInverse * (rightToe.position - r.rightFoot.position);
        _torso = Vector3.Distance(Vector3.Lerp(r.leftThigh.position, r.rightThigh.position, 0.5f), r.head.position);
        _leftHipInPelvis = pelvisInverse * (r.leftThigh.position - r.pelvis.position);
        _rightHipInPelvis = pelvisInverse * (r.rightThigh.position - r.pelvis.position);
        _pelvisRelativeToRoot = rootInverse * r.pelvis.rotation;
        _leftToeRelativeToRoot = rootInverse * leftToe.rotation;
        _rightToeRelativeToRoot = rootInverse * rightToe.rotation;
        _standingMinHeadHeight = _ik.solver.spine.minHeadHeight;

        Transform parent = transform.parent != null ? transform.parent : transform;
        _pelvisTarget = CreateTarget("Kneel Pelvis Target", parent);
        _leftToeTarget = CreateTarget("Kneel Left Toe Target", parent);
        _rightToeTarget = CreateTarget("Kneel Right Toe Target", parent);
        _leftKneeGoal = CreateTarget("Kneel Left Knee Goal", parent);
        _rightKneeGoal = CreateTarget("Kneel Right Knee Goal", parent);

        var solver = _ik.solver;
        solver.spine.pelvisTarget = _pelvisTarget;
        solver.leftLeg.target = _leftToeTarget;
        solver.leftLeg.bendGoal = _leftKneeGoal;
        solver.rightLeg.target = _rightToeTarget;
        solver.rightLeg.bendGoal = _rightKneeGoal;
        SetWeight(0f);
    }

    private void OnEnable() => _ik.solver.OnPreUpdate += UpdatePose;

    private void OnDisable()
    {
        _ik.solver.OnPreUpdate -= UpdatePose;
        _kneeling = false;
        SetWeight(0f);
    }

    private void UpdatePose()
    {
        Transform head = _ik.solver.spine.headTarget;
        if (head == null)
            return;

        float floor = transform.position.y;
        float headHeight = head.position.y - floor;
        _kneeling = _kneeling ? headHeight < standHeight : headHeight < kneelHeight;
        SetWeight(Mathf.MoveTowards(_weight, _kneeling ? 1f : 0f, blendTime > 0f ? Time.deltaTime / blendTime : 1f));
        if (_weight <= 0f)
            return;

        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f)
            return;
        forward.Normalize();
        Quaternion heading = Quaternion.LookRotation(forward, Vector3.up);

        // Hip joints at thigh height, behind the head by however far the torso needs to lean to reach it,
        // with the pelvis tilted forward by the same lean.
        float hipHeight = kneeHeight + _thigh;
        float rise = Mathf.Max(headHeight - hipHeight, 0f);
        float back = Mathf.Sqrt(Mathf.Max(_torso * _torso - rise * rise, 0f));
        float lean = Mathf.Atan2(back, rise) * Mathf.Rad2Deg;
        Quaternion pelvisRotation = heading * Quaternion.AngleAxis(lean, Vector3.right) * _pelvisRelativeToRoot;
        Vector3 hips = new Vector3(head.position.x, floor + hipHeight, head.position.z) - forward * back;
        Vector3 pelvis = hips - pelvisRotation * Vector3.Lerp(_leftHipInPelvis, _rightHipInPelvis, 0.5f);
        _pelvisTarget.SetPositionAndRotation(pelvis, pelvisRotation);

        // Feet stretched back in line with the shins: toes pointing behind, soles up.
        Quaternion footFlip = heading * Quaternion.AngleAxis(180f, Vector3.right);
        PlaceLeg(_leftToeTarget, _leftKneeGoal, pelvis + pelvisRotation * _leftHipInPelvis, footFlip, _leftToeFromAnkle, _leftToeRelativeToRoot, floor, forward);
        PlaceLeg(_rightToeTarget, _rightKneeGoal, pelvis + pelvisRotation * _rightHipInPelvis, footFlip, _rightToeFromAnkle, _rightToeRelativeToRoot, floor, forward);
    }

    /// <summary>Knee on the floor straight under the hip joint, shin flat behind it, foot stretched back from the ankle.</summary>
    private void PlaceLeg(Transform toeTarget, Transform kneeGoal, Vector3 hipJoint, Quaternion footFlip,
        Vector3 toeFromAnkle, Quaternion toeRelativeToRoot, float floor, Vector3 forward)
    {
        Vector3 knee = new Vector3(hipJoint.x, floor + kneeHeight, hipJoint.z);
        Vector3 ankle = knee - forward * _shin;
        toeTarget.SetPositionAndRotation(ankle + footFlip * toeFromAnkle, footFlip * toeRelativeToRoot);
        kneeGoal.position = knee + forward * _thigh;
    }

    private void SetWeight(float weight)
    {
        _weight = weight;
        var solver = _ik.solver;
        solver.spine.pelvisPositionWeight = weight;
        solver.spine.pelvisRotationWeight = weight;
        solver.leftLeg.positionWeight = weight;
        solver.leftLeg.rotationWeight = weight;
        solver.leftLeg.bendGoalWeight = weight;
        solver.rightLeg.positionWeight = weight;
        solver.rightLeg.rotationWeight = weight;
        solver.rightLeg.bendGoalWeight = weight;
        solver.spine.minHeadHeight = Mathf.Lerp(_standingMinHeadHeight, minHeadHeight, weight);
    }

    private static Transform CreateTarget(string name, Transform parent)
    {
        var target = new GameObject(name).transform;
        target.SetParent(parent, false);
        return target;
    }
}
