using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class ThermalTemperatureDisplay : MonoBehaviour
{
    private const float AmbientCelsius = 21f;
    private const float MaximumDisplayCelsius = 999f;

    [SerializeField, Min(1f)] private float maximumDistance = 50f;
    [SerializeField] private LayerMask measurementLayers = ~0;
    [SerializeField, Min(0.02f)] private float refreshInterval = 0.1f;

    private Camera thermalCamera;
    private GameObject overlay;
    private Text temperatureLabel;
    private Text modeLabel;
    private float nextRefresh;

    private void Awake()
    {
        thermalCamera = GetComponent<Camera>();
        CreateOverlay();
        RefreshReading();
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextRefresh)
            RefreshReading();
    }

    private void RefreshReading()
    {
        nextRefresh = Time.unscaledTime + refreshInterval;
        if (temperatureLabel == null)
            return;

        float celsius = MeasureCenterPoint();
        temperatureLabel.text = $"{Mathf.RoundToInt(Mathf.Clamp(celsius, 0f, MaximumDisplayCelsius)):000} \u00B0C";
    }

    private float MeasureCenterPoint()
    {
        Ray ray = thermalCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f));
        if (!Physics.Raycast(ray, out RaycastHit hit, maximumDistance, measurementLayers,
                QueryTriggerInteraction.Ignore))
        {
            return AmbientCelsius;
        }

        HazardTemperature heat = hit.collider.GetComponentInParent<HazardTemperature>();
        if (heat == null)
            return AmbientCelsius;

        return heat.EstimatedCelsius;
    }

    private void CreateOverlay()
    {
        overlay = new GameObject("TIC Overlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        overlay.transform.SetParent(transform, false);
        overlay.layer = gameObject.layer;

        Canvas canvas = overlay.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = thermalCamera;
        canvas.planeDistance = Mathf.Max(thermalCamera.nearClipPlane + 0.01f, 0.1f);
        canvas.sortingOrder = 1000;

        CanvasScaler scaler = overlay.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1024f, 1024f);

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        modeLabel = CreateText("Mode", font, 25, TextAnchor.UpperLeft);
        modeLabel.text = "TI BASIC  |  WHITE HOT";
        RectTransform modeRect = modeLabel.rectTransform;
        modeRect.anchorMin = modeRect.anchorMax = modeRect.pivot = new Vector2(0f, 1f);
        modeRect.anchoredPosition = new Vector2(25f, -22f);
        modeRect.sizeDelta = new Vector2(360f, 48f);

        temperatureLabel = CreateText("Temperature", font, 44, TextAnchor.LowerRight);
        RectTransform temperatureRect = temperatureLabel.rectTransform;
        temperatureRect.anchorMin = new Vector2(1f, 0f);
        temperatureRect.anchorMax = new Vector2(1f, 0f);
        temperatureRect.pivot = new Vector2(1f, 0f);
        temperatureRect.anchoredPosition = new Vector2(-28f, 24f);
        temperatureRect.sizeDelta = new Vector2(300f, 64f);

        Text reticle = CreateText("Reticle", font, 34, TextAnchor.MiddleCenter);
        reticle.text = "+";
        RectTransform reticleRect = reticle.rectTransform;
        reticleRect.anchorMin = reticleRect.anchorMax = reticleRect.pivot = new Vector2(0.5f, 0.5f);
        reticleRect.sizeDelta = new Vector2(64f, 64f);
    }

    private Text CreateText(string objectName, Font font, int fontSize, TextAnchor alignment)
    {
        GameObject textObject = new GameObject(
            objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Outline));
        textObject.transform.SetParent(overlay.transform, false);
        textObject.layer = overlay.layer;

        Text text = textObject.GetComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.fontStyle = FontStyle.Bold;
        text.alignment = alignment;
        text.color = Color.white;
        text.raycastTarget = false;

        Outline outline = textObject.GetComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
        outline.effectDistance = new Vector2(2f, -2f);
        return text;
    }

    private void OnDestroy()
    {
        if (overlay != null)
            Destroy(overlay);
    }

}
