using System.Collections.Generic;
using Normal.Realtime;
using RootMotion.FinalIK;
using Unity.XR.CoreUtils;
using UnityEngine;

/// <summary>
/// Hides your own avatar's head from your own eyes, so its face, mask and helmet never clip into the view (walking,
/// leaning, looking down), while other players, and you in a mirror, still see it.
///
/// The avatar is one skinned mesh, so on the local player's avatar only it is split once in two:
///  - The head: every triangle with a vertex skinned mostly to the head bone or its children, and every part
///    (material) that is mostly such triangles, so the whole helmet goes, brim included. It moves to a second
///    renderer with the same bones, on the Head Layer, which the player's own camera stops rendering. Mirrors and
///    other cameras still render that layer.
///  - The body: everything else, left on the original renderer, seen by every camera.
/// Other players' copies of this avatar are never split, so everyone else sees the head as normal.
///
/// The mesh must be readable (Read/Write enabled in the model's import settings).
/// </summary>
[RequireComponent(typeof(VRIK))]
public class FirstPersonHeadHider : MonoBehaviour
{
    [Tooltip("The avatar's skinned mesh. Left empty, the first one under this object.")]
    [SerializeField] private SkinnedMeshRenderer body;

    [Tooltip("Layer the head is moved to. The player's own camera stops rendering it; mirrors still do.")]
    [SerializeField] private string headLayer = "LocalPlayerHead";

    [Tooltip("A vertex is part of the head when at least this much of its skin weight is on the head bone or its children.")]
    [Range(0f, 1f)] [SerializeField] private float headWeight = 0.5f;

    private RealtimeView _avatarView;

    private void Awake()
    {
        _avatarView = GetComponentInParent<RealtimeView>();
        if (body == null)
            body = GetComponentInChildren<SkinnedMeshRenderer>();
    }

    // Waits for ownership: the avatar is only known to be this player's own once Normcore has assigned it.
    private void Update()
    {
        if (_avatarView != null && !_avatarView.isOwnedLocallyInHierarchy)
        {
            if (_avatarView.isOwnedRemotelyInHierarchy)
                enabled = false;
            return;
        }

        enabled = false;
        HideHead();
    }

    private void HideHead()
    {
        Transform headBone = GetComponent<VRIK>().references.head;
        int layer = LayerMask.NameToLayer(headLayer);
        var origin = FindAnyObjectByType<XROrigin>();
        Mesh mesh = body != null ? body.sharedMesh : null;
        if (headBone == null || layer < 0 || origin == null || origin.Camera == null || mesh == null || !mesh.isReadable)
        {
            Debug.LogWarning($"{nameof(FirstPersonHeadHider)}: cannot hide the head (needs the head bone, a '{headLayer}' layer, an XR Origin camera and a readable mesh).", this);
            return;
        }

        bool[] isHeadVertex = FindHeadVertices(mesh, body.bones, headBone);
        var bodyMesh = Instantiate(mesh);
        var headMesh = Instantiate(mesh);
        bodyMesh.name = mesh.name + " (no head)";
        headMesh.name = mesh.name + " (head)";

        var triangles = new List<int>();
        var bodyTriangles = new List<int>();
        var headTriangles = new List<int>();
        for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
        {
            mesh.GetTriangles(triangles, subMesh);
            int headCount = 0;
            for (int i = 0; i < triangles.Count; i += 3)
            {
                if (IsHeadTriangle(isHeadVertex, triangles, i))
                    headCount++;
            }

            bool wholePartIsHead = headCount * 2 > triangles.Count / 3;
            bodyTriangles.Clear();
            headTriangles.Clear();
            for (int i = 0; i < triangles.Count; i += 3)
            {
                var side = wholePartIsHead || IsHeadTriangle(isHeadVertex, triangles, i) ? headTriangles : bodyTriangles;
                side.Add(triangles[i]);
                side.Add(triangles[i + 1]);
                side.Add(triangles[i + 2]);
            }

            bodyMesh.SetTriangles(bodyTriangles, subMesh, false);
            headMesh.SetTriangles(headTriangles, subMesh, false);
        }

        var head = new GameObject(body.name + " (Head)").AddComponent<SkinnedMeshRenderer>();
        head.gameObject.layer = layer;
        head.transform.SetParent(body.transform.parent, false);
        head.transform.SetLocalPositionAndRotation(body.transform.localPosition, body.transform.localRotation);
        head.transform.localScale = body.transform.localScale;
        head.sharedMesh = headMesh;
        head.bones = body.bones;
        head.rootBone = body.rootBone;
        head.sharedMaterials = body.sharedMaterials;
        head.localBounds = body.localBounds;
        head.updateWhenOffscreen = body.updateWhenOffscreen;
        head.quality = body.quality;
        head.shadowCastingMode = body.shadowCastingMode;
        head.receiveShadows = body.receiveShadows;

        body.sharedMesh = bodyMesh;
        origin.Camera.cullingMask &= ~(1 << layer);
    }

    private static bool IsHeadTriangle(bool[] isHeadVertex, List<int> triangles, int i) =>
        isHeadVertex[triangles[i]] || isHeadVertex[triangles[i + 1]] || isHeadVertex[triangles[i + 2]];

    /// <summary>Per vertex, whether at least Head Weight of its skinning is on the head bone or its children.</summary>
    private bool[] FindHeadVertices(Mesh mesh, Transform[] bones, Transform headBone)
    {
        var isHeadBone = new bool[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            isHeadBone[i] = bones[i] != null && bones[i].IsChildOf(headBone);

        var bonesPerVertex = mesh.GetBonesPerVertex();
        var weights = mesh.GetAllBoneWeights();
        var isHeadVertex = new bool[mesh.vertexCount];
        int w = 0;
        for (int vertex = 0; vertex < bonesPerVertex.Length; vertex++)
        {
            float onHead = 0f;
            for (int b = 0; b < bonesPerVertex[vertex]; b++, w++)
            {
                if (isHeadBone[weights[w].boneIndex])
                    onHead += weights[w].weight;
            }

            isHeadVertex[vertex] = onHead >= headWeight;
        }

        return isHeadVertex;
    }
}
